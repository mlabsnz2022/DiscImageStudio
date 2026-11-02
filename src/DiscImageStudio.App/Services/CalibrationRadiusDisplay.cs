using DiscImageStudio.Core.Calibration;

namespace DiscImageStudio.Services;

internal static class CalibrationRadiusDisplay
{
    internal static string Format(CalibrationTarget target)
    {
        CalibrationParameters p = target.Parameters;
        return $"Full data zone radius (at pattern creation): {ParameterNumberText.ForInput(p.InnerRadiusMm)}–{ParameterNumberText.ForInput(p.OuterRadiusMm)} mm";
    }
}
