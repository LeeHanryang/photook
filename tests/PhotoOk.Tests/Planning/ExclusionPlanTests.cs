using System.IO;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Planning;

/// <summary>사용자가 뺀 묶음과 여러 날 폴더는 작업이 없고, 그 자리는 다른 파일에게 비어 있지 않다.</summary>
public class ExclusionPlanTests
{
    private const string Archive = @"D:\Archive";
    private const string Day14 = @"D:\Archive\2026\2026-10-14";
    private const string Day15 = @"D:\Archive\2026\2026-10-15";
    private const string Old = @"D:\Archive\모음";
    private const string Trip = @"D:\Archive\[2026-10-14~10-15] 소풍";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FileTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string MultiDayKeptNote1 = "여러 날 폴더 1개는 옮기지 않습니다";

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();

    public ExclusionPlanTests()
    {
        _fs.AddDirectory(Archive);
    }

    private PhotoInfo Add(string path, byte[] content, DateTime? capture)
    {
        _fs.AddFile(path, content);
        return new PhotoInfo(path, content.Length, FileTime, FileKinds.Default.Classify(path), capture, null, null, null);
    }

    private static FileGroup Group(PhotoInfo primary, params PhotoInfo[] companions) => new(primary, companions);

    private Plan Create(FileGroup[] groups, IEnumerable<string>? excluded = null, JobKind kind = JobKind.Reorganize,
        TransferMode mode = TransferMode.Move, bool keepReview = false, bool split = false, SubfolderPolicy subfolders = SubfolderPolicy.Keep,
        Func<FileGroup, IReadOnlyList<string>>? keptOf = null, IReadOnlyList<string>? roots = null, ArchiveIndex? index = null,
        Preset? preset = null)
    {
        preset ??= BuiltInPresets.DateOnly;
        var sessions = SessionGrouper.GroupByDay(groups, preset.DayStart, TimeZoneInfo.Utc);
        return new Planner(_fs, new XxHasher(_fs)).Create(new PlanRequest(sessions, preset, Archive, mode, TimeZoneInfo.Utc,
            Kind: kind, Roots: roots ?? [Archive], Index: index, KeepReviewInPlace: keepReview, Subfolders: subfolders, KeptFoldersOf: keptOf,
            ExcludedGroups: excluded?.ToHashSet(StringComparer.OrdinalIgnoreCase), SplitMultiDayFolders: split));
    }

