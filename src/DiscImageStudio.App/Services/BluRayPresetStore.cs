using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiscImageStudio.BluRay;

namespace DiscImageStudio.Services;

/// <summary>Independent user BD catalog. Constructors do no I/O; saves never touch CD/DVD presets.</summary>
public sealed class BluRayPresetStore(string path)
{
    public const string FileName = "blu-ray-presets.json";
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscImageStudio", FileName);

    public IReadOnlyList<BluRayDiscPreset> Load()
        => File.Exists(Path) ? ReadCatalog(Path).Presets.Select(ToPreset).ToArray() : [];

    public BluRayDiscPreset Save(string name, BluRayDiscParameters parameters, bool invert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Entry entry = new(NewId(), name.Trim(), parameters, invert);
        Validate(entry);
        Document existing = File.Exists(Path) ? ReadCatalog(Path) : new(1, []);
        Write(new(1, [.. existing.Presets, entry]));
        return ToPreset(entry);
    }

    /// <summary>Imports this catalog shape or the original bd-info export, preserving its geometry and polarity.</summary>
    public IReadOnlyList<BluRayDiscPreset> Import(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        Entry[] imported;
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(sourcePath), new JsonDocumentOptions
        {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
        }))
        {
            JsonElement root = json.RootElement;
            if (Find(root, "presets") is not null)
                imported = ReadCatalog(sourcePath).Presets.Select(entry => entry with { Id = NewId() }).ToArray();
            else
            {
                JsonElement parametersJson = Find(root, "parameters")
                    ?? throw new InvalidDataException("Please select a Blu-ray preset library or a previously exported Blu-ray parameters JSON (missing parameters).");
                ValidateGeometryFields(parametersJson);
                BluRayDiscParameters parameters = parametersJson.Deserialize<BluRayDiscParameters>(JsonOptions)
                    ?? throw new InvalidDataException("Blu-ray parameters cannot be empty.");
                bool invert = false;
                if (Find(root, "luminanceMapping") is JsonElement mapping && Find(mapping, "invert") is JsonElement polarity)
                {
                    if (polarity.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("Blu-ray luminanceMapping.invert must be a boolean.");
                    invert = polarity.GetBoolean();
                }
                imported = [new(NewId(), System.IO.Path.GetFileNameWithoutExtension(sourcePath), parameters, invert)];
            }
        }
        if (imported.Length == 0) throw new InvalidDataException("This file contains no importable Blu-ray presets.");
        foreach (Entry entry in imported) Validate(entry);
        // Load only after the entire import has passed validation. A malformed
        // existing file also blocks the save; it is never silently replaced.
        Document existing = File.Exists(Path) ? ReadCatalog(Path) : new(1, []);
        Write(new(1, [.. existing.Presets, .. imported]));
        return imported.Select(ToPreset).ToArray();
    }

    private static Document ReadCatalog(string path)
    {
        Document document;
        try
        {
            string source = File.ReadAllText(path);
            using JsonDocument json = JsonDocument.Parse(source, new JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (Find(json.RootElement, "presets") is JsonElement presets && presets.ValueKind == JsonValueKind.Array)
                foreach (JsonElement preset in presets.EnumerateArray())
                    ValidateGeometryFields(Find(preset, "parameters")
                        ?? throw new InvalidDataException("Blu-ray preset is missing parameters."));
            document = JsonSerializer.Deserialize<Document>(source, JsonOptions)
                ?? throw new InvalidDataException("Blu-ray preset JSON cannot be empty.");
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid Blu-ray preset JSON format.", exception); }
        if (document.SchemaVersion != 1 || document.Presets is null)
            throw new InvalidDataException("Blu-ray preset JSON must contain schemaVersion: 1 and a presets list.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Entry entry in document.Presets)
        {
            Validate(entry);
            if (!ids.Add(entry.Id)) throw new InvalidDataException($"Duplicate Blu-ray preset ID: {entry.Id}");
        }
        return document;
    }

    private void Write(Document document)
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(Entry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.DisplayName)
            || entry.Parameters is null || entry.Id == BluRayDiscPreset.Manual.Id
            || BluRayDiscPreset.BuiltIn.Any(preset => preset.Id == entry.Id))
            throw new InvalidDataException("Blu-ray preset must contain unique ID, name and parameters, and cannot reuse built-in or unsaved IDs.");
        entry.Parameters.Validate();
        if (entry.Parameters.StartAngleDegrees != 0)
            throw new InvalidDataException("Blu-ray authoring and calibration currently only support a starting angle of 0°; non-zero starting angles cannot be imported.");
    }
    private static JsonElement? Find(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(property => (JsonElement?)property.Value).FirstOrDefault()
            : null;
    private static void ValidateGeometryFields(JsonElement parameters)
    {
        foreach (string field in new[] { "totalSectors", "layerCount", "innerRadiusMm", "outerRadiusMm" })
            if (Find(parameters, field) is null)
                throw new InvalidDataException($"Blu-ray parameters missing {field}; defaults cannot substitute for imported parameters.");
    }
    private static string NewId() => "user-bd-" + Guid.NewGuid().ToString("N");
    private static BluRayDiscPreset ToPreset(Entry entry)
        => new(entry.Id, entry.DisplayName, entry.Parameters.LayerCount, entry.Parameters, entry.InvertMapping);
    private sealed record Entry(string Id, string DisplayName, BluRayDiscParameters Parameters, bool InvertMapping);
    private sealed record Document(int SchemaVersion, IReadOnlyList<Entry> Presets);
}
