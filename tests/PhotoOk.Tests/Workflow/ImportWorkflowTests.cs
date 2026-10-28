using System.IO;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Workflow;

public class ImportWorkflowTests
{
    private const string Archive = @"D:\Archive";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly IReadOnlyDictionary<string, string> NoNames = new Dictionary<string, string>();

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();

    public ImportWorkflowTests()
    {
        _fs.AddDirectory(Archive);
    }

    private static ImportSettings Settings(Preset? preset = null) =>
        new([@"E:\DCIM"], true, Archive, preset ?? BuiltInPresets.DateOnly, TransferMode.Copy);

    private ImportWorkflow Workflow(IHasher? hasher = null) =>
        new(_fs, _reader, hasher ?? new XxHasher(_fs), TimeZoneInfo.Utc);

    private void AddPhoto(string path, byte[] content, DateTime capture)
    {
        _fs.AddFile(path, content);
        _reader.Set(path, capture);
    }

    [Fact]
    public async Task Scan_ThenPlan_ProducesDatedFolders()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        AddPhoto(@"E:\DCIM\B.JPG", [2], Oct14);
        AddPhoto(@"E:\DCIM\C.JPG", [3], Oct15);
        _fs.AddFile(@"E:\DCIM\notes.txt", [9]);
        var workflow = Workflow();
        var settings = Settings();

        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        var outcome = workflow.BuildPlan(settings, scan, NoNames);

        Assert.Equal(["2026-10-14", "2026-10-15"], scan.Sessions.Select(s => s.Id));
        Assert.Equal(3, scan.FileCount);
        Assert.Equal([@"E:\DCIM\notes.txt"], scan.Ignored);
        Assert.Empty(scan.Errors);
        var copies = outcome.Plan.Operations.Where(o => o.Kind == OperationKind.Copy).ToList();
        Assert.Equal(
            [
                @"D:\Archive\2026\2026-10-14\A.JPG",
                @"D:\Archive\2026\2026-10-14\B.JPG",
                @"D:\Archive\2026\2026-10-15\C.JPG",
            ],
            copies.Select(o => o.Destination));
        Assert.Empty(outcome.Issues);
        Assert.False(outcome.HasBlockingIssue);
    }

    [Fact]
    public async Task Replan_WithEventName_ReusesHashes()
    {
        // 대상에 같은 크기의 파일이 이미 있어 해시 비교가 필요하다.
        // 첫 계획은 14일에만 이름을 붙이고, 두 번째 계획은 15일 이름을 더한다.
        AddPhoto(@"E:\DCIM\A.JPG", [1, 2, 3], Oct14);
        AddPhoto(@"E:\DCIM\C.JPG", [4, 5, 6], Oct15);
        _fs.AddFile(@"D:\Archive\2026\2026-10-14 여행\A.JPG", [1, 2, 3]);
        _fs.AddFile(@"D:\Archive\2026\2026-10-15\C.JPG", [7, 8, 9]);
        _fs.AddFile(@"D:\Archive\2026\2026-10-15 소풍\C.JPG", [7, 8, 9]); // 두 번째 계획에서도 원본 C를 비교해야 한다
        var counting = new CountingHasher(new XxHasher(_fs));
        var workflow = Workflow(counting);
        var settings = Settings(BuiltInPresets.DateEvent);

        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        var first = workflow.BuildPlan(settings, scan, new Dictionary<string, string> { ["2026-10-14"] = "여행" });
        var callsAfterFirst = counting.TotalCalls;
        var second = workflow.BuildPlan(settings, scan,
            new Dictionary<string, string> { ["2026-10-14"] = "여행", ["2026-10-15"] = "소풍" });

        Assert.Equal(4, callsAfterFirst); // 원본 A·C + 대상 A·C
        Assert.Equal(5, counting.TotalCalls); // 두 번째 계획은 새 대상(소풍\C) 하나만 해시했다
        Assert.All(counting.CallsByPath.Values, n => Assert.Equal(1, n));
        Assert.Equal(1, counting.CallsByPath[@"E:\DCIM\A.JPG"]); // 두 원본 모두 두 계획에서 비교했지만 한 번씩만 읽었다
        Assert.Equal(1, counting.CallsByPath[@"E:\DCIM\C.JPG"]);
        Assert.Contains(first.Plan.Skipped, s => s.Reason == SkipReason.DuplicateAtDestination);
        Assert.Contains(second.Plan.Skipped, s => s.Source == @"E:\DCIM\A.JPG" && s.Reason == SkipReason.DuplicateAtDestination);
        Assert.Contains(second.Plan.Operations,
            o => o.Destination.EndsWith(@"2026-10-15 소풍\C_1.JPG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replan_UnreadableFileNote_AppearsOncePerPlan()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1, 2, 3], Oct14);
        _fs.AddFile(@"D:\Archive\2026\2026-10-14\A.JPG", [9, 9, 9]); // 같은 크기 → 비교하려다 원본을 읽지 못한다
        _fs.AddFile(@"D:\Archive\2026\2026-10-14 여행\A.JPG", [9, 9, 9]);
        var workflow = Workflow();
        var settings = Settings(BuiltInPresets.DateEvent);
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        _fs.FailReadsOf(@"E:\DCIM\A.JPG");
        const string Note = @"파일을 읽지 못해 다른 파일로 처리했습니다: E:\DCIM\A.JPG";

        var first = workflow.BuildPlan(settings, scan, NoNames);
        var second = workflow.BuildPlan(settings, scan, new Dictionary<string, string> { ["2026-10-14"] = "여행" });
        var third = workflow.BuildPlan(settings, scan, NoNames);

        Assert.Equal(1, first.Plan.Notes.Count(n => n == Note));
        Assert.Equal(1, second.Plan.Notes.Count(n => n == Note));
        Assert.Equal(1, third.Plan.Notes.Count(n => n == Note));
    }

    [Fact]
    public async Task BuildPlan_ReportsPreflightErrors()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        _fs.SetFreeSpace(@"D:\", 1000);
        var workflow = Workflow();
        var settings = Settings();

        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        var outcome = workflow.BuildPlan(settings, scan, NoNames);

        Assert.True(outcome.HasBlockingIssue);
    }

    [Fact]
    public async Task Scan_IsCancellable()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Workflow().ScanAsync(Settings(), null, cts.Token));
    }
}
