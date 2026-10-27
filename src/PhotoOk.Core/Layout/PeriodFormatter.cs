using System.Globalization;

namespace PhotoOk.Core.Layout;

public static class PeriodFormatter
{
    /// <summary>하루면 <c>2026-10-14</c>, 같은 해면 <c>2026-07-08~07-09</c>, 해를 넘기면 <c>2026-12-30~2027-01-02</c>.</summary>
    public static string Format(DateOnly start, DateOnly end)
    {
        var culture = CultureInfo.InvariantCulture;
        if (start == end)
        {
            return start.ToString("yyyy-MM-dd", culture);
        }

        return start.Year == end.Year
            ? $"{start.ToString("yyyy-MM-dd", culture)}~{end.ToString("MM-dd", culture)}"
            : $"{start.ToString("yyyy-MM-dd", culture)}~{end.ToString("yyyy-MM-dd", culture)}";
    }
}
