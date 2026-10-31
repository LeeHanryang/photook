using PhotoOk.Core.Metadata;

namespace PhotoOk.Tests.Metadata;

public class FileNameDateTests
{
    private static readonly DateTime Today = new(2026, 10, 28, 15, 0, 0, DateTimeKind.Unspecified);

    [Theory]
    [InlineData("photo_2026-10-17_00-32-09.jpg", "2026-10-17T00:32:09")]
    [InlineData("photo_3_2025-12-03_23-01-58 (2).jpg", "2025-12-03T23:01:58")]
    [InlineData("video_2026-10-17_00-31-50.mp4", "2026-10-17T00:31:50")]
    [InlineData("20231214_161919.jpg", "2023-12-14T16:19:19")]
    [InlineData("20240104_115334 (2).jpg", "2024-01-04T11:53:34")]
    [InlineData("KakaoTalk_20260109_143628675.mp4", "2026-01-09T14:36:28")]
    [InlineData("IMG_20231214_161919.jpg", "2023-12-14T16:19:19")]
    [InlineData("KakaoTalk_20251220.jpg", "2025-12-20T00:00:00")]
    [InlineData("2025.11.25 가나.mp4", "2025-11-25T00:00:00")]
    [InlineData("20250306 양식영상2.mp4", "2025-03-06T00:00:00")]
    [InlineData("IMG_20250306.jpg", "2025-03-06T00:00:00")]
    [InlineData("20250306_120000.jpg", "2025-03-06T12:00:00")] // 시각이 있는 형식이 먼저
    [InlineData("a 2025.11.25.jpg", "2025-11-25T00:00:00")]
    public void TryParse_ReadsKnownFormats(string name, string expected)
    {
        var result = FileNameDate.TryParse(name, Today);

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
        Assert.Equal(DateTimeKind.Unspecified, result!.Value.Kind);
    }

    [Theory]
    [InlineData("1690164207730.jpg")]
    [InlineData("20231399_161919.jpg")]
    [InlineData("photo_1989-12-31_10-00-00.jpg")]
    [InlineData("20261030_101010.jpg")] // 내일모레(미래)
    [InlineData("김공장 도면1.jpg")]
    [InlineData("120231214_161919.jpg")] // 앞에 숫자가 붙음
    [InlineData("20231214_256019.jpg")] // 시각이 올바르지 않으면 날짜만 읽지 않는다
    [InlineData("1690164019845.jpg")] // 긴 숫자는 날짜가 아니다
    [InlineData("2025.13.45.jpg")]
    [InlineData("99991231.jpg")] // 미래
    [InlineData("12025.11.25.jpg")] // 점 형식도 앞에 숫자가 붙으면 안 된다
    [InlineData("2025.11.251.jpg")]
    [InlineData("202503061.jpg")] // 숫자만 있는 형식도 뒤에 숫자가 붙으면 안 된다
    public void TryParse_RejectsOthers(string name)
    {
        Assert.Null(FileNameDate.TryParse(name, Today));
    }

    [Fact]
    public void TryParse_AllowsLaterToday_ButNotTomorrow()
    {
        // 한계는 "오늘 다음 날 0시 미만"이다. 오늘 안의 시각은 지금보다 늦어도 받는다.
        Assert.Equal(new DateTime(2026, 10, 28, 23, 59, 59), FileNameDate.TryParse("20261028_235959.jpg", Today));
        Assert.Null(FileNameDate.TryParse("20261029_000000.jpg", Today));
    }

    [Fact]
    public void TryParse_RejectsDashedFormatWithDigitsAround()
    {
        Assert.Null(FileNameDate.TryParse("12026-10-17_00-32-09.jpg", Today));
        Assert.Null(FileNameDate.TryParse("2026-10-17_00-32-091.jpg", Today));
    }

    [Fact]
    public void TryParse_FallsThroughToNextFormat_WhenFirstIsInvalid()
    {
        // 첫 형식은 달력에 없는 날짜라 버리고, 두 번째 형식을 쓴다.
        Assert.Equal(new DateTime(2023, 12, 14, 16, 19, 19),
            FileNameDate.TryParse("2026-02-30_10-00-00_20231214_161919.jpg", Today));
    }

    [Fact]
    public void TryParse_UsesOnlyFirstMatchOfEachFormat()
    {
        // 같은 형식의 첫 일치가 달력에 없는 날짜면, 그 형식의 뒤쪽 일치는 보지 않는다.
        Assert.Null(FileNameDate.TryParse("20231399_161919_20231214_161919.jpg", Today));

        // 둘 다 올바르면 앞의 것.
        Assert.Equal(new DateTime(2023, 12, 14, 16, 19, 19), FileNameDate.TryParse("20231214_161919_20240101_101010.jpg", Today));
    }
}
