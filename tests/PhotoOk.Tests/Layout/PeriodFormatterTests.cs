using PhotoOk.Core.Layout;

namespace PhotoOk.Tests.Layout;

public class PeriodFormatterTests
{
    [Theory]
    [InlineData("2026-10-14", "2026-10-14", "2026-10-14")]
    [InlineData("2026-07-08", "2026-07-09", "2026-07-08~07-09")]
    [InlineData("2026-12-30", "2027-01-02", "2026-12-30~2027-01-02")]
    public void Format_Period(string s, string e, string expected)
    {
        Assert.Equal(expected, PeriodFormatter.Format(DateOnly.Parse(s), DateOnly.Parse(e)));
    }
}
