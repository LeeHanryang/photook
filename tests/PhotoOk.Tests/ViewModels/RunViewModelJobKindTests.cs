using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.ViewModels;

/// <summary>실행 결과 화면의 재정리·흡수 요약. 가져오기 자체의 시험은 <see cref="RunViewModelTests"/>.</summary>
public class RunViewModelJobKindTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string LogFolder = @"L:\PhotoOk\logs";
    private const string SafeMessage = "검증 완료 — 원본을 직접 정리해도 안전합니다";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly ShellViewModel _shell = new(new FakeDialogService());
    private readonly ObservableObject _home = new ScreenStub();

    public RunViewModelJobKindTests()
    {
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
        _fs.AddDirectory(@"D:\Archive\2026");
        _fs.AddDirectory(@"D:\Archive\2027");
    }

    private sealed class ScreenStub : ObservableObject;

    private Executor NewExecutor() => new(new ExecutionContext(_fs, new XxHasher(_fs), AppData, () => Now));

    private RunViewModel ForPlan(Plan plan, ScanFacts facts) =>
        RunViewModel.ForPlan(_shell, NewExecutor(), _fs, new FileLog(_fs, LogFolder), () => _home, () => _home, plan, facts);

    private static Plan PlanOf(params PlannedOperation[] ops) =>
        new(Archive, ops, Array.Empty<PlannedSkip>(), Array.Empty<string>());

    private void AddFile(string path, int seed)
    {
        var data = new byte[64];
        new Random(seed).NextBytes(data);
        _fs.AddFile(path, data);
    }

    /// <summary>보관 폴더 안에서 파일 <paramref name="count"/>개를 옮기는 계획.</summary>
    private Plan MovePlan(int count)
    {
        var ops = new List<PlannedOperation>();
        for (var i = 1; i <= count; i++)
        {
            AddFile($@"D:\Archive\2026\m{i}.jpg", i);
            ops.Add(new PlannedOperation($"op{i}", OperationKind.Move, $@"D:\Archive\2026\m{i}.jpg", $@"D:\Archive\2027\m{i}.jpg", 64,
                Array.Empty<ReviewReason>()));
        }

        return PlanOf([.. ops]);
    }

    /// <summary>다른 드라이브의 파일 <paramref name="count"/>개를 보관 폴더로 복사하는 계획.</summary>
    private Plan CopyPlan(int count)
    {
        var ops = new List<PlannedOperation>();
        for (var i = 1; i <= count; i++)
        {
            AddFile($@"E:\Source\c{i}.jpg", 100 + i);
            ops.Add(new PlannedOperation($"op{i}", OperationKind.Copy, $@"E:\Source\c{i}.jpg", $@"D:\Archive\2026\c{i}.jpg", 64,
                Array.Empty<ReviewReason>()));
        }

        return PlanOf([.. ops]);
    }

    private static ScanFacts Reorganize(int kept = 0, int review = 0, int duplicates = 0, int ignored = 0, int errors = 0) =>
        new(errors, ignored, false, JobKind.Reorganize, AnyMoved: true, KeptInPlace: kept, ReviewKept: review, DuplicateKept: duplicates);

    // ----- 재정리 -----

    [Fact]
    public async Task Reorganize_NeverShowsSafeMessage()
    {
        var vm = ForPlan(MovePlan(2), Reorganize());

        await vm.StartAsync();

        Assert.Null(vm.SafeToCleanMessage);
        Assert.Null(vm.CautionMessage);
        Assert.Empty(vm.FailureLines);
    }

    [Fact]
    public async Task Reorganize_TitleAndSummary()
    {
        var vm = ForPlan(MovePlan(2), Reorganize(kept: 3, review: 1, duplicates: 4));

        await vm.StartAsync();

        Assert.Equal("재정리를 마쳤습니다", vm.ResultTitle);
        Assert.Equal(
            ["옮긴 파일 2개", "그대로 둔 파일 3개", "판단 필요로 그대로 둔 파일 1개", "같은 파일이 있어 그대로 둔 파일 4개"],
            vm.SummaryLines);
    }

    [Fact]
    public async Task Reorganize_ZeroCounts_ShowsOnlyMovedLine()
    {
        var vm = ForPlan(MovePlan(1), Reorganize());

        await vm.StartAsync();

        Assert.Equal(["옮긴 파일 1개"], vm.SummaryLines);
    }

    [Fact]
    public async Task Reorganize_MovedLine_CountsFilesNotTemporarySteps()
    {
        // 두 파일이 자리를 바꾼다: 임시 이름으로 비켜 둔 뒤 최종 자리로 옮긴다. 파일은 둘이다.
        AddFile(@"D:\Archive\2026\a.jpg", 1);
        AddFile(@"D:\Archive\2026\b.jpg", 2);
        const string tempA = @"D:\Archive\2026\a.jpg.photook-tmp-1";
        var plan = PlanOf(
            new PlannedOperation("op1", OperationKind.Move, @"D:\Archive\2026\a.jpg", tempA, 64, []),
            new PlannedOperation("op2", OperationKind.Move, @"D:\Archive\2026\b.jpg", @"D:\Archive\2027\b.jpg", 64, []),
            new PlannedOperation("op3", OperationKind.Move, tempA, @"D:\Archive\2027\a.jpg", 64, []));
        var vm = ForPlan(plan, Reorganize());

        await vm.StartAsync();

        Assert.Equal(["옮긴 파일 2개"], vm.SummaryLines);
    }

    [Fact]
    public async Task Reorganize_IgnoredFiles_UsesCautionThatFitsReorganize()
    {
        var vm = ForPlan(MovePlan(1), Reorganize(ignored: 5));

        await vm.StartAsync();

        Assert.Equal("사진·동영상이 아닌 파일 5개는 옮기지 않았습니다", vm.CautionMessage);
        Assert.Null(vm.SafeToCleanMessage);
    }

    [Fact]
    public async Task Reorganize_Cancelled_ShowsNoSummary()
    {
        var plan = MovePlan(3);
        var vm = ForPlan(plan, Reorganize(kept: 2));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunViewModel.Done) && vm.Done == 1)
            {
                vm.Cancel();
            }
        };

        await vm.StartAsync();

        Assert.True(vm.CanResume);
        Assert.Empty(vm.SummaryLines);
    }

    // ----- 흡수 -----

    [Fact]
    public async Task Absorb_Moved_NoSafeMessage_ShowsCounts()
    {
        var facts = new ScanFacts(0, 0, false, JobKind.Absorb, AnyMoved: true, AlreadyInArchive: 2);
        var vm = ForPlan(MovePlan(3), facts);

        await vm.StartAsync();

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Null(vm.SafeToCleanMessage);
        Assert.Equal(["옮긴 파일 3개", "이미 있어 남긴 파일 2개"], vm.SummaryLines);
    }

    [Fact]
    public async Task Absorb_Moved_NoAlreadyInArchive_ShowsOnlyMovedLine()
    {
        var facts = new ScanFacts(0, 0, false, JobKind.Absorb, AnyMoved: true);
        var vm = ForPlan(MovePlan(1), facts);

        await vm.StartAsync();

        Assert.Equal(["옮긴 파일 1개"], vm.SummaryLines);
    }

    [Theory]
    [InlineData(0, 0, false, true)]
    [InlineData(1, 0, false, false)]
    [InlineData(0, 3, false, false)]
    [InlineData(0, 0, true, false)]
    [InlineData(2, 3, true, false)]
    public async Task Absorb_CopyOnly_SafeMessageRulesSameAsImport(int scanErrors, int ignored, bool subfolders, bool safe)
    {
        var facts = new ScanFacts(scanErrors, ignored, subfolders, JobKind.Absorb, AnyMoved: false);
        var vm = ForPlan(CopyPlan(2), facts);

        await vm.StartAsync();

        Assert.Equal(safe ? SafeMessage : null, vm.SafeToCleanMessage);
    }

    [Fact]
    public async Task Absorb_AlreadyInArchive_DoesNotBlockSafeMessage()
    {
        var facts = new ScanFacts(0, 0, false, JobKind.Absorb, AnyMoved: false, AlreadyInArchive: 4);
        var vm = ForPlan(CopyPlan(2), facts);

        await vm.StartAsync();

        Assert.Equal(SafeMessage, vm.SafeToCleanMessage); // 그 내용은 아카이브에 이미 있다
        Assert.Contains("이미 있어 남긴 파일 4개", vm.SummaryLines);
    }

    [Theory]
    [InlineData(SkipReason.SubfolderKeptInPlace)]
    [InlineData(SkipReason.ReviewKeptInPlace)]
    [InlineData(SkipReason.OtherVolume)]
    [InlineData(SkipReason.AlreadyInPlace)]
    public async Task Absorb_CopyPlan_WithFileLeftInPlace_NoSafeMessage(SkipReason stay)
    {
        var plan = CopyPlan(2) with { Skipped = [Skip(stay, "left.jpg")] };
        var facts = ScanFacts.From(NoScan, JobKind.Absorb, plan);
        var vm = ForPlan(plan, facts);

        await vm.StartAsync();

        Assert.False(facts.AnyMoved);
        Assert.Equal(1, facts.KeptInPlace);
        Assert.Null(vm.SafeToCleanMessage); // 그 파일은 복사하지 않았다
        Assert.Equal(["복사한 파일 2개", "그대로 둔 파일 1개"], vm.SummaryLines);
    }

    [Theory]
    [InlineData(SkipReason.DuplicateInArchive)]
    [InlineData(SkipReason.DuplicateAtDestination)]
    [InlineData(SkipReason.DuplicateInPlan)]
    public async Task Absorb_CopyPlan_DuplicatesOnly_KeepsSafeMessage(SkipReason duplicate)
    {
        var plan = CopyPlan(2) with { Skipped = [Skip(duplicate, "dup.jpg"), Skip(duplicate, "dup2.jpg")] };
        var facts = ScanFacts.From(NoScan, JobKind.Absorb, plan);
        var vm = ForPlan(plan, facts);

        await vm.StartAsync();

        Assert.Equal(SafeMessage, vm.SafeToCleanMessage);
        Assert.Equal(["복사한 파일 2개", "이미 있어 남긴 파일 2개"], vm.SummaryLines);
    }

    [Fact]
    public async Task Absorb_CopyOnly_SaysCopied_NotMoved()
    {
        var vm = ForPlan(CopyPlan(2), new ScanFacts(0, 0, false, JobKind.Absorb));

        await vm.StartAsync();

        Assert.Equal(["복사한 파일 2개"], vm.SummaryLines);
    }

    [Fact]
    public async Task Absorb_MixedRun_ShowsBothMovedAndCopied()
    {
        var plan = PlanOf([.. MovePlan(2).Operations, .. CopyPlan(1).Operations.Select(op => op with { Id = "opc" })]);
        var vm = ForPlan(plan, new ScanFacts(0, 0, false, JobKind.Absorb, AnyMoved: true));

        await vm.StartAsync();

        Assert.Equal(["옮긴 파일 2개", "복사한 파일 1개"], vm.SummaryLines);
        Assert.Null(vm.SafeToCleanMessage);
    }

    [Fact]
    public async Task Reorganize_ScanErrors_CautionDropsOriginalClause()
    {
        var vm = ForPlan(MovePlan(1), Reorganize(errors: 2));

        await vm.StartAsync();

        Assert.Equal("스캔할 때 읽지 못한 파일이 2개 있습니다", vm.CautionMessage);
    }

    [Fact]
    public async Task Absorb_ScanErrors_KeepsOriginalClause()
    {
        var vm = ForPlan(CopyPlan(1), new ScanFacts(2, 0, false, JobKind.Absorb));

        await vm.StartAsync();

        Assert.Equal("스캔할 때 읽지 못한 파일이 2개 있습니다. 원본을 직접 정리하기 전에 그 파일을 확인해 주세요", vm.CautionMessage);
    }

    // ----- 가져오기와 이어 하기 -----

    [Fact]
    public async Task Import_Unchanged()
    {
        // 새 값이 들어 있어도 가져오기는 요약 줄이 없고 안전 문구 규칙도 그대로다.
        var facts = new ScanFacts(0, 0, false, JobKind.Import, AnyMoved: true, KeptInPlace: 2, ReviewKept: 1, AlreadyInArchive: 3);
        var vm = ForPlan(CopyPlan(2), facts);

        await vm.StartAsync();

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Equal(SafeMessage, vm.SafeToCleanMessage);
        Assert.Empty(vm.SummaryLines);
        Assert.Null(vm.CautionMessage);
    }

    [Fact]
    public async Task Import_IgnoredFiles_KeepsOriginalCaution()
    {
        var vm = ForPlan(CopyPlan(1), new ScanFacts(0, 3, false));

        await vm.StartAsync();

        Assert.Equal("사진·동영상이 아닌 파일 3개는 가져오지 않았습니다. 원본을 정리하기 전에 확인해 주세요", vm.CautionMessage);
        Assert.Empty(vm.SummaryLines);
    }

    [Fact]
    public async Task ResumedFromHistory_HasNoSummaryLines()
    {
        var plan = MovePlan(2);
        var first = ForPlan(plan, Reorganize());
        var cancelAfterFirst = first;
        cancelAfterFirst.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunViewModel.Done) && cancelAfterFirst.Done == 1)
            {
                cancelAfterFirst.Cancel();
            }
        };
        await first.StartAsync();
        var journal = _fs.AllFiles().First(p => p.StartsWith(@"D:\Archive\.photook\journal\", StringComparison.OrdinalIgnoreCase));

        var resumed = RunViewModel.ForResume(_shell, NewExecutor(), _fs, new FileLog(_fs, LogFolder), () => _home, () => _home, journal);
        await resumed.StartAsync();

        Assert.Equal("정리를 마쳤습니다", resumed.ResultTitle);
        Assert.Empty(resumed.SummaryLines);
    }

    // ----- ScanFacts 만들기 -----

    private static readonly PhotoOk.Core.Workflow.ScanOutcome NoScan = new([], [], [], 0, false);

    private static PlannedSkip Skip(SkipReason reason, string name) => new($@"D:\Archive\{name}", $@"D:\Archive\{name}", reason);

    private static Plan PlanWithSkips(IReadOnlyList<PlannedOperation> ops, params PlannedSkip[] skips) =>
        new(Archive, ops, skips, Array.Empty<string>());

    private static PlannedOperation Op(OperationKind kind) =>
        new("op", kind, @"D:\Archive\x.jpg", @"D:\Archive\y.jpg", 1, []);

    [Fact]
    public void From_Reorganize_CountsSkipsBySameRulesAsPreview()
    {
        var plan = PlanWithSkips([Op(OperationKind.Move)],
            Skip(SkipReason.AlreadyInPlace, "a"), Skip(SkipReason.SubfolderKeptInPlace, "b"), Skip(SkipReason.OtherVolume, "c"),
            Skip(SkipReason.ReviewKeptInPlace, "d"), Skip(SkipReason.ReviewKeptInPlace, "e"),
            Skip(SkipReason.DuplicateAtDestination, "f"), Skip(SkipReason.DuplicateInPlan, "g"),
            Skip(SkipReason.DuplicateInArchive, "h"));
        var scan = new PhotoOk.Core.Workflow.ScanOutcome([], [], [], 0, false);

        var facts = ScanFacts.From(scan, JobKind.Reorganize, plan);

        Assert.Equal(JobKind.Reorganize, facts.Kind);
        Assert.True(facts.AnyMoved);
        Assert.Equal(3, facts.KeptInPlace);  // 이미 제자리, 하위 폴더, 다른 볼륨(판단 필요는 따로)
        Assert.Equal(2, facts.ReviewKept);
        Assert.Equal(2, facts.DuplicateKept); // 같은 파일이 있어 그대로 둔 파일
        Assert.Equal(0, facts.AlreadyInArchive);
    }

    [Fact]
    public void From_Absorb_CountsDuplicatesAsAlreadyThere_AndStaysAsKeptInPlace()
    {
        var plan = PlanWithSkips([Op(OperationKind.Copy), Op(OperationKind.Move)],
            Skip(SkipReason.DuplicateInArchive, "a"), Skip(SkipReason.DuplicateAtDestination, "b"), Skip(SkipReason.DuplicateInPlan, "c"),
            Skip(SkipReason.AlreadyInPlace, "d"), Skip(SkipReason.SubfolderKeptInPlace, "e"), Skip(SkipReason.OtherVolume, "f"),
            Skip(SkipReason.ReviewKeptInPlace, "g"));
        var scan = new PhotoOk.Core.Workflow.ScanOutcome([], [], [], 0, false);

        var facts = ScanFacts.From(scan, JobKind.Absorb, plan);

        Assert.True(facts.AnyMoved);
        Assert.Equal(3, facts.AlreadyInArchive); // 미리보기의 중복과 같은 수
        Assert.Equal(4, facts.KeptInPlace);      // 판단 필요도 센다
        Assert.Equal(0, facts.ReviewKept);
        Assert.Equal(0, facts.DuplicateKept);
    }

    [Fact]
    public void From_Absorb_CopyOnly_IsNotMoved()
    {
        var plan = PlanWithSkips([Op(OperationKind.Copy)]);
        var scan = new PhotoOk.Core.Workflow.ScanOutcome([], [], [], 0, false);

        Assert.False(ScanFacts.From(scan, JobKind.Absorb, plan).AnyMoved);
    }

    [Fact]
    public void From_Import_LeavesNewValuesAtDefault()
    {
        var plan = PlanWithSkips([Op(OperationKind.Move)], Skip(SkipReason.AlreadyInPlace, "a"), Skip(SkipReason.DuplicateInArchive, "b"));
        var scan = new PhotoOk.Core.Workflow.ScanOutcome([], [], [], 0, false);

        var facts = ScanFacts.From(scan, JobKind.Import, plan);

        Assert.Equal(new ScanFacts(0, 0, false), facts);
    }
}
