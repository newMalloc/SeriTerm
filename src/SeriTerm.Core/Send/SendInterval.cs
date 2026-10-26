using System.Globalization;

namespace SeriTerm.Core.Send;

public static class SendInterval
{
    public static bool TryParse(string? text, out double seconds)
    {
        // UI and persisted values use the same decimal syntax on every machine.
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            && double.IsFinite(seconds) && seconds >= 0.05 && seconds <= 86400;
    }
}
