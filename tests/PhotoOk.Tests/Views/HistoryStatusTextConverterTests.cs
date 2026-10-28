using System.Globalization;
using System.IO;
using PhotoOk.Core.History;
using PhotoOk.Views;

namespace PhotoOk.Tests.Views;

/// <summary>시작 화면과 작업 기록 화면은 같은 상태 문구(작업 기록 화면의 문구)를 쓴다.</summary>
public class HistoryStatusTextConverterTests
{
    [Theory]
    [InlineData(RunStatus.Completed, "완료")]
    [InlineData(RunStatus.CompletedWithFailures, "일부 실패")]
    [InlineData(RunStatus.Unfinished, "끝나지 않음")]
    [InlineData(RunStatus.PlanIncomplete, "계획 미완료 — 다시 계획해 주세요")]
    [InlineData(RunStatus.UndoInProgress, "되돌리는 중")]
    [InlineData(RunStatus.Undone, "되돌림")]
    public void Convert_UsesHistoryWording(RunStatus status, string expected)
    {
        var text = new HistoryStatusTextConverter().Convert(status, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void StartView_UsesHistoryStatusConverter()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PhotoOk", "Views", "StartView.xaml"));

        Assert.Contains("HistoryStatusTextConverter", xaml, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "src", "PhotoOk", "Views", "RunStatusTextConverter.cs")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoOk.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("레포 루트를 찾지 못했습니다.");
    }
}
