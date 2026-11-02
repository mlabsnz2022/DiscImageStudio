using System.Globalization;
using DiscImageStudio.Core;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>The two directions between an on-disk <see cref="ProjectDocument"/> and the
/// shared <see cref="DiscParametersState"/>. Opening fills State from the document; saving
/// reads State back. LivePreview/Burn/Calibration keep binding State unchanged.</summary>
internal static class ProjectSession
{
    public static void ApplyToState(ProjectDocument document, DiscParametersState state)
    {
        ProjectGeometry geometry = document.Geometry;
        ProjectOutputs outputs = document.Outputs;
        string inner = Format(geometry.InnerRadiusMm);
        string outer = Format(geometry.OuterRadiusMm);
        string sectors = geometry.Sectors.ToString(CultureInfo.InvariantCulture);

        ToneProfile tone = (document.Tone ?? ToneProfile.LegacyBinary).Normalized();
        if (document.Family is OpticalDiscFamily.Dvd or OpticalDiscFamily.BluRay
            && tone.Levels == ToneProfile.FourLevels)
        {
            tone = tone with { Levels = ToneProfile.TwoLevels };
        }

        state.ToneDitherStrength = tone.DitherStrength;
        state.ToneLevels = tone.Levels;
        state.ToneBrightness = tone.Brightness;
        state.ToneSrgbLinearization = tone.SrgbLinearization;

        switch (document.Family)
        {
            case OpticalDiscFamily.Dvd:
                state.DvdInnerRadius = inner;
                state.DvdOuterRadius = outer;
                state.DvdTotalSectors = sectors;
                state.DvdPitchLinear = Format(geometry.PitchLinear);
                state.DvdPitchQuadratic = Format(geometry.PitchQuadratic);
                state.DvdPitchCubic = Format(geometry.PitchCubic);
                // The design projection starts neutral: measured geometry follows the project
                // until the calibration workflow supplies a real disc's radii.
                state.DvdActualInnerRadius = inner;
                state.DvdActualOuterRadius = outer;
                state.DvdActualPitchLinear = Format(geometry.PitchLinear);
                state.DvdActualPitchQuadratic = Format(geometry.PitchQuadratic);
                state.DvdActualPitchCubic = Format(geometry.PitchCubic);
                state.DvdOutputPath = outputs.OutputPath;
                state.DvdDataDirectory = outputs.DataDirectory;
                state.DvdFilesAtOuterRing = outputs.FilesAtOuterRing;
                state.DvdVolumeLabel = outputs.VolumeLabel;
                state.DvdImagePath = string.Empty;
                break;
            case OpticalDiscFamily.BluRay:
                state.BdInnerRadius = inner;
                state.BdOuterRadius = outer;
                state.BdTotalSectors = sectors;
                state.BdActualInnerRadius = inner;
                state.BdActualOuterRadius = outer;
                state.BdOutputPath = outputs.OutputPath;
                state.BdDataDirectory = outputs.DataDirectory;
                state.BdFilesAtOuterRing = outputs.FilesAtOuterRing;
                state.BdVolumeLabel = outputs.VolumeLabel;
                state.BdInvertMapping = document.BluRay?.InvertMapping ?? false;
                state.BdDrawingStartSector = "0";
                state.BdDrawingEndSectorExclusive = "0";
                state.BdImagePath = string.Empty;
                break;
            default:
                state.CdInnerRadius = inner;
                state.CdOuterRadius = outer;
                state.CdSectors = sectors;
                state.CdActualInnerRadius = inner;
                state.CdActualOuterRadius = outer;
                state.CdDataDirectory = outputs.DataDirectory;
                state.CdFilesAtOuterRing = outputs.FilesAtOuterRing;
                state.CdVolumeLabel = string.IsNullOrWhiteSpace(outputs.VolumeLabel) ? "CD_IMAGE" : outputs.VolumeLabel;
                state.CdOutputPath = outputs.OutputPath;
                state.NormalizeCdOutputFormat();
                state.CdWriteCue = outputs.WriteCue;
                state.CdInterleave = outputs.Interleave;
                state.CdImagePath = string.Empty;
                break;
        }
    }

