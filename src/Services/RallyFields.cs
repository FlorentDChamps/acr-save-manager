using System.Globalization;

namespace ACRSaveManager.Services;

/// <summary>
/// The values of the rally editor as its boxes show them: a start time as
/// "08:30", a share of the road as a whole percentage, and the step the ▲ and
/// ▼ beside a box take.
/// </summary>
public static class RallyFields
{
    private const int MinutesInDay = 24 * 60;

    /// <summary>Seconds since midnight as "08:30", within the day: 23:59:45 shows as 23:59, not 00:00.</summary>
    public static string Clock(float seconds)
    {
        int minutes = Math.Clamp((int)Math.Round(seconds / 60), 0, MinutesInDay - 1);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>"8:30" or "08:30" as seconds since midnight; null when it is not a time of day.</summary>
    public static float? ParseClock(string text) =>
        TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var t) && t.TotalHours < 24
            ? (float)t.TotalSeconds
            : null;

    /// <summary>A share from 0 to 1 as a whole percentage, "35".</summary>
    public static string Percent(float value) => Math.Round(value * 100).ToString(CultureInfo.InvariantCulture);

    /// <summary>"35" or "35 %" as 0.35; null unless a whole number from 0 to 100.</summary>
    public static float? ParsePercent(string text) =>
        int.TryParse(text.Trim().TrimEnd('%').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v is >= 0 and <= 100
            ? v / 100f
            : null;

    /// <summary>
    /// Where ▲ (<paramref name="direction"/> 1) or ▼ (-1) takes
    /// <paramref name="now"/>: the next multiple of <paramref name="step"/>
    /// that way, kept within <paramref name="min"/> and <paramref name="max"/>.
    /// Null when that does not move it that way: at the end of the range, or
    /// past it already.
    /// </summary>
    public static int? Step(int now, int step, int min, int max, int direction)
    {
        int next = direction > 0
            ? ((int)Math.Floor(now / (double)step) * step) + step
            : ((int)Math.Ceiling(now / (double)step) * step) - step;
        next = Math.Clamp(next, min, Math.Max(min, max));
        return (next - now) * direction > 0 ? next : null;
    }
}
