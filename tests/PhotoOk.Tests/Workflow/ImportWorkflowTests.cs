using System.IO;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
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

    private ImportWorkflow Workflow(IHasher? hasher = null, string? ignoredNamesPath = null, IFileSystem? fs = null) =>
        new(fs ?? _fs, _reader, hasher ?? new XxHasher(_fs), TimeZoneInfo.Utc, ignoredNamesPath);

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

    [Theory]
    [InlineData(false, true)] // 하위 폴더를 포함하지 않았는데 하위 폴더가 있다
    [InlineData(true, false)] // 하위 폴더까지 스캔했다
    public async Task Scan_CarriesWhetherSubfoldersWereLeftOut(bool includeSubfolders, bool expected)
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        AddPhoto(@"E:\DCIM\100CANON\B.JPG", [2], Oct14);
        var settings = Settings() with { IncludeSubfolders = includeSubfolders };

        var scan = await Workflow().ScanAsync(settings, null, CancellationToken.None);

        Assert.Equal(expected, scan.SubfoldersNotScanned);
        Assert.Equal(includeSubfolders ? 2 : 1, scan.FileCount);
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

    // ---- 흡수·재정리: 아카이브 색인과 제안 이름 ----

    private const string Source = @"E:\DCIM";
    private const string IgnoredPath = @"D:\Data\ignored-folder-names.txt";

    private static readonly byte[] IgnoredFileContent = "봄소풍\n"u8.ToArray();

    private static ImportSettings AbsorbSettings(Preset? preset = null, bool useFolderNames = true) =>
        new([Source], true, Archive, preset ?? BuiltInPresets.DateEvent, TransferMode.Move, JobKind.Absorb, useFolderNames);

    private sealed class Recording<T>(Action<T>? onReport = null) : IProgress<T>
    {
        public void Report(T value) => onReport?.Invoke(value);
    }

    [Fact]
    public async Task Scan_Absorb_ReportsArchiveCheck_AfterFilesAreRead()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        var events = new List<string>();

        await Workflow().ScanAsync(AbsorbSettings(), new Recording<int>(_ => events.Add("파일")), CancellationToken.None,
            new Recording<bool>(checking => events.Add(checking ? "보관 폴더" : "끝")));

        Assert.Equal(["파일", "보관 폴더"], events);
    }

    [Fact]
    public async Task Scan_Import_DoesNotReportArchiveCheck()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        var reports = 0;

        await Workflow().ScanAsync(Settings(), null, CancellationToken.None, new Recording<bool>(_ => reports++));

        Assert.Equal(0, reports);
    }

    [Fact]
    public async Task Scan_Reorganize_MarksFormatNames()
    {
        AddPhoto(@"D:\Archive\[2026-10-14] 소풍\A.JPG", [1], Oct14);
        AddPhoto(@"D:\Archive\봄꽃\B.JPG", [2], Oct14);
        var settings = new ImportSettings([@"D:\Archive"], true, @"D:\Archive",
            new Preset("테스트", TimeOnly.MinValue,
                [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("[{날짜}] {이벤트}")])],
                [], new Dictionary<string, IReadOnlyList<string>>()),
            TransferMode.Move, JobKind.Reorganize, UseFolderNames: true);

        var workflow = Workflow();
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);

        Assert.True(scan.Sessions.Single(s => s.SuggestedEventName == "소풍").SuggestedFromFormat);
        Assert.False(scan.Sessions.Single(s => s.SuggestedEventName == "봄꽃").SuggestedFromFormat);
        Assert.Equal([false, true], workflow.Regroup(settings, scan).Sessions.Select(s => s.SuggestedFromFormat).Order());
    }

    [Fact]
    public async Task Scan_Absorb_BuildsIndexAndSuggestsNames()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(@"D:\Archive\2026\old\x.jpg", [1, 2]);
        _fs.AddFile(@"D:\Archive\2026\old\y.jpg", [3]);
        _fs.AddFile(@"D:\Archive\.photook\되돌림\z.jpg", [4]);

        var scan = await Workflow().ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        Assert.NotNull(scan.Index);
        Assert.Equal(2, scan.Index.Count);
        Assert.Equal("봄소풍", Assert.Single(scan.Sessions).SuggestedEventName);
        Assert.NotNull(scan.Groups);
        Assert.Single(scan.Groups);
        Assert.Empty(scan.Errors);
    }

    [Fact]
    public async Task Scan_Import_NoIndexNoNames()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(@"D:\Archive\2026\old\x.jpg", [1, 2]);
        var settings = AbsorbSettings() with { Kind = JobKind.Import, Mode = TransferMode.Copy };

        var scan = await Workflow().ScanAsync(settings, null, CancellationToken.None);

        Assert.Null(scan.Index);
        Assert.Null(Assert.Single(scan.Sessions).SuggestedEventName);
    }

    [Fact]
    public async Task Scan_Reorganize_NoIndex()
    {
        AddPhoto(@"D:\Archive\2026\2026-10-14 여행\A.JPG", [1], Oct14);
        var settings = new ImportSettings([Archive], true, Archive, BuiltInPresets.DateEvent, TransferMode.Move, JobKind.Reorganize);

        var scan = await Workflow().ScanAsync(settings, null, CancellationToken.None);

        Assert.Null(scan.Index);
        Assert.Equal("여행", Assert.Single(scan.Sessions).SuggestedEventName);
    }

    [Fact]
    public async Task Scan_Absorb_IndexFolderError_IsScanError()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(@"D:\Archive\locked\x.jpg", [1]);
        _fs.DenyDirectory(@"D:\Archive\locked");

        var scan = await Workflow().ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        var error = Assert.Single(scan.Errors);
        Assert.Equal(@"D:\Archive\locked", error.Path);
        Assert.NotNull(scan.Index);
    }

    [Fact]
    public async Task Scan_Absorb_LinkedFolderInArchive_IsScanError()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(@"D:\Archive\link\x.jpg", [1]);
        _fs.AddLinkedDirectory(@"D:\Archive\link");

        var scan = await Workflow().ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        var error = Assert.Single(scan.Errors);
        Assert.Equal(@"D:\Archive\link", error.Path);
        Assert.StartsWith(LinkedFolderSkippedException.MessagePrefix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_Absorb_UnreadableArchiveFile_IsScanError()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(@"D:\Archive\x.jpg", [1]);
        var hooked = new HookedFileSystem(_fs)
        {
            BeforeGetFile = path =>
            {
                if (path == @"D:\Archive\x.jpg")
                {
                    throw new IOException("locked");
                }
            },
        };

        var scan = await Workflow(fs: hooked).ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        var error = Assert.Single(scan.Errors);
        Assert.Equal(@"D:\Archive\x.jpg", error.Path);
        Assert.Equal(0, scan.Index!.Count);
    }

    [Theory]
    [InlineData(JobKind.Import, false)]
    [InlineData(JobKind.Absorb, true)]
    public async Task Scan_LoadsIgnoredNamesOnlyWhenNamesAreRead(JobKind kind, bool expectedLoaded)
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(IgnoredPath, IgnoredFileContent);
        var opened = new List<string>();
        var hooked = new HookedFileSystem(_fs) { BeforeOpenRead = opened.Add };
        var settings = AbsorbSettings() with { Kind = kind };

        await Workflow(ignoredNamesPath: IgnoredPath, fs: hooked).ScanAsync(settings, null, CancellationToken.None);

        Assert.Equal(expectedLoaded, opened.Contains(IgnoredPath));
    }

    [Fact]
    public async Task Scan_UsesWholeScanForDeviceNames()
    {
        // Zf 폴더의 사진에는 EXIF가 없지만, 같은 스캔의 다른 폴더 사진이 Zf 모델이면 Zf는 기기 이름이다.
        AddPhoto(@"E:\DCIM\Zf\A.JPG", [1], Oct14);
        var control = await Workflow().ScanAsync(AbsorbSettings(), null, CancellationToken.None);
        AddPhoto(@"E:\DCIM\Other\B.JPG", [2], Oct15);
        _reader.Set(@"E:\DCIM\Other\B.JPG", Oct15, "Maker", "Zf");

        var scan = await Workflow().ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        Assert.Equal("Zf", Assert.Single(control.Sessions).SuggestedEventName);
        var a = scan.Sessions.Single(s => s.Groups.Any(g => g.Primary.Path == @"E:\DCIM\Zf\A.JPG"));
        Assert.Null(a.SuggestedEventName);
    }

    [Fact]
    public async Task Scan_ReadsIgnoredNames()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(IgnoredPath, IgnoredFileContent);

        var scan = await Workflow(ignoredNamesPath: IgnoredPath).ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        Assert.Null(Assert.Single(scan.Sessions).SuggestedEventName);
    }

    [Fact]
    public async Task Regroup_ChangesNamesWithoutReadingMetadata()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        var workflow = Workflow();
        var scan = await workflow.ScanAsync(AbsorbSettings(), null, CancellationToken.None);
        var reads = 0;
        _reader.OnRead = _ => reads++;

        var regrouped = workflow.Regroup(AbsorbSettings(useFolderNames: false), scan);

        Assert.Equal("봄소풍", Assert.Single(scan.Sessions).SuggestedEventName);
        Assert.Null(Assert.Single(regrouped.Sessions).SuggestedEventName);
        Assert.Equal(0, reads);
        Assert.Same(scan.Index, regrouped.Index);
        Assert.Same(scan.Errors, regrouped.Errors);
        Assert.Same(scan.Ignored, regrouped.Ignored);
        Assert.Equal(scan.FileCount, regrouped.FileCount);
        Assert.Equal(scan.SubfoldersNotScanned, regrouped.SubfoldersNotScanned);
    }

    [Fact]
    public async Task Regroup_SplitUnderscores_FillsBackslash()
    {
        AddPhoto(@"E:\DCIM\봄소풍_공원\A.JPG", [1], Oct14);
        var workflow = Workflow();
        var scan = await workflow.ScanAsync(AbsorbSettings(), null, CancellationToken.None);

        var regrouped = workflow.Regroup(AbsorbSettings() with { SplitUnderscores = true }, scan);

        Assert.Equal("봄소풍_공원", Assert.Single(scan.Sessions).SuggestedEventName);
        Assert.Equal(@"봄소풍\공원", Assert.Single(regrouped.Sessions).SuggestedEventName);
    }

    [Fact]
    public async Task Regroup_PresetChange_UsesNewDayStart()
    {
        var lateNight = new DateTime(2026, 10, 15, 1, 0, 0, DateTimeKind.Unspecified);
        AddPhoto(@"E:\DCIM\A.JPG", [1], lateNight);
        var workflow = Workflow();
        var scan = await workflow.ScanAsync(AbsorbSettings(BuiltInPresets.DateOnly), null, CancellationToken.None);
        var shifted = BuiltInPresets.DateOnly with { DayStart = new TimeOnly(3, 0) };

        var regrouped = workflow.Regroup(AbsorbSettings(shifted), scan);

        Assert.Equal(["2026-10-15"], scan.Sessions.Select(s => s.Id));
        Assert.Equal(["2026-10-14"], regrouped.Sessions.Select(s => s.Id));
    }

    [Fact]
    public async Task Regroup_RereadsIgnoredNamesFile()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        var workflow = Workflow(ignoredNamesPath: IgnoredPath);
        var scan = await workflow.ScanAsync(AbsorbSettings(), null, CancellationToken.None);
        _fs.AddFile(IgnoredPath, IgnoredFileContent);

        var regrouped = workflow.Regroup(AbsorbSettings(), scan);

        Assert.Equal("봄소풍", Assert.Single(scan.Sessions).SuggestedEventName);
        Assert.Null(Assert.Single(regrouped.Sessions).SuggestedEventName);
    }

    [Fact]
    public async Task BuildPlan_DoesNotRebuildIndex()
    {
        AddPhoto(@"E:\DCIM\봄소풍\A.JPG", [1], Oct14);
        _fs.AddFile(@"D:\Archive\2026\old\x.jpg", [1, 2]);
        var hooked = new HookedFileSystem(_fs);
        var workflow = Workflow(fs: hooked);
        var settings = AbsorbSettings();
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        var callsAfterScan = hooked.EnumerateFilesCalls;

        workflow.BuildPlan(settings, scan, NoNames);
        workflow.BuildPlan(settings, scan, new Dictionary<string, string> { ["2026-10-14|봄소풍"] = "다른 이름" });

        Assert.Equal(2, callsAfterScan); // 소스 한 번, 아카이브 색인 한 번
        Assert.Equal(callsAfterScan, hooked.EnumerateFilesCalls);
    }
}