    public static ProjectGeometry CaptureGeometry(ProjectDocument current, DiscParametersState state)
    {
        switch (current.Family)
        {
            case OpticalDiscFamily.Dvd:
                return new ProjectGeometry(
                    ParameterParser.ParsePositiveDouble(state.DvdInnerRadius, "DVD Inner Radius"),
                    ParameterParser.ParsePositiveDouble(state.DvdOuterRadius, "DVD Outer Radius"),
                    ParameterParser.ParsePositiveLong(state.DvdTotalSectors, "DVD Total Sectors"),
                    ParameterParser.ParseDouble(state.DvdPitchLinear, "DVD Linear Term"),
                    ParameterParser.ParseDouble(state.DvdPitchQuadratic, "DVD Quadratic Term"),
                    ParameterParser.ParseDouble(state.DvdPitchCubic, "DVD Cubic Term"));
            case OpticalDiscFamily.BluRay:
                return new ProjectGeometry(
                    ParameterParser.ParsePositiveDouble(state.BdInnerRadius, "Blu-ray Inner Radius"),
                    ParameterParser.ParsePositiveDouble(state.BdOuterRadius, "Blu-ray Outer Radius"),
                    ParameterParser.ParsePositiveLong(state.BdTotalSectors, "Blu-ray Total Sectors"));
            default:
                return new ProjectGeometry(
                    ParameterParser.ParsePositiveDouble(state.CdInnerRadius, "CD Inner Radius"),
                    ParameterParser.ParsePositiveDouble(state.CdOuterRadius, "CD Outer Radius"),
                    ParameterParser.ParsePositiveLong(state.CdSectors, "CD Sectors"));
        }
    }

    public static ProjectOutputs CaptureOutputs(ProjectDocument current, DiscParametersState state)
    {
        switch (current.Family)
        {
            case OpticalDiscFamily.Dvd:
                return new ProjectOutputs(
                    RequireOutput(state.DvdOutputPath, "DVD ISO output location"),
                    WriteCue: false,
                    state.DvdDataDirectory,
                    state.DvdVolumeLabel,
                    Interleave: false,
                    FilesAtOuterRing: state.DvdFilesAtOuterRing);
            case OpticalDiscFamily.BluRay:
                return new ProjectOutputs(
                    RequireOutput(state.BdOutputPath, "Blu-ray ISO output location"),
                    WriteCue: false,
                    state.BdDataDirectory,
                    state.BdVolumeLabel,
                    Interleave: false,
                    FilesAtOuterRing: state.BdFilesAtOuterRing);
            default:
                return new ProjectOutputs(
                    RequireOutput(state.CdOutputPath, state.HasCdFiles ? "CD ISO output location" : "CD track output location"),
                    state.CdWriteCue,
                    state.CdDataDirectory,
                    state.CdVolumeLabel,
                    state.CdInterleave,
                    FilesAtOuterRing: state.CdFilesAtOuterRing);
        }
    }

    /// <summary>Tone fields as the project stores them. DVD and BD have only the two
    /// dispersion textures, so a 4-level setting carried over from a CD project is stored as 2.</summary>
    public static ToneProfile CaptureTone(ProjectDocument current, DiscParametersState state)
    {
        ToneProfile tone = state.Tone.Normalized();
        return current.Family is OpticalDiscFamily.Dvd or OpticalDiscFamily.BluRay
            && tone.Levels == ToneProfile.FourLevels
            ? tone with { Levels = ToneProfile.TwoLevels }
            : tone;
    }

    /// <summary>BD-only mapping polarity. CD/DVD projects never carry this section.</summary>
    public static ProjectBluRaySettings? CaptureBluRay(ProjectDocument current, DiscParametersState state)
        => current.Family == OpticalDiscFamily.BluRay
            ? new ProjectBluRaySettings(state.BdInvertMapping)
            : null;

    public static string Format(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string RequireOutput(string raw, string field)
    {
        string value = raw.Trim();
        return value.Length == 0 ? throw new ArgumentException($"Please enter {field}.") : value;
    }
}
