namespace DiscImageStudio.Cd;

public sealed record CdDiscPreset(
    string Id,
    string DisplayName,
    string Description,
    long Sectors,
    double InnerRadiusMm,
    double OuterRadiusMm,
    string? DisplayNameResourceKey = null,
    string? DescriptionResourceKey = null,
    bool IsCustom = false)
{
    public static CdDiscPreset Manual { get; } = new(
        "__manual__",
        "Custom Parameters (Unsaved)",
        "Generation parameters adjusted manually in UI; to preserve them, save to user presets JSON.",
        359_849,
        24.5,
        56.8,
        IsCustom: true);
}
