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
        var counting = new CountingHasher(new XxHasher(_fs));
        var workflow = Workflow(counting);
        var settings = Settings(BuiltInPresets.DateEvent);

        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        var first = workflow.BuildPlan(settings, scan, new Dictionary<string, string> { ["2026-10-14"] = "여행" });
        var callsAfterFirst = counting.TotalCalls;
        var second = workflow.BuildPlan(settings, scan,
            new Dictionary<string, string> { ["2026-10-14"] = "여행", ["2026-10-15"] = "소풍" });

        Assert.Equal(4, callsAfterFirst); // 원본 A·C + 대상 A·C
        Assert.Equal(4, counting.TotalCalls); // 두 번째 계획은 새로 해시하지 않았다
        Assert.All(counting.CallsByPath.Values, n => Assert.Equal(1, n));
        Assert.Contains(first.Plan.Skipped, s => s.Reason == SkipReason.DuplicateAtDestination);
        Assert.Contains(second.Plan.Operations,
            o => o.Destination.EndsWith(@"2026-10-15 소풍\C.JPG", StringComparison.Ordinal));
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
