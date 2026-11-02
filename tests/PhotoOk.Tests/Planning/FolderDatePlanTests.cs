using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Planning;

/// <summary>재정리는 폴더 날짜로 정리한다. 스캔부터 계획까지 실제 흐름으로 대상 경로를 확인한다.</summary>
public sealed class FolderDatePlanTests
{
    private const string Archive = @"D:\Archive";
    private const string Daily = @"D:\Archive\2026\[2026-10-15] 일상";
    private const string Model = "TESTCAM";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);

    /// <summary>날짜 토큰이 여러 개인 정리 방식. 판단 필요 갈래가 없다.</summary>
    private static readonly Preset YearPeriodFormat = new("연도·기간·포맷", TimeOnly.MinValue,
        [new LayoutBranch("기본", BranchCondition.Always, [new("{연도}"), new("[{기간}] {이벤트}"), new("{포맷}")])],
        [], BuiltInPresets.DateEvent.FormatGroups);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();
    private readonly ImportWorkflow _workflow;
    private byte _nextFill = 1;

    public FolderDatePlanTests()
    {
        _fs.AddDirectory(Archive);
        _workflow = new ImportWorkflow(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc);
    }

    private string Add(string path, DateTime capture, byte[]? content = null)
    {
        var fill = _nextFill++;
        _fs.AddFile(path, content ?? [fill, fill, fill]);
        _reader.Set(path, capture, model: Model);
        return path;
    }

    private static ImportSettings Settings(Preset? preset = null, JobKind kind = JobKind.Reorganize, string source = Archive) =>
        new([source], true, Archive, preset ?? YearPeriodFormat, TransferMode.Move, kind, UseFolderNames: kind != JobKind.Import);

    private async Task<Plan> PlanAsync(ImportSettings settings)
    {
        var scan = await _workflow.ScanAsync(settings, null, CancellationToken.None);
        var names = scan.Sessions
            .Where(session => session.SuggestedEventName is not null)
            .ToDictionary(session => session.Id, session => session.SuggestedEventName!, StringComparer.Ordinal);
        return _workflow.BuildPlan(settings, scan, names).Plan;
    }

    private static List<PlannedOperation> FileOps(Plan plan) =>
        plan.Operations.Where(op => op.Kind != OperationKind.CreateDirectory).ToList();

    private static string DestinationOf(Plan plan, string source) =>
        FileOps(plan).Single(op => string.Equals(op.Source, source, StringComparison.OrdinalIgnoreCase)).Destination;

    /// <summary>계획대로 옮긴 것처럼 파일 시스템과 사진 정보를 고친다.</summary>
    private void Run(Plan plan, DateTime capture)
    {
        foreach (var op in plan.Operations)
        {
            if (op.Kind == OperationKind.CreateDirectory)
            {
                _fs.CreateDirectory(op.Destination);
            }
            else
            {
                _fs.Move(op.Source!, op.Destination);
                _reader.Set(op.Destination, capture, model: Model);
            }
        }
    }

    [Fact]
    public async Task Reorganize_UsesFolderDate_ForEveryDateToken()
    {
        var a = Add($@"{Daily}\a.jpg", Oct14);

        var plan = await PlanAsync(Settings());

        Assert.Equal($@"{Daily}\JPG\a.jpg", DestinationOf(plan, a));
        Assert.DoesNotContain(plan.Operations, op => op.Destination.Contains("[2026-10-14]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reorganize_ChosenGroup_GoesToPhotoDate()
    {
        var a = Add($@"{Daily}\a.jpg", Oct14);
        var b = Add($@"{Daily}\b.jpg", Oct14);
        var settings = Settings() with { PhotoDateGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a } };

        var plan = await PlanAsync(settings);

        Assert.Equal(@"D:\Archive\2026\[2026-10-14] 일상\JPG\a.jpg", DestinationOf(plan, a));
        Assert.Equal($@"{Daily}\JPG\b.jpg", DestinationOf(plan, b));
    }

    [Fact]
    public async Task Reorganize_FolderDate_Rerun_HasNoOperations()
    {
        Add($@"{Daily}\a.jpg", Oct14);
        var first = await PlanAsync(Settings());
        Assert.Single(FileOps(first));
        Run(first, Oct14);

        var second = await PlanAsync(Settings());

        Assert.Empty(second.Operations);
    }

    [Fact]
    public async Task Reorganize_SameNameFromTwoFolders_GetsNumber_NotOverwrite()
    {
        // 이벤트 후보 이름의 폴더는 이벤트 이름의 조각으로 읽히므로, 이름으로 읽지 않는 작업 폴더를 쓴다.
        var a = Add($@"{Daily}\보정\x.jpg", Oct15, [1, 1, 1]);
        var b = Add($@"{Daily}\편집\x.jpg", Oct14, [2, 2, 2]);

        var plan = await PlanAsync(Settings() with { Subfolders = SubfolderPolicy.Merge });

        Assert.Equal(new[] { $@"{Daily}\JPG\x.jpg", $@"{Daily}\JPG\x_1.jpg" },
            new[] { DestinationOf(plan, b), DestinationOf(plan, a) }.Order(StringComparer.Ordinal));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public async Task Reorganize_SameContent_IsSkipped()
    {
        byte[] same = [7, 7, 7];
        var a = Add($@"{Daily}\보정\x.jpg", Oct15, same);
        var b = Add($@"{Daily}\편집\x.jpg", Oct14, same);

        var plan = await PlanAsync(Settings() with { Subfolders = SubfolderPolicy.Merge });

        // 하나만 옮기고 다른 하나는 이번 작업의 중복으로 제자리에 남긴다. 어느 파일도 사라지거나 덮어써지지 않는다.
        var moved = Assert.Single(FileOps(plan));
        Assert.Equal($@"{Daily}\JPG\x.jpg", moved.Destination);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal(SkipReason.DuplicateInPlan, skip.Reason);
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), new[] { moved.Source!, skip.Source }.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(FileOps(plan), op => _fs.FileExists(op.Destination));
    }

    [Fact]
    public async Task Monthly_FirstRunKeepsFolderMonth_SecondRunUsesPhotoDate()
    {
        var photoDay = new DateTime(2020, 2, 3, 10, 0, 0, DateTimeKind.Unspecified);
        var a = Add(@"D:\Archive\2026\[2026-10-15]\a.jpg", photoDay);
        var settings = Settings(BuiltInPresets.Monthly);

        var first = await PlanAsync(settings);

        Assert.Equal(@"D:\Archive\2026\2026-10\a.jpg", DestinationOf(first, a));
        Run(first, photoDay);

        // 하루 폴더를 남기지 않는 방식의 한계: 다음 재정리에는 읽을 폴더 날짜가 없어 사진 날짜로 옮겨진다.
        var second = await PlanAsync(settings);

        Assert.Equal(@"D:\Archive\2020\2020-02\a.jpg", DestinationOf(second, @"D:\Archive\2026\2026-10\a.jpg"));
    }

    [Fact]
    public async Task Reorganize_PhotoDateChoice_WithKeptDayFolder_ReturnsToFolderDateNextTime()
    {
        // 사진 날짜로 보내도 유지한 폴더 이름의 날짜는 그대로여서, 고른 값이 없는 다음 재정리는 그 날짜로 되돌린다(미리보기가 미리 알린다).
        var a = Add($@"{Daily}\a.jpg", Oct14);
        var chosen = Settings(BuiltInPresets.DateOnly) with { PhotoDateGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a } };

        var first = await PlanAsync(chosen);

        var sent = DestinationOf(first, a);
        Assert.Equal(@"D:\Archive\2026\2026-10-14\[2026-10-15] 일상\a.jpg", sent);
        Run(first, Oct14);

        var second = await PlanAsync(Settings(BuiltInPresets.DateOnly));

        Assert.Equal(@"D:\Archive\2026\2026-10-15\[2026-10-15] 일상\a.jpg", DestinationOf(second, sent));
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task ImportAndAbsorb_KeepPhotoDate(JobKind kind)
    {
        const string inbox = @"D:\Archive\받은 사진";
        var a = Add($@"{inbox}\[2026-10-15] 일상\a.jpg", Oct14);

        var plan = await PlanAsync(Settings(kind: kind, source: inbox));

        var destination = DestinationOf(plan, a);
        Assert.Contains("[2026-10-14]", destination, StringComparison.Ordinal);
        Assert.DoesNotContain("[2026-10-15]", destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiDayFolder_Split_UsesInnerOneDayFolder()
    {
        var a = Add(@"D:\Archive\[2026-10-14~10-16] 여행\2026-10-15\a.jpg", Oct14);

        var plan = await PlanAsync(Settings() with { SplitMultiDayFolders = true });

        Assert.Contains("[2026-10-15]", DestinationOf(plan, a), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiDayFolder_Kept_HasNoOperations()
    {
        var a = Add(@"D:\Archive\[2026-10-14~10-16] 여행\2026-10-15\a.jpg", Oct14);

        var plan = await PlanAsync(Settings());

        Assert.Empty(plan.Operations);
        Assert.Equal(new PlannedSkip(a, a, SkipReason.MultiDayFolderKept), Assert.Single(plan.Skipped));
    }
}
