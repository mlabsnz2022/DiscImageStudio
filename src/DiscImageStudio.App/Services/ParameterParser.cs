using System.Globalization;

namespace DiscImageStudio.Services;

/// Input parsing with dual-culture tolerance; messages are identical to the original UI.
internal static class ParameterParser
{
    internal static double ParseDouble(string raw, string fieldName)
    {
        string value = raw.Trim();
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result))
        {
            return result;
        }

        throw new FormatException($"{fieldName} is not a valid number.");
    }

    internal static double ParsePositiveDouble(string raw, string fieldName)
    {
        double result = ParseDouble(raw, fieldName);
        return double.IsFinite(result) && result > 0
            ? result
            : throw new ArgumentOutOfRangeException(fieldName, $"{fieldName} must be greater than 0.");
    }

    internal static int ParsePositiveInt(string raw, string fieldName)
    {
        string value = raw.Trim();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : throw new FormatException($"{fieldName} must be a positive integer.");
    }

    internal static long ParsePositiveLong(string raw, string fieldName)
    {
        string value = raw.Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result > 0
            ? result
            : throw new FormatException($"{fieldName} must be a positive integer.");
    }

    internal static long ParseNonNegativeLong(string raw, string fieldName)
    {
        string value = raw.Trim();
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) && result >= 0
            ? result
            : throw new FormatException($"{fieldName} must be a non-negative integer.");
    }

    internal static int ParsePreviewSize(string raw)
    {
        int size = ParsePositiveInt(raw, "Preview Size");
        return size is >= 64 and <= 8192
            ? size
            : throw new ArgumentOutOfRangeException(nameof(raw), "Preview size must be between 64 and 8192 px.");
    }

    internal static int ParsePreviewSamples(string raw, int maximum)
    {
        int samples = ParsePositiveInt(raw, "Samples per Sector");
        return samples <= maximum
            ? samples
            : throw new ArgumentOutOfRangeException(nameof(raw), $"Samples per sector must be between 1 and {maximum}.");
    }
}
