using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Tests.Planning;

public class ShootDayResolverTests
{
    private const string Archive = @"D:\Archive";
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);

    private static FileGroup Group(string path, DateTime? capture = null, DateTime? lastWriteUtc = null) =>
        new(new PhotoInfo(path, 100, lastWriteUtc ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MediaKind.Image, capture, null, null, null), []);

    private static ShootDayResolver Resolver(JobKind kind = JobKind.Reorganize, TimeOnly? dayStart = null,
        TimeZoneInfo? zone = null, string archiveRoot = Archive, IReadOnlySet<string>? photoDateGroups = null) =>
        new(dayStart ?? TimeOnly.MinValue, zone ?? Utc, kind, archiveRoot, photoDateGroups);

    [Fact]
    public void Resolve_UsesFolderDate_InReorganize()
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\a.jpg", Oct14);

        Assert.Equal(new DateOnly(2026, 10, 15), Resolver().Resolve(group));
        Assert.Equal(new DateOnly(2026, 10, 15), Resolver().FolderDay(group));
        Assert.Equal(new DateOnly(2026, 10, 14), Resolver().PhotoDay(group));
    }

    [Fact]
    public void ChosePhotoDate_IsFalse_WhenTheSetIsNull()
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\a.jpg", Oct14);

        Assert.False(Resolver(photoDateGroups: null).ChosePhotoDate(group));
    }

    [Fact]
    public void Resolve_PhotoDate_WhenChosen()
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\a.jpg", Oct14);
        var chosen = new HashSet<string>(StringComparer.Ordinal) { group.Id.ToUpperInvariant() };
        var resolver = Resolver(photoDateGroups: chosen);

        Assert.True(resolver.ChosePhotoDate(group));
        Assert.Equal(new DateOnly(2026, 10, 14), resolver.Resolve(group));
    }

    [Fact]
    public void Resolve_PhotoDate_WithoutOneDayFolder()
    {
        var group = Group(@"D:\Archive\모음\a.jpg", Oct14);

        Assert.Null(Resolver().FolderDay(group));
        Assert.Equal(new DateOnly(2026, 10, 14), Resolver().Resolve(group));
    }

    [Fact]
    public void Resolve_NearestOneDayFolderWins()
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\2026-10-16 보정\a.jpg", Oct14);

        Assert.Equal(new DateOnly(2026, 10, 16), Resolver().Resolve(group));
    }

    [Fact]
    public void Resolve_NoCaptureTime_UsesModifiedTime()
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\a.jpg", null,
            new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        Assert.Null(Resolver().FolderDay(group));
        Assert.Equal(new DateOnly(2026, 11, 1), Resolver().Resolve(group));
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public void Resolve_ImportAndAbsorb_UsePhotoDate(JobKind kind)
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\a.jpg", Oct14);

        Assert.Null(Resolver(kind).FolderDay(group));
        Assert.Equal(new DateOnly(2026, 10, 14), Resolver(kind).Resolve(group));
    }

    [Fact]
    public void Resolve_FolderDate_IgnoresDayStart()
    {
        var group = Group(@"D:\Archive\[2026-10-15] 일상\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0, DateTimeKind.Unspecified));
        var resolver = Resolver(dayStart: new TimeOnly(4, 0));

        Assert.Equal(new DateOnly(2026, 10, 15), resolver.Resolve(group));
        Assert.Equal(new DateOnly(2026, 10, 14), resolver.PhotoDay(group));
    }

    [Fact]
    public void Resolve_ArchiveRootNameIsNotAFolderDate()
    {
        var group = Group(@"D:\2026-10-15 백업\모음\a.jpg", Oct14);

        Assert.Equal(new DateOnly(2026, 10, 14), Resolver(archiveRoot: @"D:\2026-10-15 백업").Resolve(group));
    }

    [Fact]
    public void PhotoDay_UsesDayStart()
    {
        var group = Group(@"D:\Archive\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal(new DateOnly(2026, 10, 14), Resolver(dayStart: new TimeOnly(4, 0)).PhotoDay(group));
    }

    [Fact]
    public void PhotoDay_FallsBackToLastWriteTime_InGivenZone()
    {
        var kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var group = Group(@"D:\Archive\a.jpg", null, new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateOnly(2026, 10, 15), Resolver(zone: kst).PhotoDay(group));
    }
}
