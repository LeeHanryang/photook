using PhotoOk.Core.Metadata;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Tests.Sessions;

public class SessionGrouperTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static FileGroup Group(string path, DateTime? captureTime, DateTime? lastWriteUtc = null) =>
        new(new PhotoInfo(path, 100, lastWriteUtc ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MediaKind.Image, captureTime, null, null, null), []);

    private static DateTime T(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    [Fact]
    public void GroupByDay_SplitsByCalendarDay()
    {
        var groups = new[]
        {
            Group(@"D:\S\c.jpg", T(10, 15, 8)),
            Group(@"D:\S\b.jpg", T(10, 14, 21)),
            Group(@"D:\S\a.jpg", T(10, 14, 9)),
        };

        var sessions = SessionGrouper.GroupByDay(groups, TimeOnly.MinValue, Utc);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("2026-10-14", sessions[0].Id);
        Assert.Equal(new DateOnly(2026, 10, 14), sessions[0].Start);
        Assert.Equal(new DateOnly(2026, 10, 14), sessions[0].End);
        Assert.Equal([@"D:\S\a.jpg", @"D:\S\b.jpg"], sessions[0].Groups.Select(g => g.Id));
        Assert.Equal("2026-10-15", sessions[1].Id);
        Assert.Single(sessions[1].Groups);
    }

    [Fact]
    public void GroupByDay_UsesDayStart()
    {
        var groups = new[] { Group(@"D:\S\a.jpg", T(10, 15, 3, 30)) };

        var sessions = SessionGrouper.GroupByDay(groups, new TimeOnly(4, 0), Utc);

        var session = Assert.Single(sessions);
        Assert.Equal("2026-10-14", session.Id);
    }

    [Fact]
    public void ShootDay_ExactlyAtDayStartBelongsToThatDay()
    {
        Assert.Equal(new DateOnly(2026, 10, 15), ShootDate.ShootDay(T(10, 15, 4), new TimeOnly(4, 0)));
        Assert.Equal(new DateOnly(2026, 10, 14), ShootDate.ShootDay(T(10, 15, 3, 59), new TimeOnly(4, 0)));
    }

    [Fact]
    public void EffectiveTime_FallsBackToLastWriteTime()
    {
        var kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var info = new PhotoInfo(@"D:\S\a.jpg", 1, new DateTime(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc),
            MediaKind.Image, null, null, null, null);

        var time = ShootDate.EffectiveTime(info, kst);

        Assert.Equal(new DateTime(2026, 10, 14, 10, 0, 0), time);
        Assert.Equal(DateTimeKind.Unspecified, time.Kind);
    }

    [Fact]
    public void EffectiveTime_PrefersCaptureTimeAsIs()
    {
        var kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var info = new PhotoInfo(@"D:\S\a.jpg", 1, new DateTime(2026, 10, 14, 1, 0, 0, DateTimeKind.Utc),
            MediaKind.Image, T(10, 13, 7), null, null, null);

        var time = ShootDate.EffectiveTime(info, kst);

        Assert.Equal(T(10, 13, 7), time);
        Assert.Equal(DateTimeKind.Unspecified, time.Kind);
    }

    [Fact]
    public void GroupByDay_UsesPrimaryTimeAndBreaksTiesByPath()
    {
        var same = T(10, 14, 9);
        var groups = new[]
        {
            Group(@"D:\S\B.jpg", same),
            Group(@"D:\S\a.jpg", same),
        };

        var sessions = SessionGrouper.GroupByDay(groups, TimeOnly.MinValue, Utc);

        Assert.Equal([@"D:\S\a.jpg", @"D:\S\B.jpg"], Assert.Single(sessions).Groups.Select(g => g.Id));
    }

    [Fact]
    public void GroupByDay_EmptyInputGivesNoSessions()
    {
        Assert.Empty(SessionGrouper.GroupByDay([], TimeOnly.MinValue, Utc));
    }
}
