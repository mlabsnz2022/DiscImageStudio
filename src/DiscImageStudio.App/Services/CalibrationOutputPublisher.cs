using System.IO;

namespace DiscImageStudio.Services;

/// <summary>
/// Publishes one completed output set, restoring the previous set on publication failure.
/// This is a recoverable multi-file transaction, not an atomic snapshot for concurrent readers.
/// </summary>
internal static class CalibrationOutputPublisher
{
    internal static void Publish(string stagingDirectory, string destinationDirectory)
    {
        string staging = NormalizeDirectory(stagingDirectory);
        string destination = NormalizeDirectory(destinationDirectory);
        if (string.Equals(staging, destination, StringComparison.OrdinalIgnoreCase)
            || IsWithin(destination, staging))
            throw new ArgumentException("Staging directory and output directory cannot be identical, nor can output reside inside staging.");
        if (!Directory.Exists(staging)) throw new DirectoryNotFoundException("Staging directory does not exist: " + staging);
        RejectReparsePoint(staging);
        if (Directory.Exists(destination)) RejectReparsePoint(destination);
        if (Directory.EnumerateDirectories(staging).Any())
            throw new IOException("Staging directory must only contain top-level output files for this job.");

        string[] sources = Directory.GetFiles(staging, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) throw new IOException("No calibration output files available to publish.");
        if (sources.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new IOException("Output files contain case-insensitive duplicate names and cannot be safely published.");

        // Resolve and validate the complete operation before moving any existing output.
        string backup = Path.Combine(destination, ".calibration-backup-" + Guid.NewGuid().ToString("N"));
        var files = new List<PublicationFile>(sources.Length);
        foreach (string source in sources)
        {
            RejectReparsePoint(source);
            string name = Path.GetFileName(source);
            string target = Path.GetFullPath(Path.Combine(destination, name));
            if (!IsWithin(target, destination) || string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output file path conflicts with staging path.");
            if (Directory.Exists(target)) throw new IOException("Target output path is already a directory: " + target);
            if (File.Exists(target)) RejectReparsePoint(target);
            files.Add(new(source, target, Path.Combine(backup, name)));
        }

        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(backup);
        try
        {
            // Back up the whole old set before replacing any member with a new file.
            foreach (PublicationFile file in files)
            {
                if (!File.Exists(file.Target)) continue;
                File.Move(file.Target, file.Backup);
                file.BackedUp = true;
            }
            foreach (PublicationFile file in files)
            {
                File.Move(file.Source, file.Target);
                file.Published = true;
            }
        }
        catch (Exception publicationError)
        {
            var rollbackErrors = new List<Exception>();
            for (int i = files.Count - 1; i >= 0; i--)
            {
                PublicationFile file = files[i];
                if (!file.Published) continue;
                try
                {
                    // Preserve generated content for retry. Never overwrite a file
                    // concurrently created in staging or delete an occupied new output.
                    File.Move(file.Target, file.Source);
                    file.Published = false;
                }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            for (int i = files.Count - 1; i >= 0; i--)
            {
                PublicationFile file = files[i];
                if (!file.BackedUp) continue;
                try
                {
                    File.Move(file.Backup, file.Target);
                    file.BackedUp = false;
                }
                catch (Exception error) { rollbackErrors.Add(error); }
            }
            if (rollbackErrors.Count > 0)
                throw new IOException("Failed to publish calibration files, some files could not be restored. Backup kept at " + backup
                    + "; please inspect that directory and resolve file locks.",
                    new AggregateException(new[] { publicationError }.Concat(rollbackErrors)));

            // A restored set is complete even if removing the empty recovery folder fails.
            TryRemoveEmptyDirectory(backup);
            throw;
        }

        // Commit point: every new file is installed. Cleanup must never trigger a
        // rollback after an older backup has already been deleted. Occupied backups
        // remain in this independent directory; the complete new set stays published.
        foreach (PublicationFile file in files)
        {
            if (!file.BackedUp) continue;
            try { File.Delete(file.Backup); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
        }
        TryRemoveEmptyDirectory(backup);
    }

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsWithin(string path, string directory)
        => path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Publish path cannot be a symbolic link or directory junction: " + path);
    }

    private static void TryRemoveEmptyDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class PublicationFile(string source, string target, string backup)
    {
        internal string Source { get; } = source;
        internal string Target { get; } = target;
        internal string Backup { get; } = backup;
        internal bool BackedUp { get; set; }
        internal bool Published { get; set; }
    }
}
