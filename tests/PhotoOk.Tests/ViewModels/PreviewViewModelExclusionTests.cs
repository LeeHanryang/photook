using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

/// <summary>미리보기에서 촬영 빼기, 폴더 날짜와 다른 촬영, 여러 날 폴더 옵션.</summary>
public sealed class PreviewViewModelExclusionTests : IDisposable
{
    private const string Archive = @"D:\Archive";
    private const string Old = @"D:\Archive\모음";
    private const string LogFolder = @"C:\Users\test\AppData\Local\PhotoOk\logs";
    private const string IgnoredPath = @"C:\Users\test\AppData\Roaming\PhotoOk\ignored-folder-names.txt";
    private const string NothingToMove = "옮길 파일이 없습니다";
    private const string Trip = "바닷가 나들이";
    private const string Walk = "동네 산책";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeThumbnailProvider _thumbnails = new();
    private readonly ShellViewModel _shell;
    private readonly ImportWorkflow _workflow;
    private readonly ManualResetEventSlim _gate = new(initialState: true);
    private readonly List<(Plan Plan, HashSet<string> Excluded, ImportSettings Settings)> _plans = [];
    private readonly List<ImportSettings> _saved = [];
    private readonly List<ImportSettings> _backs = [];
    private Plan? _runPlan;
    private ScanFacts? _runFacts;

    public PreviewViewModelExclusionTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(Old);
        _fs.SetFreeSpace(@"D:\", 50L * 1024 * 1024 * 1024);
        _workflow = new ImportWorkflow(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc, IgnoredPath);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class ScreenStub : ObservableObject;

    private static ImportSettings Settings(JobKind kind = JobKind.Reorganize, Preset? preset = null, string? source = null) =>
        new([source ?? Archive], true, Archive, preset ?? BuiltInPresets.DateEvent,
            kind == JobKind.Import ? TransferMode.Copy : TransferMode.Move, kind, UseFolderNames: kind != JobKind.Import);

    private byte _nextFill = 1;

    private string Add(string path, DateTime? capture, CaptureTimeSource? source = null, DateTime? lastWrite = null)
    {
        var fill = _nextFill++;
        _fs.AddFile(path, [fill, fill, fill], lastWrite);
        _reader.Set(path, capture, source: source);
        return path;
    }

    private async Task<PreviewViewModel> CreateAsync(ImportSettings? settings = null, IReadOnlyList<PresetEntry>? presets = null)
    {
        settings ??= Settings();
        var scan = await _workflow.ScanAsync(settings, null, CancellationToken.None);
        var vm = Create(settings, scan, presets);
        await vm.WhenPlanned;
        return vm;
    }

    private PreviewViewModel Create(ImportSettings settings, ScanOutcome scan, IReadOnlyList<PresetEntry>? presets = null)
    {
        var vm = new PreviewViewModel(_shell, _workflow, settings, scan, _fs, _thumbnails, _dialogs, new FileLog(_fs, LogFolder),
            createRun: (plan, facts) =>
            {
                _runPlan = plan;
                _runFacts = facts;
                return new ScreenStub();
            },
            createBack: current =>
            {
                _backs.Add(current);
                return new ScreenStub();
            },
            replanDelay: TimeSpan.Zero,
            ignoredNamesPath: IgnoredPath,
            presets: presets ?? PresetLibrary.BuiltIn,
            saveChoices: _saved.Add,
            buildPlan: (s, scanned, names, excluded) =>
            {
                _gate.Wait(Wait);
                var outcome = _workflow.BuildPlan(s, scanned, names, excluded);
                lock (_plans)
                {
                    _plans.Add((outcome.Plan, excluded.ToHashSet(StringComparer.OrdinalIgnoreCase), s));
                }

                return outcome;
            });
        _shell.Navigate(vm);
        return vm;
    }

    private Plan LastPlan => _plans[^1].Plan;

    private static List<string> Sources(Plan plan) =>
        plan.Operations.Where(op => op.Source is not null).Select(op => op.Source!).Order(StringComparer.Ordinal).ToList();

    private static IEnumerable<FolderNodeViewModel> Flatten(IEnumerable<FolderNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static List<string> NodeNames(PreviewViewModel vm) => Flatten(vm.FolderTree).Select(n => n.Name).ToList();

    private static async Task ToggleAsync(PreviewViewModel vm, SessionItemViewModel card)
    {
        card.ToggleIncludedCommand.Execute(null);
        await vm.WhenPlanned;
    }

    private static void AssertNoOperationOn(Plan plan, params string[] paths)
    {
        foreach (var path in paths)
        {
            Assert.DoesNotContain(plan.Operations, op =>
                string.Equals(op.Source, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(op.Destination, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ----- 체크 상태와 트리 -----

    [Fact]
    public async Task Default_AllIncluded()
    {
        Add($@"{Old}\a.jpg", Oct14);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsIncluded);
        Assert.Equal("이번에 옮기기", card.IncludeLabel);
        Assert.Null(card.ExcludedNote);
        Assert.Equal("제외 0개", vm.ExcludedText);
    }

    [Fact]
    public async Task Unchecking_RemovesFromTree_AndCheckingBrings_Back()
    {
        var a = Add($@"{Old}\a.jpg", Oct14);
        var b = Add($@"{Old}\b.jpg", Oct15);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        Assert.Contains("2026-10-14", NodeNames(vm));
        var card = vm.Sessions.Single(c => c.SessionId == "2026-10-14");

        await ToggleAsync(vm, card);

        Assert.False(card.IsIncluded);
        Assert.Equal("이번에 옮기기", card.IncludeLabel);
        Assert.DoesNotContain("2026-10-14", NodeNames(vm));
        Assert.Contains("2026-10-15", NodeNames(vm));
        Assert.Equal(1, vm.TransferCount);
        Assert.Equal("제외 1개", vm.ExcludedText);
        Assert.Equal([b], Sources(LastPlan));
        Assert.Equal(new PlannedSkip(a, a, SkipReason.ExcludedByUser), Assert.Single(LastPlan.Skipped));

        await ToggleAsync(vm, card);

        Assert.True(card.IsIncluded);
        Assert.Contains("2026-10-14", NodeNames(vm));
        Assert.Equal(2, vm.TransferCount);
        Assert.Equal("제외 0개", vm.ExcludedText);
    }

    [Fact]
    public async Task RawAndSidecar_LeaveTogether_AndCountAsFiles()
    {
        var raw = Add($@"{Old}\IMG.NEF", Oct14);
        var xmp = $@"{Old}\IMG.xmp";
        _fs.AddFile(xmp, [99]);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        await ToggleAsync(vm, Assert.Single(vm.Sessions));

        Assert.Empty(LastPlan.Operations);
        Assert.Equal("제외 2개", vm.ExcludedText);
        AssertNoOperationOn(LastPlan, raw, xmp);
    }

    [Fact]
    public async Task ReviewCard_CanBeExcluded()
    {
        var review = Add($@"{Old}\x.jpg", null);
        var dated = Add($@"{Old}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false, KeepReviewInPlace = false });
        var card = vm.Sessions.Single(c => c.IsReviewGroup);
        Assert.Contains(review, Sources(LastPlan));

        await ToggleAsync(vm, card);

        Assert.False(card.IsIncluded);
        Assert.Equal([dated], Sources(LastPlan));
        Assert.Equal(SkipReason.ExcludedByUser, Assert.Single(LastPlan.Skipped).Reason);
    }

    [Fact]
    public async Task ReviewCard_KeptInPlace_HidesCheckBox_UntilTheOptionMovesIt()
    {
        Add($@"{Old}\x.jpg", null);
        Add($@"{Old}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        Assert.False(vm.Sessions.Single(c => c.IsReviewGroup).ShowIncludeCheckBox);
        Assert.True(vm.Sessions.Single(c => !c.IsReviewGroup).ShowIncludeCheckBox);

        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        Assert.True(vm.Sessions.Single(c => c.IsReviewGroup).ShowIncludeCheckBox);
    }

    [Fact]
    public void Card_HiddenCheckBox_GivesNoExcludedNote()
    {
        var card = new SessionItemViewModel("판단 필요", "판단 필요 · 2장 (그대로 둠)", "", true, [@"D:\a.jpg", @"D:\b.jpg"], null, null, showIncludeCheckBox: false);

        card.SetExclusion(1, 1);

        Assert.Null(card.ExcludedNote);
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task OtherJobs_CanExclude_AndFactsCarryTheCount(JobKind kind)
    {
        const string inbox = @"D:\흩어짐";
        var a = Add($@"{inbox}\a.jpg", Oct14);
        var b = Add($@"{inbox}\b.jpg", Oct15);
        var vm = await CreateAsync(Settings(kind, source: inbox));

        await ToggleAsync(vm, vm.Sessions.Single(c => c.Files.Contains(a)));
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal([b], Sources(_runPlan!));
        Assert.Equal(1, _runFacts!.Excluded);
    }

    [Fact]
    public async Task EventNameEdit_KeepsExclusion()
    {
        var a = Add($@"{Old}\a.jpg", Oct14);
        Add($@"{Old}\b.jpg", Oct15);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        await ToggleAsync(vm, vm.Sessions.Single(c => c.Files.Contains(a)));

        vm.Sessions.Single(c => !c.Files.Contains(a)).EventName = "소풍";
        await vm.WhenPlanned;

        AssertNoOperationOn(LastPlan, a);
        Assert.False(vm.Sessions.Single(c => c.Files.Contains(a)).IsIncluded);
    }

    // ----- 실행 잠금 -----

    [Fact]
    public async Task Toggle_BlocksExecute_UntilTheNewPlanArrives()
    {
        var a = Add($@"{Old}\a.jpg", Oct14);
        Add($@"{Old}\b.jpg", Oct15);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        Assert.True(vm.ExecuteCommand.CanExecute(null));

        _gate.Reset();
        vm.Sessions.Single(c => c.Files.Contains(a)).ToggleIncludedCommand.Execute(null);

        Assert.False(vm.ExecuteCommand.CanExecute(null));
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Null(_runPlan); // 옛 계획으로 실행되지 않는다

        _gate.Set();
        await vm.WhenPlanned;

        Assert.True(vm.ExecuteCommand.CanExecute(null));
        await vm.ExecuteCommand.ExecuteAsync(null);
        AssertNoOperationOn(_runPlan!, a);
    }

    [Fact]
    public async Task TogglesWhilePlanning_LastPlanHasThemAll()
    {
        var a = Add($@"{Old}\a.jpg", Oct14);
        var b = Add($@"{Old}\b.jpg", Oct15);
        var c = Add($@"{Old}\c.jpg", new DateTime(2026, 10, 16, 9, 0, 0));
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        _gate.Reset();
        vm.Sessions.Single(card => card.Files.Contains(a)).ToggleIncludedCommand.Execute(null);
        vm.Sessions.Single(card => card.Files.Contains(b)).ToggleIncludedCommand.Execute(null);
        _gate.Set();
        await vm.WhenPlanned;
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal([c], Sources(_runPlan!));
    }

    [Fact]
    public async Task DiscardedCard_ToggleIsIgnored()
    {
        var a = Add($@"{Old}\{Trip}\a.jpg", Oct14);
        var vm = await CreateAsync();
        var old = Assert.Single(vm.Sessions);
        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        var plans = _plans.Count;

        await ToggleAsync(vm, old);

        Assert.Equal(plans, _plans.Count);
        Assert.Contains(a, Sources(LastPlan));
    }

    // ----- 옵션을 바꿔도 뺀 파일은 그대로 -----

    private async Task<(PreviewViewModel Vm, string Excluded, string Other)> TwoNamedCardsOneExcludedAsync()
    {
        var excluded = Add($@"{Old}\{Trip}\a.jpg", Oct14);
        var other = Add($@"{Old}\{Walk}\b.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.Equal(2, vm.Sessions.Count);
        await ToggleAsync(vm, vm.Sessions.Single(c => c.Files.Contains(excluded)));
        return (vm, excluded, other);
    }

    [Fact]
    public async Task OptionChange_KeepsExclusion_WhenCardsStay()
    {
        var (vm, excluded, other) = await TwoNamedCardsOneExcludedAsync();

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;

        Assert.False(vm.Sessions.Single(c => c.Files.Contains(excluded)).IsIncluded);
        Assert.Equal([other], Sources(LastPlan));
    }

    [Fact]
    public async Task CardsMerge_ExcludedStaysOut_AndCardIsPartial()
    {
        var (vm, excluded, other) = await TwoNamedCardsOneExcludedAsync();

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.IsIncluded);
        Assert.Equal("일부", card.IncludeLabel);
        Assert.Equal("뺀 파일 1개", card.ExcludedNote);
        Assert.Equal([other], Sources(LastPlan));
        AssertNoOperationOn(LastPlan, excluded);
    }

    [Fact]
    public async Task PartialCard_Click_IncludesAll_ThenChecked_Click_ExcludesAll()
    {
        var (vm, excluded, other) = await TwoNamedCardsOneExcludedAsync();
        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        var card = Assert.Single(vm.Sessions);

        await ToggleAsync(vm, card);

        Assert.True(card.IsIncluded);
        Assert.Equal("이번에 옮기기", card.IncludeLabel);
        Assert.Null(card.ExcludedNote);
        Assert.Equal(new[] { excluded, other }.Order(StringComparer.Ordinal), Sources(LastPlan));

        await ToggleAsync(vm, card);

        Assert.False(card.IsIncluded);
        Assert.Empty(LastPlan.Operations);
    }

    [Fact]
    public async Task UncheckedCard_Click_IncludesAll()
    {
        var (vm, excluded, _) = await TwoNamedCardsOneExcludedAsync();
        var card = vm.Sessions.Single(c => c.Files.Contains(excluded));
        Assert.False(card.IsIncluded);

        await ToggleAsync(vm, card);

        Assert.True(card.IsIncluded);
        Assert.Contains(excluded, Sources(LastPlan));
    }

    [Fact]
    public async Task MergedCardExcluded_ThenSplit_BothCardsStayOut()
    {
        var a = Add($@"{Old}\{Trip}\a.jpg", Oct14);
        var b = Add($@"{Old}\{Walk}\b.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        await ToggleAsync(vm, Assert.Single(vm.Sessions));

        vm.UseFolderNames = true;
        await vm.WhenPlanned;

        Assert.Equal(2, vm.Sessions.Count);
        Assert.All(vm.Sessions, card => Assert.False(card.IsIncluded));
        Assert.Empty(LastPlan.Operations);
        AssertNoOperationOn(LastPlan, a, b);
    }

    [Fact]
    public async Task EveryOptionFlip_NeverGivesExcludedFileAnOperation()
    {
        var (vm, excluded, _) = await TwoNamedCardsOneExcludedAsync();
        var start = _plans.Count;

        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        vm.SplitUnderscores = true;
        await vm.WhenPlanned;
        vm.Subfolders = SubfolderPolicy.InPlace;
        await vm.WhenPlanned;
        vm.UseFolderNames = true;
        await vm.WhenPlanned;
        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;
        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;
        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;
        foreach (var preset in vm.Presets)
        {
            vm.SelectedPreset = preset;
            await vm.WhenPlanned;
        }

        Assert.True(_plans.Count >= start + 7);
        foreach (var (plan, set, _) in _plans.Skip(start))
        {
            Assert.Contains(excluded, set);
            AssertNoOperationOn(plan, excluded);
            Assert.Contains(plan.Skipped, s => s.Source == excluded && s.Reason == SkipReason.ExcludedByUser);
        }
    }

    // ----- 모두 옮기기·모두 빼기 -----

    [Fact]
    public async Task ExcludeAll_TurnsOffExecute_AndIncludeAllRestores()
    {
        var a = Add($@"{Old}\a.jpg", Oct14);
        var b = Add($@"{Old}\b.jpg", Oct15);
        var c = Add($@"{Old}\x.jpg", null);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false, KeepReviewInPlace = false });

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.All(vm.Sessions, card => Assert.False(card.IsIncluded));
        Assert.Empty(LastPlan.Operations);
        Assert.Equal("제외 3개", vm.ExcludedText);
        Assert.Equal(NothingToMove, vm.BlockingReason);
        Assert.False(vm.ExecuteCommand.CanExecute(null));

        vm.IncludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.All(vm.Sessions, card => Assert.True(card.IsIncluded));
        Assert.Equal([a, b, c], Sources(LastPlan).Where(s => !s.Contains("tmp", StringComparison.Ordinal)));
        Assert.Null(vm.BlockingReason);
        Assert.True(vm.ExecuteCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task ExcludeAll_OtherJobs_ShowsSameMessage(JobKind kind)
    {
        const string inbox = @"D:\흩어짐";
        Add($@"{inbox}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(kind, source: inbox));

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(NothingToMove, vm.BlockingReason);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingExcluded_NoMoves_KeepsOldMessage()
    {
        Add(@"D:\Archive\2026\2026-10-14\a.jpg", Oct14);

        var vm = await CreateAsync(Settings() with { UseFolderNames = false, Preset = BuiltInPresets.DateOnly });

        Assert.Equal("옮길 파일이 없습니다 — 모두 제자리에 있습니다", vm.BlockingReason);
    }

    [Fact]
    public async Task IncludeAll_NothingToChange_DoesNotReplan()
    {
        Add($@"{Old}\a.jpg", Oct14);
        var vm = await CreateAsync();
        var plans = _plans.Count;

        vm.IncludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(plans, _plans.Count);
    }

    [Fact]
    public async Task ExcludeAll_Twice_ReplansOnce()
    {
        Add($@"{Old}\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;
        var plans = _plans.Count;

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(plans, _plans.Count);
    }

    // ----- 폴더 날짜와 다른 촬영 -----

    [Theory]
    [InlineData(@"D:\Archive\[2026-10-15] 일상\a.jpg")]
    [InlineData(@"D:\Archive\2026\2026-10-15\a.jpg")]
    [InlineData(@"D:\Archive\2026-10-15 일상\라이트룸\a.jpg")]
    public async Task Mismatch_ShownAndExcludedByDefault(string path)
    {
        Add(path, Oct14);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        const string text = "폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보)";
        Assert.Equal(text, card.DateMismatch);
        Assert.Equal(text, vm.Detail?.DateMismatch);
        Assert.Equal("폴더 날짜와 다른 촬영 1개", vm.DateMismatchText);
        Assert.False(card.IsIncluded);
        AssertNoOperationOn(LastPlan, path);
        Assert.Contains(_plans[0].Excluded, id => string.Equals(id, path, StringComparison.OrdinalIgnoreCase)); // 첫 계획부터 빠져 있다
        Assert.Equal(SkipReason.ExcludedByUser, Assert.Single(LastPlan.Skipped).Reason);
    }

    [Theory]
    [InlineData(@"D:\Archive\[2026-10-14] 일상\a.jpg")] // 같은 날짜
    [InlineData(@"D:\Archive\모음\a.jpg")] // 날짜 없는 폴더
    [InlineData(@"D:\Archive\2026.10.15\a.jpg")] // yyyy-MM-dd만 읽는다
    public async Task NoMismatch_NothingShown_NothingExcluded(string path)
    {
        Add(path, Oct14);

        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly });

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.DateMismatch);
        Assert.Null(vm.Detail?.DateMismatch);
        Assert.Null(vm.DateMismatchText);
        Assert.True(card.IsIncluded);
    }

    [Fact]
    public async Task RangeFolder_IsNotADateFolder()
    {
        Add(@"D:\Archive\[2026-10-15~10-16] 소풍\a.jpg", Oct14);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.DateMismatch);
        Assert.True(card.IsIncluded);
    }

    [Theory]
    [InlineData(CaptureTimeSource.Photo, "사진 정보")]
    [InlineData(CaptureTimeSource.Video, "사진 정보")]
    [InlineData(CaptureTimeSource.FileName, "파일 이름")]
    public async Task Mismatch_NamesTheDateSource(CaptureTimeSource source, string label)
    {
        Add(@"D:\Archive\2026-10-15\a.jpg", Oct14, source);

        var vm = await CreateAsync();

        Assert.Equal($"폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 ({label})", vm.Sessions[0].DateMismatch);
    }

    [Fact]
    public async Task NoCaptureTime_MovableToReviewFolder_SaysSo_AndDoesNotCallItAPhotoDate()
    {
        var path = Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings() with { KeepReviewInPlace = false });

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        const string text = "폴더 날짜 2026-10-14 · 촬영 시각 없음 (수정 시각 2026-11-01)";
        Assert.Equal(text, card.DateMismatch);
        Assert.Equal(text, vm.Detail?.DateMismatch);
        Assert.False(card.IsIncluded);
        AssertNoOperationOn(LastPlan, path);
    }

    [Fact]
    public async Task NoCaptureTime_KeptInPlaceAsReview_IsNotMismatched_NorExcluded()
    {
        var path = Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.DateMismatch);
        Assert.Null(vm.Detail?.DateMismatch);
        Assert.Null(vm.DateMismatchText);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Empty(_plans[0].Excluded);
        Assert.Equal(new PlannedSkip(path, path, SkipReason.ReviewKeptInPlace), Assert.Single(LastPlan.Skipped));
    }

    [Fact]
    public async Task ReviewOptionOff_ExcludesOnceAtThatPoint_ThenStaysAsTheUserLeftIt()
    {
        var path = Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        var vm = await CreateAsync();

        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        // 판단 필요 폴더로 움직일 수 있게 된 바로 그 계획부터 빠진다.
        Assert.False(vm.Sessions[0].IsIncluded);
        Assert.NotNull(vm.Sessions[0].DateMismatch);
        Assert.Contains(path, _plans[^1].Excluded);
        AssertNoOperationOn(LastPlan, path);

        await ToggleAsync(vm, vm.Sessions[0]);
        Assert.Contains(path, Sources(LastPlan));

        vm.KeepReviewInPlace = true;
        await vm.WhenPlanned;
        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        Assert.True(vm.Sessions[0].IsIncluded); // 켠 묶음은 다시 빼지 않는다
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task NoCaptureTime_ComparesTheModifiedDate_WithoutDayStart()
    {
        // 수정 시각이 새벽이어도 달력의 날짜로 본다. 폴더와 같은 날이므로 다르다고 하지 않는다.
        Add(@"D:\Archive\2026-11-01\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc));
        var preset = BuiltInPresets.DateOnly with { DayStart = new TimeOnly(4, 0) };

        var vm = await CreateAsync(Settings(preset: preset) with { KeepReviewInPlace = false });

        Assert.Null(Assert.Single(vm.Sessions).DateMismatch);
        Assert.True(vm.Sessions[0].IsIncluded);
    }

    [Fact]
    public async Task NoCaptureTime_SeveralOnOneCard_CountedSeparately()
    {
        Add(@"D:\Archive\2026-10-14\a.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        Add(@"D:\Archive\2026-10-15\b.jpg", null, lastWrite: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings() with { KeepReviewInPlace = false, UseFolderNames = false });

        Assert.Equal("촬영 시각 없는 파일 2개", Assert.Single(vm.Sessions).DateMismatch);
    }

    [Fact]
    public async Task ReviewCard_MixedCaptureAndNoCapture_CountsEach()
    {
        // 기기를 쓰는 프리셋에서 기기를 모르면 촬영 시각이 있어도 판단 필요다.
        Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-16\b.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateDeviceFormat) with { KeepReviewInPlace = false, UseFolderNames = false });

        Assert.Equal("폴더 날짜가 다른 파일 1개 · 촬영 시각 없는 파일 1개", Assert.Single(vm.Sessions).DateMismatch);
    }

    [Fact]
    public async Task MultiDayKept_InnerDateFolderDiffers_IsKept_NotExcluded()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\a.jpg", Oct15);

        var vm = await CreateAsync();

        Assert.Null(Assert.Single(vm.Sessions).DateMismatch);
        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Equal("그대로 1개", vm.KeptInPlaceText);
        Assert.Equal("여러 날 폴더 1개는 옮기지 않습니다", vm.MultiDayNotice);
        Assert.Equal(new PlannedSkip(path, path, SkipReason.MultiDayFolderKept), Assert.Single(LastPlan.Skipped));
    }

    [Fact]
    public async Task SplitOn_ExcludesOnceAtThatPoint_ThenStaysAsTheUserLeftIt()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\a.jpg", Oct15);
        var vm = await CreateAsync();

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.Equal("폴더 날짜 2026-10-14 → 사진 날짜 2026-10-15 (사진 정보)", vm.Sessions[0].DateMismatch);
        Assert.False(vm.Sessions[0].IsIncluded);
        Assert.Contains(path, _plans[^1].Excluded);
        AssertNoOperationOn(LastPlan, path);

        await ToggleAsync(vm, vm.Sessions[0]);
        Assert.Contains(path, Sources(LastPlan));

        vm.SplitMultiDayFolders = false;
        await vm.WhenPlanned;
        Assert.Empty(LastPlan.Operations);
        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task Mismatch_DayStartCorrection_IsNamed()
    {
        // 15일 새벽 사진이 하루 시작 시각 때문에 14일 촬영이 되었다.
        Add(@"D:\Archive\2026-10-15\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0));
        var preset = BuiltInPresets.DateOnly with { DayStart = new TimeOnly(4, 0) };

        var vm = await CreateAsync(Settings(preset: preset));

        Assert.Equal("폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보, 하루 시작 시각 보정)", vm.Sessions[0].DateMismatch);
    }

    [Fact]
    public async Task Mismatch_SeveralFolderDates_ShowsFileCount()
    {
        var same = Add(@"D:\Archive\2026-10-14\a.jpg", Oct14);
        var day15 = Add(@"D:\Archive\2026-10-15\b.jpg", Oct14);
        var day16 = Add(@"D:\Archive\2026-10-16\c.jpg", Oct14);

        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly, UseFolderNames = false });

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("폴더 날짜가 다른 파일 2개", card.DateMismatch);
        Assert.Equal("폴더 날짜와 다른 촬영 1개", vm.DateMismatchText);
        Assert.Null(card.IsIncluded); // 다른 파일만 빠진다
        Assert.Equal("뺀 파일 2개", card.ExcludedNote);
        AssertNoOperationOn(LastPlan, day15, day16);
        Assert.True(_fs.FileExists(same));
    }

    [Fact]
    public async Task Mismatch_SameFolderDateWithMatchingFiles_ShowsFileCount()
    {
        Add(@"D:\Archive\2026-10-14\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-15\b.jpg", Oct14);

        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly, UseFolderNames = false });

        Assert.Equal("폴더 날짜가 다른 파일 1개", Assert.Single(vm.Sessions).DateMismatch);
    }

    [Fact]
    public async Task Mismatch_MixedSources_NamesEach()
    {
        Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-15\b.jpg", Oct14, CaptureTimeSource.FileName);

        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        Assert.Equal("폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보, 파일 이름)", Assert.Single(vm.Sessions).DateMismatch);
    }

    [Fact]
    public async Task Mismatch_RawSidecarCounted_InFileCount()
    {
        Add(@"D:\Archive\2026-10-14\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-15\IMG.NEF", Oct14);
        _fs.AddFile(@"D:\Archive\2026-10-15\IMG.xmp", [99]);

        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly, UseFolderNames = false });

        Assert.Equal("폴더 날짜가 다른 파일 2개", Assert.Single(vm.Sessions).DateMismatch);
        Assert.Equal("제외 2개", vm.ExcludedText);
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task Mismatch_OnlyForReorganize(JobKind kind)
    {
        var path = Add(@"D:\흩어짐\2026-10-15\a.jpg", Oct14);

        var vm = await CreateAsync(Settings(kind, source: @"D:\흩어짐"));

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.DateMismatch);
        Assert.Null(vm.DateMismatchText);
        Assert.True(card.IsIncluded);
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task Mismatch_DetailShowsOwnFile_OnMixedCard()
    {
        Add(@"D:\Archive\2026-10-15\b.jpg", Oct14);
        Add(@"D:\Archive\2026-10-16\c.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        vm.SelectedThumbnail = vm.Thumbnails.Single(t => t.Name == "c.jpg");

        Assert.Equal("폴더 날짜 2026-10-16 → 사진 날짜 2026-10-14 (사진 정보)", vm.Detail?.DateMismatch);
    }

    // ----- 날짜가 달라 뺀 묶음은 한 번만 뺀다 -----

    [Fact]
    public async Task Mismatch_AfterUserIncludes_NotAddedAgain_AcrossRegroupsAndPresets()
    {
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        var vm = await CreateAsync();
        await ToggleAsync(vm, Assert.Single(vm.Sessions));
        Assert.Contains(path, Sources(LastPlan));

        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        Assert.True(Assert.Single(vm.Sessions).IsIncluded);
        Assert.Contains(path, Sources(LastPlan));

        vm.SelectedPreset = vm.Presets.Single(p => p.Name == "날짜만");
        await vm.WhenPlanned;
        Assert.True(Assert.Single(vm.Sessions).IsIncluded);
        Assert.Contains(path, Sources(LastPlan));
        Assert.Equal("폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보)", vm.Sessions[0].DateMismatch);
    }

    [Fact]
    public async Task IncludeAll_ClearsMismatchDefaults_AndTheyStayCleared()
    {
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.False(vm.Sessions[0].IsIncluded);

        vm.IncludeAllCommand.Execute(null);
        await vm.WhenPlanned;
        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Contains(path, Sources(LastPlan));

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task ExcludeAll_ThenIncludeAll_MismatchNotAddedAgain()
    {
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        var vm = await CreateAsync();

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;
        vm.IncludeAllCommand.Execute(null);
        await vm.WhenPlanned;
        vm.SplitUnderscores = true;
        await vm.WhenPlanned;

        Assert.Contains(path, Sources(LastPlan));
    }

    private static IReadOnlyList<PresetEntry> PresetsWithDayStart(TimeOnly dayStart) =>
    [
        new("보통", BuiltInPresets.DateOnly, "", null),
        new("새벽까지", BuiltInPresets.DateOnly with { Name = "새벽까지", DayStart = dayStart }, "", null),
    ];

    [Fact]
    public async Task PresetChange_NewMismatch_IsExcludedOnce_InTheSamePlan()
    {
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0));
        var presets = PresetsWithDayStart(new TimeOnly(4, 0));
        var vm = await CreateAsync(Settings(preset: presets[0].Preset), presets);
        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Null(vm.Sessions[0].DateMismatch);

        vm.SelectedPreset = presets[1];
        await vm.WhenPlanned;

        // 하루 시작 시각 때문에 14일이 되어 폴더 날짜와 달라졌다. 이 파일이 움직이는 계획은 한 번도 반영되지 않는다.
        var card = Assert.Single(vm.Sessions);
        Assert.NotNull(card.DateMismatch);
        Assert.False(card.IsIncluded);
        Assert.Contains(path, _plans[^1].Excluded);
        AssertNoOperationOn(LastPlan, path);
        Assert.Equal("제외 1개", vm.ExcludedText);

        await ToggleAsync(vm, card);
        Assert.Contains(path, Sources(LastPlan));

        vm.SelectedPreset = presets[0];
        await vm.WhenPlanned;
        vm.SelectedPreset = presets[1];
        await vm.WhenPlanned;

        Assert.True(Assert.Single(vm.Sessions).IsIncluded); // 켠 묶음은 다시 빼지 않는다
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task IncludeAll_WhilePlanIsInFlight_PreviewMatchesWhatRuns()
    {
        // 다음 계획이 도는 동안 새로 날짜가 달라진 묶음을 그 계획이 빼고 있다. 그때 모두 옮기기를 누르면 그 계획은 쓰이면 안 된다.
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0));
        var other = Add($@"{Old}\b.jpg", new DateTime(2026, 10, 20, 12, 0, 0));
        var presets = PresetsWithDayStart(new TimeOnly(4, 0));
        var vm = await CreateAsync(Settings(preset: presets[0].Preset) with { UseFolderNames = false }, presets);
        Assert.Equal("제외 0개", vm.ExcludedText);

        _gate.Reset();
        vm.SelectedPreset = presets[1];
        vm.IncludeAllCommand.Execute(null);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        _gate.Set();
        await vm.WhenPlanned;

        Assert.All(vm.Sessions, card => Assert.True(card.IsIncluded));
        Assert.Equal("제외 0개", vm.ExcludedText);
        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(new[] { path, other }.Order(StringComparer.Ordinal), Sources(_runPlan!));
        Assert.Equal(0, _runFacts!.Excluded);
    }

    [Fact]
    public async Task IncludeAll_WhilePlanIsInFlight_WithNothingExcluded_Replans()
    {
        Add($@"{Old}\a.jpg", Oct14);
        var vm = await CreateAsync();
        _gate.Reset();
        vm.UseFolderNames = false;
        var inFlight = _plans.Count;

        vm.IncludeAllCommand.Execute(null);
        _gate.Set();
        await vm.WhenPlanned;

        Assert.Equal(inFlight + 2, _plans.Count); // 돌던 계획은 버리고 하나 더 세운다
        Assert.Empty(_plans[^1].Excluded);
    }

    [Fact]
    public async Task PresetChange_MismatchGone_DefaultStaysUntilUserIncludes()
    {
        var path = Add(@"D:\Archive\2026-10-14\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0));
        var presets = PresetsWithDayStart(new TimeOnly(4, 0));
        var vm = await CreateAsync(Settings(preset: presets[0].Preset), presets);
        Assert.False(vm.Sessions[0].IsIncluded); // 폴더는 14일, 사진은 15일

        vm.SelectedPreset = presets[1];
        await vm.WhenPlanned;

        Assert.Null(vm.Sessions[0].DateMismatch); // 이제 같은 날이다
        Assert.False(vm.Sessions[0].IsIncluded); // 저절로 다시 포함되지 않는다
        Assert.Contains(path, _plans[^1].Excluded);
    }

    // ----- 여러 날 폴더 옵션 -----

    [Fact]
    public async Task MultiDay_DefaultOff_KeptInSummary_WithNote()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", Oct14);
        var moved = Add($@"{Old}\b.jpg", Oct14);

        var vm = await CreateAsync();

        Assert.False(vm.SplitMultiDayFolders);
        Assert.Equal("그대로 1개", vm.KeptInPlaceText);
        Assert.Equal("여러 날 폴더 1개는 옮기지 않습니다", vm.MultiDayNotice);
        Assert.Empty(vm.PlanNotes);
        Assert.Equal([moved], Sources(LastPlan));
        AssertNoOperationOn(LastPlan, path);
        vm.SelectedSession = vm.Sessions.Single(c => c.Files.Contains(path));
        Assert.Equal("여러 날 폴더라 제자리에 둡니다", vm.Detail?.SkipNote);
    }

    [Fact]
    public async Task MultiDay_OptionOn_Splits_OptionOff_KeepsAgain()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", Oct14);
        var vm = await CreateAsync();

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.True(_plans[^1].Settings.SplitMultiDayFolders);
        Assert.Equal([path], Sources(LastPlan));
        Assert.Equal("여러 날을 묶은 폴더 1개가 날짜별로 나뉩니다. 여러 날 묶기가 들어오기 전에는 재정리에서 빼 주세요", vm.MultiDayNotice);

        vm.SplitMultiDayFolders = false;
        await vm.WhenPlanned;

        Assert.Empty(LastPlan.Operations);
        Assert.Equal("여러 날 폴더 1개는 옮기지 않습니다", vm.MultiDayNotice);
    }

    [Fact]
    public async Task MultiDay_OnlyKeptFiles_BlocksExecute_WithOldMessage()
    {
        Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", Oct14);

        var vm = await CreateAsync();

        Assert.Equal("옮길 파일이 없습니다 — 모두 제자리에 있습니다", vm.BlockingReason);
    }

    [Fact]
    public async Task MultiDay_Facts_CountAsKept()
    {
        Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", Oct14);
        Add($@"{Old}\b.jpg", Oct14);
        var vm = await CreateAsync();

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(1, _runFacts!.KeptInPlace);
        Assert.Equal(0, _runFacts.Excluded);
    }

    [Fact]
    public async Task Detail_ExcludedFile_SaysSo()
    {
        Add($@"{Old}\a.jpg", Oct14);
        var vm = await CreateAsync();

        await ToggleAsync(vm, Assert.Single(vm.Sessions));

        Assert.Equal("이번에 옮기지 않도록 뺐습니다", vm.Detail?.SkipNote);
    }

    [Fact]
    public async Task SplitOption_ReachesSaveFunction_ButTheStoreHasNoPlaceForIt()
    {
        Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        await vm.ExecuteCommand.ExecuteAsync(null);

        // 실행 때 저장 함수는 값을 받지만, 저장 파일에는 이 값의 자리가 없다(AppSettingsStoreTests).
        Assert.True(Assert.Single(_saved).SplitMultiDayFolders);
    }
}
