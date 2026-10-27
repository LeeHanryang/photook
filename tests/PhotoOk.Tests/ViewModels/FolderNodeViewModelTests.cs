using System.Diagnostics;
using System.Globalization;
using System.IO;
using PhotoOk.Core.Planning;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class FolderNodeViewModelTests
{
    private const string Archive = @"D:\Archive";

    private static PlannedOperation Op(int id, OperationKind kind, string destination, string? source = null) =>
        new(id.ToString("000000", CultureInfo.InvariantCulture), kind, source, destination, 1, []);

    [Fact]
    public void Build_CountsFiles_MarksNewFolders_SkipsTempMoves()
    {
        var plan = new Plan(Archive,
            [
                Op(1, OperationKind.CreateDirectory, @"D:\Archive\2026\2026-10-14"),
                Op(2, OperationKind.Move, @"E:\x\A.JPG.photook-tmp-op-000002", @"E:\x\A.JPG"),
                Op(3, OperationKind.Copy, @"D:\Archive\2026\2026-10-14\A.JPG", @"E:\x\A.JPG.photook-tmp-op-000002"),
                Op(4, OperationKind.Copy, @"D:\Archive\2026\2026-10-14\B.JPG", @"E:\x\B.JPG"),
                Op(5, OperationKind.Copy, @"D:\Archive\2026\2026-10-02\C.JPG", @"E:\x\C.JPG"),
                Op(6, OperationKind.Copy, @"D:\Archive\Root.JPG", @"E:\x\Root.JPG"),
            ],
            [], []);

        var tree = FolderNodeViewModel.Build(plan);

        var year = Assert.Single(tree);
        Assert.Equal("2026", year.Name);
        Assert.Equal(3, year.FileCount);
        Assert.False(year.IsNew);
        Assert.Equal(["2026-10-02", "2026-10-14"], year.Children.Select(c => c.Name));
        Assert.Equal(2, year.Children[1].FileCount);
        Assert.True(year.Children[1].IsNew);
        Assert.False(year.Children[0].IsNew);
        Assert.Equal(@"D:\Archive\2026\2026-10-14", year.Children[1].FullPath);
    }

    [Fact]
    public void Build_FiveThousandDates_IsFast()
    {
        var ops = new List<PlannedOperation>();
        var start = new DateOnly(2010, 1, 1);
        for (var day = 0; day < 5000; day++)
        {
            var date = start.AddDays(day);
            var folder = Path.Combine(Archive, date.Year.ToString(CultureInfo.InvariantCulture),
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            ops.Add(Op(ops.Count + 1, OperationKind.CreateDirectory, folder));
            for (var file = 0; file < 10; file++)
            {
                ops.Add(Op(ops.Count + 1, OperationKind.Copy, Path.Combine(folder, $"IMG_{file}.JPG"), $@"E:\x\{day}_{file}.JPG"));
            }
        }

        var watch = Stopwatch.StartNew();
        var tree = FolderNodeViewModel.Build(new Plan(Archive, ops, [], []));
        watch.Stop();

        Assert.Equal(50_000, tree.Sum(year => year.FileCount));
        Assert.Equal(5000, tree.Sum(year => year.Children.Count));
        Assert.All(tree.SelectMany(year => year.Children), date => Assert.True(date.IsNew));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"트리 만들기에 {watch.Elapsed}가 걸렸습니다");
    }
}
