using System.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.Tests.Planning;

public class FolderDateTests
{
    private const string Archive = @"D:\Archive";

    [Theory]
    [InlineData("[2024-06-24] 일상", 2024, 6, 24)]
    [InlineData("[2024-06-24]", 2024, 6, 24)]
    [InlineData("[ 2024-06-24 ] 일상", 2024, 6, 24)]
    [InlineData("2024-06-24", 2024, 6, 24)]
    [InlineData("2024-06-24 일상", 2024, 6, 24)]
    [InlineData("2024-06-24_가족_속초", 2024, 6, 24)]
    public void DayOfSegment_ReadsOneDay(string segment, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), FolderDate.DayOfSegment(segment));
    }

    [Theory]
    [InlineData("일상")]
    [InlineData("2024")]
    [InlineData("2024-06")]
    [InlineData("2024.06.24")] // yyyy-MM-dd 형식만 읽는다
    [InlineData("24-06-2024")]
    [InlineData("20240624")]
    [InlineData("일상 2024-06-24")] // 날짜가 이름 앞에 있어야 한다
    [InlineData("2024-13-45")] // 없는 날짜
    [InlineData("2024-02-30")]
    [InlineData("2024-06-245")]
    [InlineData("2024-06-24-2")]
    [InlineData("12024-06-24")]
    [InlineData("[2024-06-24] 2024-06-25 다시")] // 날짜가 둘
    [InlineData("2024-06-24 2024-06-25")]
    [InlineData("[2024-06-24~06-25] 소풍")] // 기간은 날짜 폴더가 아니다
    [InlineData("[2024-06-24~2024-06-25]")]
    [InlineData("2024-06-24~06-25 소풍")]
    [InlineData("2024-06-24 ~ 25")]
    [InlineData("2024-06-24～06-25")]
    [InlineData("2024-06-24–06-25")]
    [InlineData("2024-06-24~")]
    [InlineData("2024-06-15 ~ 16시 모임")] // 하루 폴더도 여러 날 폴더도 아니다
    [InlineData("2024-06-15-16")]
    public void DayOfSegment_OtherNames_AreNotDateFolders(string segment)
    {
        Assert.Null(FolderDate.DayOfSegment(segment));
    }

    [Fact]
    public void Of_UsesNearestDateFolder()
    {
        Assert.Equal(new DateOnly(2024, 6, 24),
            FolderDate.Of(@"D:\Archive\[2024-06-20] 여행\2024-06-24\Zfc\a.jpg", Archive));
    }

    [Fact]
    public void Of_LooksPastNameFoldersAndRanges()
    {
        Assert.Equal(new DateOnly(2024, 6, 20),
            FolderDate.Of(@"D:\Archive\[2024-06-20] 여행\[2024-06-21~06-22]\라이트룸\a.jpg", Archive));
    }

    [Theory]
    [InlineData(@"D:\Archive\모음\a.jpg")]
    [InlineData(@"D:\Archive\a.jpg")]
    [InlineData(@"D:\Archive\[2024-06-21~06-22] 소풍\a.jpg")]
    [InlineData(@"D:\2024-06-24\a.jpg")] // 아카이브 밖
    [InlineData(@"E:\2024-06-24\a.jpg")]
    public void Of_NoDateFolder_IsNull(string path)
    {
        Assert.Null(FolderDate.Of(path, Archive));
    }

    [Fact]
    public void Of_StopsAtArchiveRoot()
    {
        Assert.Null(FolderDate.Of(@"D:\2024-06-24\모음\a.jpg", @"D:\2024-06-24"));
    }

    [Fact]
    public void Of_ArchiveRootWithTrailingSeparator()
    {
        Assert.Equal(new DateOnly(2024, 6, 24), FolderDate.Of(@"D:\Archive\2024-06-24\a.jpg", @"D:\Archive\"));
        Assert.Equal(new DateOnly(2024, 6, 24), FolderDate.Of(@"D:\2024-06-24\a.jpg", @"D:\"));
    }

    [Theory]
    [InlineData("[2026-07-08~07-09] 소풍")]
    [InlineData("[2026-07-08~2026-07-09]")]
    [InlineData("[ 2026.07.08 ~ 07.09 ] 소풍")]
    [InlineData("2026-07-08~07-09 소풍")]
    [InlineData("2026-07-08~2026-07-09")]
    [InlineData("2026-7-8～7-9")]
    [InlineData("2026-07-08–09")]
    [InlineData("2024-06-15 ~ 16")]
    [InlineData("2024-06-15~16 모임")]
    [InlineData("[2024-06-15~16]모임")]
    [InlineData("2026-07-08~07-09_소풍")]
    [InlineData("2026-07-08~2026-07-09_소풍")]
    [InlineData("2026-07-08~07-09-소풍")]
    [InlineData("2026-07-08~09")]
    [InlineData("2026-07-08~09_소풍")]
    public void IsMultiDaySegment_True(string segment)
    {
        Assert.True(FolderDate.IsMultiDaySegment(segment));
    }

    [Theory]
    [InlineData("[2026-07-08] 소풍")]
    [InlineData("2026-07-08")]
    [InlineData("[소풍~나들이]")]
    [InlineData("소풍 [2026-07-08~07-09]")] // 이름이 앞에 있으면 기간 폴더로 보지 않는다
    [InlineData("2026-07-08~소풍")]
    [InlineData("2024-06-15 ~ 16시 모임")] // 끝이 날짜가 아니라 시각이다
    [InlineData("2024-06-15~16시")]
    [InlineData("2024-06-15~-")] // 끝에 숫자가 없다
    [InlineData("2024-06-15~123")] // 끝이 날짜 조각이 아니다
    [InlineData("2024-06-15~2026")]
    [InlineData("2024-06-15~16-17시")]
    [InlineData("2024-06-15~16.모임")]
    [InlineData("2024-06-15-16")] // 구분 표시가 없다
    public void IsMultiDaySegment_False(string segment)
    {
        Assert.False(FolderDate.IsMultiDaySegment(segment));
    }

    [Fact]
    public void MultiDayFolders_ListsEveryRangeUpToArchiveRoot()
    {
        Assert.Equal(
            [@"D:\Archive\[2026-07-08~07-09] 소풍\2026-07-08~07-08 오전", @"D:\Archive\[2026-07-08~07-09] 소풍"],
            FolderDate.MultiDayFolders(@"D:\Archive\[2026-07-08~07-09] 소풍\2026-07-08~07-08 오전\라이트룸\a.jpg", Archive));
    }

    [Theory]
    [InlineData(@"D:\Archive\모음\a.jpg")]
    [InlineData(@"D:\[2026-07-08~07-09] 소풍\a.jpg")] // 아카이브 밖
    public void MultiDayFolders_None(string path)
    {
        Assert.Empty(FolderDate.MultiDayFolders(path, Archive));
    }

    [Fact]
    public void MultiDayFolders_ArchiveRootItselfDoesNotCount()
    {
        Assert.Empty(FolderDate.MultiDayFolders(@"D:\[2026-07-08~07-09] 소풍\모음\a.jpg", @"D:\[2026-07-08~07-09] 소풍"));
    }
}