    private static List<PlannedOperation> FileOps(Plan plan) =>
        plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory).ToList();

    private static void AssertNoOperationTouches(Plan plan, params string[] paths)
    {
        foreach (var path in paths)
        {
            Assert.DoesNotContain(plan.Operations, op =>
                string.Equals(op.Source, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(op.Destination, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ----- 뺀 묶음 -----

    [Theory]
    [InlineData(JobKind.Import, TransferMode.Copy)]
    [InlineData(JobKind.Import, TransferMode.Move)]
    [InlineData(JobKind.Absorb, TransferMode.Copy)]
    [InlineData(JobKind.Absorb, TransferMode.Move)]
    [InlineData(JobKind.Reorganize, TransferMode.Move)]
    public void Excluded_NoOperation_SkipPointsAtItself(JobKind kind, TransferMode mode)
    {
        var kept = Add($@"{Old}\a.jpg", [1], Oct14);
        var moved = Add($@"{Old}\b.jpg", [2], Oct14);

        var plan = Create([Group(kept), Group(moved)], [kept.Path], kind, mode);

        Assert.Equal([new PlannedSkip(kept.Path, kept.Path, SkipReason.ExcludedByUser)], plan.Skipped);
        AssertNoOperationTouches(plan, kept.Path);
        Assert.Equal(moved.Path, Assert.Single(FileOps(plan)).Source);
    }

    [Fact]
    public void Excluded_MatchesIgnoringCase_EvenIfTheSetDoesNot()
    {
        var kept = Add($@"{Old}\a.jpg", [1], Oct14);
        var sessions = SessionGrouper.GroupByDay([Group(kept)], BuiltInPresets.DateOnly.DayStart, TimeZoneInfo.Utc);

        var plan = new Planner(_fs, new XxHasher(_fs)).Create(new PlanRequest(sessions, BuiltInPresets.DateOnly, Archive, TransferMode.Move,
            TimeZoneInfo.Utc, Kind: JobKind.Reorganize, ExcludedGroups: new HashSet<string>(StringComparer.Ordinal) { kept.Path.ToUpperInvariant() }));

        Assert.Empty(plan.Operations);
        Assert.Equal(SkipReason.ExcludedByUser, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Excluded_RawAndSidecar_StayTogether()
    {
        var raw = Add($@"{Old}\IMG.NEF", [1], Oct14);
        var xmp = Add($@"{Old}\IMG.xmp", [2], null);

        var plan = Create([Group(raw, xmp)], [raw.Path]);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [new PlannedSkip(raw.Path, raw.Path, SkipReason.ExcludedByUser), new PlannedSkip(xmp.Path, xmp.Path, SkipReason.ExcludedByUser)],
            plan.Skipped);
    }

    [Fact]
    public void Excluded_SpotIsTaken_OtherFileGetsNumber()
    {
        var kept = Add($@"{Day14}\a.jpg", [1], Oct15); // 15일 사진이라 원래는 옮겨져 자리를 비운다
        var incoming = Add($@"{Old}\a.jpg", [2], Oct14);

        var plan = Create([Group(kept), Group(incoming)], [kept.Path]);

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.Equal(incoming.Path, op.Source);
        Assert.Equal($@"{Day14}\a_1.jpg", op.Destination);
        AssertNoOperationTouches(plan, kept.Path);
    }

    [Fact]
    public void Excluded_SpotHoldsSameContent_OtherFileIsDuplicate()
    {
        var kept = Add($@"{Day14}\a.jpg", [1], Oct15);
        var incoming = Add($@"{Old}\a.jpg", [1], Oct14);

        var plan = Create([Group(kept), Group(incoming)], [kept.Path]);

        Assert.Empty(plan.Operations);
        Assert.Contains(new PlannedSkip(kept.Path, kept.Path, SkipReason.ExcludedByUser), plan.Skipped);
        Assert.Contains(new PlannedSkip(incoming.Path, kept.Path, SkipReason.DuplicateAtDestination), plan.Skipped);
    }

    [Fact]
    public void Excluded_VanishedFromDisk_SpotIsStillTaken()
    {
        var kept = new PhotoInfo($@"{Day14}\a.jpg", 1, FileTime, MediaKind.Image, Oct15, null, null, null); // 스캔 뒤 사라짐
        var incoming = Add($@"{Old}\a.jpg", [2], Oct14);

        var plan = Create([Group(kept), Group(incoming)], [kept.Path]);

        Assert.Equal($@"{Day14}\a_1.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Excluded_BreaksSwap_NoTemporaryMoves()
    {
        var first = Add($@"{Day14}\S.jpg", [1], Oct15);
        var second = Add($@"{Day15}\S.jpg", [2], Oct14);

        var plan = Create([Group(first), Group(second)], [first.Path]);

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(second.Path, op.Source);
        Assert.Equal($@"{Day14}\S_1.jpg", op.Destination);
        AssertNoOperationTouches(plan, first.Path);
        Assert.DoesNotContain(plan.Operations, o => o.Destination.Contains(".photook-tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Excluded_AllGroups_NoOperationsAtAll()
    {
        var a = Add($@"{Old}\a.jpg", [1], Oct14);
        var b = Add($@"{Old}\b.jpg", [2], Oct15);

        var plan = Create([Group(a), Group(b)], [a.Path, b.Path]);

        Assert.Empty(plan.Operations);
        Assert.All(plan.Skipped, s => Assert.Equal(SkipReason.ExcludedByUser, s.Reason));
    }

    [Fact]
    public void Excluded_WinsOverDuplicateInArchive()
    {
        var existing = Add($@"{Day14}\same.jpg", [1], Oct14);
        var kept = Add(@"D:\흩어짐\a.jpg", [1], Oct14);
        var index = ArchiveIndex.Build(_fs, Archive, (_, _) => { }, CancellationToken.None);

        var plan = Create([Group(kept)], [kept.Path], JobKind.Absorb, roots: [@"D:\흩어짐"], index: index);

        Assert.Equal([new PlannedSkip(kept.Path, kept.Path, SkipReason.ExcludedByUser)], plan.Skipped);
        Assert.True(_fs.FileExists(existing.Path));
    }

    // ----- 이유가 여럿일 때의 우선순위 -----

    [Fact]
    public void Priority_ExcludedFirst()
    {
        var path = $@"{Trip}\a.jpg"; // 여러 날 폴더, 촬영일 없음(판단 필요), 유지할 하위 폴더
        var photo = Add(path, [1], null);

        var plan = Create([Group(photo)], [path], keepReview: true, subfolders: SubfolderPolicy.InPlace, keptOf: _ => ["하위"]);

        Assert.Equal(SkipReason.ExcludedByUser, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Priority_MultiDayBeforeReviewAndSubfolder()
    {
        var photo = Add($@"{Trip}\a.jpg", [1], null);

        var plan = Create([Group(photo)], keepReview: true, subfolders: SubfolderPolicy.InPlace, keptOf: _ => ["하위"]);

        Assert.Equal(SkipReason.MultiDayFolderKept, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Priority_ReviewBeforeSubfolder()
    {
        var photo = Add($@"{Old}\a.jpg", [1], null);

        var plan = Create([Group(photo)], keepReview: true, subfolders: SubfolderPolicy.InPlace, keptOf: _ => ["하위"]);

        Assert.Equal(SkipReason.ReviewKeptInPlace, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Priority_SubfolderBeforeOtherVolume()
    {
        var photo = Add(@"E:\연결\a.jpg", [1], Oct14);

        var plan = Create([Group(photo)], subfolders: SubfolderPolicy.InPlace, keptOf: _ => ["하위"]);

        Assert.Equal(SkipReason.SubfolderKeptInPlace, Assert.Single(plan.Skipped).Reason);
    }

    // ----- 여러 날 폴더 -----

    [Theory]
    [InlineData(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg")] // 직속 파일
    [InlineData(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\깊은\곳\a.jpg")] // 모든 하위 파일
    [InlineData(@"D:\Archive\2026\2026-10-14~10-15 소풍\a.jpg")] // 대괄호 없는 이름
    [InlineData(@"D:\Archive\[2026-10-14~2026-10-15]\a.jpg")]
    [InlineData(@"D:\Archive\[2026-10-14～10-15]\a.jpg")]
    public void MultiDay_StaysInPlace(string path)
    {
        var photo = Add(path, [1], Oct14);

        var plan = Create([Group(photo)]);

        Assert.Empty(plan.Operations);
        Assert.Equal([new PlannedSkip(path, path, SkipReason.MultiDayFolderKept)], plan.Skipped);
        Assert.Equal(MultiDayKeptNote1, plan.MultiDayNotice);
        Assert.Empty(plan.Notes); // 접힌 메모가 아니라 요약 줄에 보인다
    }

    [Fact]
    public void MultiDay_RawAndSidecar_StayTogether()
    {
        var raw = Add($@"{Trip}\IMG.NEF", [1], Oct14);
        var xmp = Add($@"{Trip}\IMG.xmp", [2], null);

        var plan = Create([Group(raw, xmp)]);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [new PlannedSkip(raw.Path, raw.Path, SkipReason.MultiDayFolderKept), new PlannedSkip(xmp.Path, xmp.Path, SkipReason.MultiDayFolderKept)],
            plan.Skipped);
    }

    [Fact]
    public void MultiDay_FolderAboveThePickedFolder_StillCounts()
    {
        var path = $@"{Trip}\오전\a.jpg";
        var photo = Add(path, [1], Oct14);

        var plan = Create([Group(photo)], roots: [$@"{Trip}\오전"]);

        Assert.Equal(SkipReason.MultiDayFolderKept, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void MultiDay_FolderOutsideArchive_DoesNotCount()
    {
        _fs.AddDirectory(@"D:\[2026-10-14~10-15] 소풍\Archive");
        var photo = Add(@"D:\[2026-10-14~10-15] 소풍\Archive\모음\a.jpg", [1], Oct14);
        var sessions = SessionGrouper.GroupByDay([Group(photo)], BuiltInPresets.DateOnly.DayStart, TimeZoneInfo.Utc);

        var plan = new Planner(_fs, new XxHasher(_fs)).Create(new PlanRequest(sessions, BuiltInPresets.DateOnly, @"D:\[2026-10-14~10-15] 소풍\Archive",
            TransferMode.Move, TimeZoneInfo.Utc, Kind: JobKind.Reorganize));

        Assert.Single(FileOps(plan));
    }

    [Fact]
    public void MultiDay_SpotIsTaken_OtherFileGetsNumber()
    {
        // 고정 단계 프리셋이면 다른 파일의 대상이 여러 날 폴더 안이 된다.
        var preset = new Preset("테스트", TimeOnly.MinValue,
            [
                new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new LevelTemplate("판단 필요")]),
                new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("[2026-10-14~10-15] 소풍")]),
            ],
            [], BuiltInPresets.DateOnly.FormatGroups);
        var kept = Add($@"{Trip}\a.jpg", [1], Oct14);
        var incoming = Add($@"{Old}\a.jpg", [2], Oct14);

        var plan = Create([Group(kept), Group(incoming)], preset: preset);

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(incoming.Path, op.Source);
        Assert.Equal($@"{Trip}\a_1.jpg", op.Destination);
        AssertNoOperationTouches(plan, kept.Path);
    }

    [Fact]
    public void MultiDay_Note_CountsFolders()
    {
        var a = Add($@"{Trip}\2026-10-14\a.jpg", [1], Oct14);
        var b = Add($@"{Trip}\2026-10-15\b.jpg", [2], Oct15);
        var c = Add(@"D:\Archive\2026\[2026-10-14~2026-10-15]\c.jpg", [3], Oct14);
        var d = Add($@"{Old}\d.jpg", [4], Oct14);

        var plan = Create([Group(a), Group(b), Group(c), Group(d)]);

        Assert.Equal("여러 날 폴더 2개는 옮기지 않습니다", plan.MultiDayNotice);
        Assert.Equal(d.Path, Assert.Single(FileOps(plan)).Source);
    }

    [Fact]
    public void MultiDay_ExcludedOnly_NoNote()
    {
        var a = Add($@"{Trip}\a.jpg", [1], Oct14);

        var plan = Create([Group(a)], [a.Path]);

        Assert.Null(plan.MultiDayNotice);
        Assert.Equal(SkipReason.ExcludedByUser, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void MultiDay_OptionOn_Splits_AndShowsOldNote()
    {
        var a = Add($@"{Trip}\a.jpg", [1], Oct14);
        var b = Add($@"{Trip}\b.jpg", [2], Oct15);

        var plan = Create([Group(a), Group(b)], split: true);

        Assert.Equal([$@"{Day14}\a.jpg", $@"{Day15}\b.jpg"], FileOps(plan).Select(o => o.Destination));
        Assert.Equal("여러 날을 묶은 폴더 1개가 날짜별로 나뉩니다. 여러 날 묶기가 들어오기 전에는 재정리에서 빼 주세요", plan.MultiDayNotice);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void MultiDay_OptionOn_ExcludedStillStays()
    {
        var a = Add($@"{Trip}\a.jpg", [1], Oct14);
        var b = Add($@"{Trip}\b.jpg", [2], Oct15);

        var plan = Create([Group(a), Group(b)], [a.Path], split: true);

        AssertNoOperationTouches(plan, a.Path);
        Assert.Equal(b.Path, Assert.Single(FileOps(plan)).Source);
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public void MultiDay_OtherJobs_NotKept(JobKind kind)
    {
        var photo = Add(@"D:\흩어짐\[2026-10-14~10-15] 소풍\a.jpg", [1], Oct14);

        var plan = Create([Group(photo)], kind: kind, roots: [@"D:\흩어짐"]);

        Assert.Single(FileOps(plan));
        Assert.Empty(plan.Skipped);
        Assert.Null(plan.MultiDayNotice);
    }

    // ----- 화면이 부르는 흐름의 연결 -----

    private async Task<PlanOutcome> BuildAsync(ImportSettings settings, Func<ScanOutcome, IReadOnlySet<string>?>? excluded = null)
    {
        var workflow = new ImportWorkflow(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc);
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        return workflow.BuildPlan(settings, scan, new Dictionary<string, string>(), excluded?.Invoke(scan));
    }

    private void AddScanned(string path, byte[] content, DateTime? capture)
    {
        _fs.AddFile(path, content);
        _reader.Set(path, capture);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_MultiDayIsIndependentOfNameReading(bool useFolderNames)
    {
        AddScanned($@"{Trip}\오전\a.jpg", [1], Oct14);

        var outcome = await BuildAsync(new ImportSettings([$@"{Trip}\오전"], true, Archive, BuiltInPresets.DateEvent, TransferMode.Move,
            JobKind.Reorganize, useFolderNames));

        Assert.Empty(outcome.Plan.Operations);
        Assert.Equal(SkipReason.MultiDayFolderKept, Assert.Single(outcome.Plan.Skipped).Reason);
    }

    [Fact]
    public async Task Workflow_PassesSplitOption()
    {
        AddScanned($@"{Trip}\a.jpg", [1], Oct14);

        var outcome = await BuildAsync(new ImportSettings([Trip], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move,
            JobKind.Reorganize, SplitMultiDayFolders: true));

        Assert.Single(FileOps(outcome.Plan));
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    [InlineData(JobKind.Reorganize)]
    public async Task Workflow_PassesExcludedGroups(JobKind kind)
    {
        AddScanned($@"{Old}\a.jpg", [1], Oct14);
        AddScanned($@"{Old}\b.jpg", [2], Oct14);

        var outcome = await BuildAsync(new ImportSettings([Old], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move, kind),
            scan => new HashSet<string> { scan.Sessions[0].Groups[0].Id });

        Assert.Equal($@"{Old}\b.jpg", Assert.Single(FileOps(outcome.Plan)).Source);
        Assert.Equal(new PlannedSkip($@"{Old}\a.jpg", $@"{Old}\a.jpg", SkipReason.ExcludedByUser), Assert.Single(outcome.Plan.Skipped));
    }
}
