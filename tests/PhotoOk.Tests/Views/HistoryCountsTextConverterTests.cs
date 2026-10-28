using System.Globalization;
using PhotoOk.Core.History;
using PhotoOk.Views;

namespace PhotoOk.Tests.Views;

/// <summary>작업 기록 줄의 파일 수 문구. 되돌린 작업은 "완료 0"처럼 보이지 않게 되돌린 파일 수로 보인다.</summary>
public class HistoryCountsTextConverterTests
{
    private static RunSummary Run(RunStatus status, int total, int completed, int skipped, int undone = 0) =>
        new("r", @"C:\j.jsonl", @"C:\Archive", new DateTime(2026, 10, 28), status, total, completed, skipped, 0, false, false,
            undone);

    [Theory]
    [InlineData(RunStatus.Completed, 7, 7, 0, "완료 7 · 건너뜀 0 / 전체 7")]
    [InlineData(RunStatus.CompletedWithFailures, 7, 5, 1, "완료 5 · 건너뜀 1 / 전체 7")]
    [InlineData(RunStatus.Unfinished, 7, 3, 0, "완료 3 · 건너뜀 0 / 전체 7")]
    [InlineData(RunStatus.UndoInProgress, 7, 4, 0, "완료 4 · 건너뜀 0 / 전체 7")]
    public void Convert_ShowsCountsForStatus(RunStatus status, int total, int completed, int skipped, string expected)
    {
        var text = new HistoryCountsTextConverter().Convert(Run(status, total, completed, skipped), typeof(string), null,
            CultureInfo.InvariantCulture);

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(7, "파일 7개 되돌림")]
    [InlineData(57, "파일 57개 되돌림")] // 300개를 계획했지만 57개에서 멈춘 실행: 계획한 수가 아니라 되돌린 수
    [InlineData(0, "파일 0개 되돌림")]
    public void Convert_Undone_ShowsFilesActuallyUndone_NotPlanned(int undone, string expected)
    {
        var text = new HistoryCountsTextConverter().Convert(Run(RunStatus.Undone, 300, 0, 0, undone), typeof(string), null,
            CultureInfo.InvariantCulture);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Convert_NotARun_IsEmpty()
    {
        Assert.Equal(string.Empty, new HistoryCountsTextConverter().Convert(null, typeof(string), null, CultureInfo.InvariantCulture));
    }
}
