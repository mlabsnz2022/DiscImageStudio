using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscImageStudio.Core.Calibration;

public sealed record CalibrationSession(
    CalibrationTarget Target, List<CalibrationObservation> Observations,
    CalibrationFitOptions? FitOptions = null, bool CdInterleave = true,
    CalibrationPhotoSession? Photo = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(Observations);
        Target.Validate();
        CalibrationFitter.ValidateObservations(Target, Observations);
        FitOptions?.Validate(Target);
        Photo?.Validate();
    }
}

/// <summary>The resized source photo is embedded so a saved calibration can be reopened on its own.</summary>
public sealed record CalibrationPhotoSession(string ImagePngBase64,
    IReadOnlyList<CalibrationPoint> OuterBoundary, IReadOnlyList<CalibrationPoint> HoleBoundary,
    double PhysicalOuterRadiusMm, bool RectificationConfirmed,
    IReadOnlyList<CalibrationCrossTrace> Traces)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ImagePngBase64) || ImagePngBase64.Length > 14_000_000)
            throw new ArgumentException("Calibration photo is empty or too large; please import again.");
        if (PhysicalOuterRadiusMm is not (40 or 60))
            throw new ArgumentException("Please select 12 cm or 8 cm disc.");
        ValidateBoundary(OuterBoundary);
        ValidateBoundary(HoleBoundary);
        if (Traces is null || Traces.Count > 1 || Traces.Any(t => t is null || t.ArmIndex != 0
            || t.Points is null || t.Points.Count > 4000 || t.Points.Any(p => p is null
                || !double.IsFinite(p.X) || !double.IsFinite(p.Y)
                || Math.Sqrt(p.X * p.X + p.Y * p.Y) > PhysicalOuterRadiusMm))
            || Traces.Select(t => t.ArmIndex).Distinct().Count() != Traces.Count)
            throw new ArgumentException("Invalid crosshair record.");
        if (!RectificationConfirmed && Traces.Any(t => t.Points.Count > 0))
            throw new ArgumentException("Photo rectification not confirmed; cannot save trace points.");
    }

    private static void ValidateBoundary(IReadOnlyList<CalibrationPoint> points)
    {
        if (points is null || points.Count is < 5 or > 128 || points.Any(p => p is null
            || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X > 2000 || p.Y > 2000))
            throw new ArgumentException("Invalid photo contour control points.");
    }
}

public static class CalibrationSessionJson
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(CalibrationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Validate();
        return JsonSerializer.Serialize(session, JsonOptions);
    }

    public static CalibrationSession Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 16_000_000) throw new ArgumentException("Calibration session file is too large.");
        CalibrationSession session = JsonSerializer.Deserialize<CalibrationSession>(json, JsonOptions)
            ?? throw new ArgumentException("Calibration session is empty.");
        session.Validate();
        return session;
    }

    public static void Save(string path, CalibrationSession session)
    {
        string json = Serialize(session);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static CalibrationSession Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 16_000_000) throw new ArgumentException("Calibration session file is too large.");
        return Deserialize(File.ReadAllText(path));
    }
}
