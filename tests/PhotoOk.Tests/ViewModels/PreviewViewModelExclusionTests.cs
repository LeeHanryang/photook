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

/// <summary>미리보기에서 촬영 빼기, 폴더 날짜로 정리와 사진 날짜로 보내기, 여러 날 폴더 옵션.</summary>
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

    // ----- 폴더 날짜로 정리 -----

    private const string FolderNote = "폴더 날짜로 정리 · 사진 날짜가 다른 파일 1개 (2026-10-14)";
    private const string Sent14From15 = "폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보)";
    private const string Daily15 = @"D:\Archive\[2026-10-15] 일상";

    private static async Task SendAsync(PreviewViewModel vm, SessionItemViewModel card)
    {
        card.SendToPhotoDateCommand.Execute(null);
        await vm.WhenPlanned;
    }

    private static async Task RevertAsync(PreviewViewModel vm, SessionItemViewModel card)
    {
        card.BackToFolderDateCommand.Execute(null);
        await vm.WhenPlanned;
    }

    private static async Task AllToPhotoDateAsync(PreviewViewModel vm)
    {
        vm.AllToPhotoDateCommand.Execute(null);
        await vm.WhenPlanned;
    }

    private static string DestinationOf(Plan plan, string source) =>
        plan.Operations.Single(op => string.Equals(op.Source, source, StringComparison.OrdinalIgnoreCase)).Destination;

    private static List<(string? Source, string Destination)> Shape(Plan plan) =>
        plan.Operations.Select(op => (op.Source, op.Destination)).ToList();

    [Theory]
    [InlineData(@"D:\Archive\[2026-10-15] 일상\a.jpg")]
    [InlineData(@"D:\Archive\2026\2026-10-15\a.jpg")]
    [InlineData(@"D:\Archive\2026-10-15 일상\라이트룸\a.jpg")]
    public async Task Mismatch_ShownAndNotExcluded(string path)
    {
        Add(path, Oct14);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Equal(FolderNote, card.FolderDateNote);
        Assert.Null(card.PhotoDateNote);
        Assert.Null(card.NoCaptureNote);
        Assert.Equal([path], card.FolderDateGroups);
        Assert.Empty(card.PhotoDateGroups);
        Assert.Equal("폴더 날짜로 정리 · 사진 날짜 2026-10-14 (사진 정보)", vm.Detail?.DateMismatch);
        Assert.Equal("날짜 다른 파일 1개", vm.DateDifferText);
        Assert.True(card.IsIncluded);
        Assert.Empty(_plans[0].Excluded);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.DoesNotContain(LastPlan.Skipped, skip => skip.Reason == SkipReason.ExcludedByUser);
    }

    [Fact]
    public async Task FolderDate_NotExcluded_MovesToFolderDate()
    {
        var path = Add($@"{Daily15}\a.jpg", Oct14);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("2026-10-15 · 1장", card.Title);
        Assert.True(card.IsIncluded);
        Assert.Equal(FolderNote, card.FolderDateNote);
        Assert.Equal("폴더 날짜로 정리 · 사진 날짜 2026-10-14 (사진 정보)", vm.Detail?.DateMismatch);
        Assert.Equal("날짜 다른 파일 1개", vm.DateDifferText);
        Assert.Empty(_plans[0].Excluded);
        Assert.Contains("2026-10-15", DestinationOf(LastPlan, path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderDate_CardsMerge_ByOrganizingDate()
    {
        Add($@"{Daily15}\a.jpg", Oct14);
        Add($@"{Daily15}\b.jpg", Oct15);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("2026-10-15 · 2장", card.Title);
        Assert.Equal(FolderNote, card.FolderDateNote);
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
        Assert.Null(card.FolderDateNote);
        Assert.Null(card.PhotoDateNote);
        Assert.Null(card.NoCaptureNote);
        Assert.Empty(card.FolderDateGroups);
        Assert.Null(vm.Detail?.DateMismatch);
        Assert.Null(vm.DateDifferText);
        Assert.True(card.IsIncluded);
    }

    [Fact]
    public async Task RangeFolder_IsNotADateFolder()
    {
        Add(@"D:\Archive\[2026-10-15~10-16] 소풍\a.jpg", Oct14);

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.FolderDateNote);
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
        Assert.Equal($"폴더 날짜로 정리 · 사진 날짜 2026-10-14 ({label})", vm.Detail?.DateMismatch);

        await SendAsync(vm, vm.Sessions[0]);

        var expected = $"폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 ({label})";
        Assert.Equal(expected, vm.Sessions[0].PhotoDateNote);
        Assert.Equal(expected, vm.Detail?.DateMismatch);
    }

    [Fact]
    public async Task NoCaptureTime_MovableToReviewFolder_SaysSo_AndDoesNotCallItAPhotoDate()
    {
        var path = Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings() with { KeepReviewInPlace = false });

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        const string text = "폴더 날짜 2026-10-14 · 촬영 시각 없음 (수정 시각 2026-11-01)";
        Assert.Equal(text, card.NoCaptureNote);
        Assert.Equal(text, vm.Detail?.DateMismatch);
        Assert.True(card.IsIncluded);
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task NoCaptureTime_HasNoDateButtons()
    {
        Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings() with { KeepReviewInPlace = false });

        var card = Assert.Single(vm.Sessions);
        Assert.NotNull(card.NoCaptureNote);
        Assert.Empty(card.FolderDateGroups);
        Assert.Empty(card.PhotoDateGroups);
        Assert.Null(vm.DateDifferText);
    }

    [Fact]
    public async Task NoCaptureTime_KeptInPlaceAsReview_IsNotMismatched_NorExcluded()
    {
        var path = Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync();

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.NoCaptureNote);
        Assert.Null(vm.Detail?.DateMismatch);
        Assert.Null(vm.DateDifferText);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Empty(_plans[0].Excluded);
        Assert.Equal(new PlannedSkip(path, path, SkipReason.ReviewKeptInPlace), Assert.Single(LastPlan.Skipped));
    }

    [Fact]
    public async Task ReviewOptionOff_NotExcluded_ShowsNote_AndUncheckStays()
    {
        var path = Add(@"D:\Archive\2026-10-14\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        var vm = await CreateAsync();

        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.NotNull(vm.Sessions[0].NoCaptureNote);
        Assert.Empty(_plans[^1].Excluded);
        Assert.Contains(path, Sources(LastPlan));

        await ToggleAsync(vm, vm.Sessions[0]);
        AssertNoOperationOn(LastPlan, path);

        vm.KeepReviewInPlace = true;
        await vm.WhenPlanned;
        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        Assert.False(vm.Sessions[0].IsIncluded); // 뺀 묶음은 그대로다
        AssertNoOperationOn(LastPlan, path);
    }

    [Fact]
    public async Task NoCaptureTime_ComparesTheModifiedDate_WithoutDayStart()
    {
        // 수정 시각이 새벽이어도 달력의 날짜로 본다. 폴더와 같은 날이므로 다르다고 하지 않는다.
        Add(@"D:\Archive\2026-11-01\scan.jpg", null, lastWrite: new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc));
        var preset = BuiltInPresets.DateOnly with { DayStart = new TimeOnly(4, 0) };

        var vm = await CreateAsync(Settings(preset: preset) with { KeepReviewInPlace = false });

        Assert.Null(Assert.Single(vm.Sessions).NoCaptureNote);
        Assert.True(vm.Sessions[0].IsIncluded);
    }

    [Fact]
    public async Task NoCaptureTime_SeveralOnOneCard_CountedSeparately()
    {
        Add(@"D:\Archive\2026-10-14\a.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));
        Add(@"D:\Archive\2026-10-15\b.jpg", null, lastWrite: new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings() with { KeepReviewInPlace = false, UseFolderNames = false });

        Assert.Equal("촬영 시각 없는 파일 2개", Assert.Single(vm.Sessions).NoCaptureNote);
    }

    [Fact]
    public async Task ReviewCard_MixedCaptureAndNoCapture_ShowsEach()
    {
        // 기기를 쓰는 프리셋에서 기기를 모르면 촬영 시각이 있어도 판단 필요다. 판단 필요 갈래에 날짜가 있어야 날짜 안내가 뜬다.
        var dated = Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-16\b.jpg", null, lastWrite: new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        var vm = await CreateAsync(Settings(preset: ReviewByDate) with { KeepReviewInPlace = false, UseFolderNames = false });

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        Assert.Equal(FolderNote, card.FolderDateNote);
        Assert.Equal("폴더 날짜 2026-10-16 · 촬영 시각 없음 (수정 시각 2026-11-01)", card.NoCaptureNote);
        Assert.Equal([dated], card.FolderDateGroups); // 촬영 시각이 없는 파일은 버튼을 받지 않는다
    }

    [Fact]
    public async Task MultiDayKept_InnerDateFolderDiffers_IsKept_NotExcluded()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\a.jpg", Oct15);

        var vm = await CreateAsync();

        Assert.Null(Assert.Single(vm.Sessions).FolderDateNote);
        Assert.Empty(vm.Sessions[0].FolderDateGroups);
        Assert.Null(vm.DateDifferText);
        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Equal("그대로 1개", vm.KeptInPlaceText);
        Assert.Equal("여러 날 폴더 1개는 옮기지 않습니다", vm.MultiDayNotice);
        Assert.Equal(new PlannedSkip(path, path, SkipReason.MultiDayFolderKept), Assert.Single(LastPlan.Skipped));
    }

    [Fact]
    public async Task SplitOn_NotExcluded_ShowsNote_AndUncheckStays()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\a.jpg", Oct15);
        var vm = await CreateAsync();

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.Equal("폴더 날짜로 정리 · 사진 날짜가 다른 파일 1개 (2026-10-15)", vm.Sessions[0].FolderDateNote);
        Assert.True(vm.Sessions[0].IsIncluded);
        Assert.Empty(_plans[^1].Excluded);
        Assert.Contains("2026-10-14", DestinationOf(LastPlan, path), StringComparison.Ordinal);

        await ToggleAsync(vm, vm.Sessions[0]);
        AssertNoOperationOn(LastPlan, path);

        vm.SplitMultiDayFolders = false;
        await vm.WhenPlanned;
        Assert.Empty(LastPlan.Operations);
        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.False(vm.Sessions[0].IsIncluded); // 뺀 묶음은 그대로다
        AssertNoOperationOn(LastPlan, path);
    }

    [Fact]
    public async Task Mismatch_DayStartCorrection_IsNamed()
    {
        // 15일 새벽 사진이 하루 시작 시각 때문에 14일 촬영이 되었다.
        Add(@"D:\Archive\2026-10-15\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0));
        var preset = BuiltInPresets.DateOnly with { DayStart = new TimeOnly(4, 0) };
        var vm = await CreateAsync(Settings(preset: preset));
        Assert.Equal("폴더 날짜로 정리 · 사진 날짜 2026-10-14 (사진 정보, 하루 시작 시각 보정)", vm.Detail?.DateMismatch);

        await SendAsync(vm, vm.Sessions[0]);

        Assert.Equal("폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보, 하루 시작 시각 보정)", vm.Sessions[0].PhotoDateNote);
    }

    [Fact]
    public async Task Mismatch_SeveralFolderDates_ShowsFileCount()
    {
        var same = Add(@"D:\Archive\2026-10-14\a.jpg", Oct14);
        var day15 = Add(@"D:\Archive\2026-10-15\b.jpg", Oct14);
        var day16 = Add(@"D:\Archive\2026-10-16\c.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly, UseFolderNames = false });
        Assert.Equal(3, vm.Sessions.Count);
        Assert.Equal("날짜 다른 파일 2개", vm.DateDifferText);

        await AllToPhotoDateAsync(vm);

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("사진 날짜로 보낸 파일 2개", card.PhotoDateNote);
        Assert.Equal([day15, day16], card.PhotoDateGroups.Order(StringComparer.Ordinal));
        Assert.Equal("날짜 다른 파일 2개 · 사진 날짜로 보냄 2개", vm.DateDifferText);
        Assert.True(card.IsIncluded);
        Assert.Contains(day15, Sources(LastPlan));
        Assert.Contains(day16, Sources(LastPlan));
        Assert.True(_fs.FileExists(same));
    }

    [Fact]
    public async Task Mismatch_SentBesideMatchingFiles_ShowsTheDates()
    {
        Add(@"D:\Archive\2026-10-14\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-15\b.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly, UseFolderNames = false });

        await AllToPhotoDateAsync(vm);

        Assert.Equal(Sent14From15, Assert.Single(vm.Sessions).PhotoDateNote);
    }

    [Fact]
    public async Task Mismatch_MixedSources_NamesEach()
    {
        Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-15\b.jpg", Oct14, CaptureTimeSource.FileName);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        await SendAsync(vm, Assert.Single(vm.Sessions));

        Assert.Equal("폴더 날짜 2026-10-15 → 사진 날짜 2026-10-14 (사진 정보, 파일 이름)", Assert.Single(vm.Sessions).PhotoDateNote);
    }

    [Fact]
    public async Task Mismatch_RawSidecarCounted_InFileCount()
    {
        Add(@"D:\Archive\2026-10-14\a.jpg", Oct14);
        Add(@"D:\Archive\2026-10-15\IMG.NEF", Oct14);
        _fs.AddFile(@"D:\Archive\2026-10-15\IMG.xmp", [99]);

        var vm = await CreateAsync(Settings() with { Preset = BuiltInPresets.DateOnly, UseFolderNames = false });

        Assert.Equal("폴더 날짜로 정리 · 사진 날짜가 다른 파일 2개 (2026-10-14)", vm.Sessions.Single(c => c.SessionId == "2026-10-15").FolderDateNote);
        Assert.Equal("날짜 다른 파일 2개", vm.DateDifferText);
        Assert.Equal("제외 0개", vm.ExcludedText);
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task Mismatch_OnlyForReorganize(JobKind kind)
    {
        var path = Add(@"D:\흩어짐\2026-10-15\a.jpg", Oct14);

        var vm = await CreateAsync(Settings(kind, source: @"D:\흩어짐"));

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.FolderDateNote);
        Assert.Empty(card.FolderDateGroups);
        Assert.Null(vm.DateDifferText);
        Assert.True(card.IsIncluded);
        Assert.Contains(path, Sources(LastPlan));
    }

    [Fact]
    public async Task Mismatch_DetailShowsOwnFile_OnMixedCard()
    {
        Add(@"D:\Archive\2026-10-15\b.jpg", Oct14);
        Add(@"D:\Archive\2026-10-16\c.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        await AllToPhotoDateAsync(vm);
        vm.SelectedSession = Assert.Single(vm.Sessions);
        vm.SelectedThumbnail = vm.Thumbnails.Single(t => t.Name == "c.jpg");

        Assert.Equal("폴더 날짜 2026-10-16 → 사진 날짜 2026-10-14 (사진 정보)", vm.Detail?.DateMismatch);
    }

    // ----- 사진 날짜로 보내기 -----

    [Fact]
    public async Task SendToPhotoDate_MovesOnlyDifferingGroups_AndRevertRestores()
    {
        var a = Add($@"{Daily15}\a.jpg", Oct14);
        var b = Add($@"{Daily15}\b.jpg", Oct15);
        var vm = await CreateAsync();
        var first = Shape(LastPlan);

        await SendAsync(vm, Assert.Single(vm.Sessions));

        Assert.Equal(2, vm.Sessions.Count);
        var sent = vm.Sessions.Single(c => c.Files.Contains(a));
        var stayed = vm.Sessions.Single(c => c.Files.Contains(b));
        Assert.Equal(Sent14From15, sent.PhotoDateNote);
        Assert.Null(sent.FolderDateNote);
        Assert.Equal([a], sent.PhotoDateGroups);
        Assert.Empty(sent.FolderDateGroups);
        Assert.Null(stayed.PhotoDateNote);
        Assert.Null(stayed.FolderDateNote);
        Assert.Contains("2026-10-14", DestinationOf(LastPlan, a), StringComparison.Ordinal);
        Assert.Contains("2026-10-15", DestinationOf(LastPlan, b), StringComparison.Ordinal);

        await RevertAsync(vm, sent);

        var card = Assert.Single(vm.Sessions);
        Assert.Equal(FolderNote, card.FolderDateNote);
        Assert.Equal(first, Shape(LastPlan));
        Assert.Null(_plans[^1].Settings.PhotoDateGroups); // 비면 null로 둔다
    }

    [Fact]
    public async Task MixedCard_ShowsBothNotes()
    {
        var kept = Add($@"{Daily15}\a.jpg", Oct14);
        var sent = Add(@"D:\Archive\[2026-10-16] 일상\b.jpg", Oct15);
        var vm = await CreateAsync();

        await SendAsync(vm, vm.Sessions.Single(c => c.Files.Contains(sent)));

        var card = Assert.Single(vm.Sessions);
        Assert.Equal(FolderNote, card.FolderDateNote);
        Assert.Equal("폴더 날짜 2026-10-16 → 사진 날짜 2026-10-15 (사진 정보)", card.PhotoDateNote);
        Assert.Equal([kept], card.FolderDateGroups);
        Assert.Equal([sent], card.PhotoDateGroups);
    }

    [Fact]
    public async Task PhotoDateNote_SeveralFolderDates_ShowsCount()
    {
        Add($@"{Daily15}\a.jpg", Oct14);
        Add(@"D:\Archive\[2026-10-16] 일상\b.jpg", Oct14);
        var vm = await CreateAsync();

        await AllToPhotoDateAsync(vm);

        Assert.Equal("사진 날짜로 보낸 파일 2개", Assert.Single(vm.Sessions).PhotoDateNote);
    }

    [Fact]
    public async Task AllToPhotoDate_SplitsAFolderHoldingManyDates()
    {
        Add($@"{Daily15}\a.NEF", new DateTime(2026, 10, 12, 10, 0, 0));
        _fs.AddFile($@"{Daily15}\a.xmp", [99]);
        Add($@"{Daily15}\b.jpg", new DateTime(2026, 10, 13, 10, 0, 0));
        Add($@"{Daily15}\c.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.Single(vm.Sessions);
        Assert.Equal("날짜 다른 파일 4개", vm.DateDifferText);

        await AllToPhotoDateAsync(vm);

        Assert.Equal(3, vm.Sessions.Count);
        Assert.Equal("날짜 다른 파일 4개 · 사진 날짜로 보냄 4개", vm.DateDifferText);

        vm.AllToFolderDateCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Single(vm.Sessions);
        Assert.Equal("날짜 다른 파일 4개", vm.DateDifferText);
        Assert.Null(_plans[^1].Settings.PhotoDateGroups);
    }

    [Fact]
    public async Task FolderNote_ShowsThePhotoDateRange()
    {
        Add($@"{Daily15}\a.jpg", new DateTime(2026, 10, 12, 10, 0, 0));
        Add($@"{Daily15}\b.jpg", new DateTime(2026, 10, 13, 10, 0, 0));
        Add($@"{Daily15}\c.jpg", Oct14);

        var vm = await CreateAsync();

        Assert.Equal("폴더 날짜로 정리 · 사진 날짜가 다른 파일 3개 (2026-10-12 ~ 2026-10-14)", Assert.Single(vm.Sessions).FolderDateNote);
    }

    [Fact]
    public async Task DateDifferText_CountsOnlyTheSentFiles()
    {
        Add($@"{Daily15}\a.jpg", Oct14);
        Add(@"D:\Archive\[2026-10-16] 일상\b.jpg", Oct14);
        var vm = await CreateAsync();

        await SendAsync(vm, vm.Sessions.Single(c => c.SessionId.StartsWith("2026-10-15", StringComparison.Ordinal)));

        Assert.Equal("날짜 다른 파일 2개 · 사진 날짜로 보냄 1개", vm.DateDifferText);
    }

    [Fact]
    public async Task AllButtons_AreDisabled_WhenTheyWouldDoNothing()
    {
        Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync();
        var changes = new List<string>();
        vm.AllToFolderDateCommand.CanExecuteChanged += (_, _) => changes.Add("폴더");
        vm.AllToPhotoDateCommand.CanExecuteChanged += (_, _) => changes.Add("사진");

        Assert.False(vm.AllToFolderDateCommand.CanExecute(null));
        Assert.True(vm.AllToPhotoDateCommand.CanExecute(null));

        await AllToPhotoDateAsync(vm);

        Assert.True(vm.AllToFolderDateCommand.CanExecute(null));
        Assert.False(vm.AllToPhotoDateCommand.CanExecute(null));
        Assert.Contains("폴더", changes);
        Assert.Contains("사진", changes);

        vm.AllToFolderDateCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.False(vm.AllToFolderDateCommand.CanExecute(null));
        Assert.True(vm.AllToPhotoDateCommand.CanExecute(null));
    }

    [Fact]
    public async Task AllButtons_NoChange_DoNotReplan()
    {
        Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync();
        var plans = _plans.Count;

        vm.AllToFolderDateCommand.Execute(null); // 이미 모두 폴더 날짜다
        await vm.WhenPlanned;
        Assert.Equal(plans, _plans.Count);

        await AllToPhotoDateAsync(vm);
        Assert.Equal(plans + 1, _plans.Count);

        await AllToPhotoDateAsync(vm); // 이미 모두 사진 날짜다
        await SendAsync(vm, vm.Sessions[0]); // 보낼 묶음이 없다
        Assert.Equal(plans + 1, _plans.Count);
    }

    [Fact]
    public async Task AllButtons_NothingDiffers_DoNotReplan()
    {
        Add(@"D:\Archive\[2026-10-14] 일상\a.jpg", Oct14);
        var vm = await CreateAsync();
        var plans = _plans.Count;

        await AllToPhotoDateAsync(vm);
        await SendAsync(vm, vm.Sessions[0]);
        await RevertAsync(vm, vm.Sessions[0]);

        Assert.Equal(plans, _plans.Count);
    }

    [Fact]
    public async Task DateChoice_SurvivesOptionChange()
    {
        var a = Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync();
        await SendAsync(vm, vm.Sessions[0]);

        vm.SplitUnderscores = true;
        await vm.WhenPlanned;

        Assert.Contains("2026-10-14", DestinationOf(LastPlan, a), StringComparison.Ordinal);
        Assert.Equal(Sent14From15, vm.Sessions[0].PhotoDateNote);
    }

    [Fact]
    public async Task DateChoice_KeepsExclusion()
    {
        var a = Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync();
        await ToggleAsync(vm, vm.Sessions[0]);

        await SendAsync(vm, vm.Sessions[0]);
        AssertNoOperationOn(LastPlan, a);
        Assert.False(vm.Sessions[0].IsIncluded);

        await RevertAsync(vm, vm.Sessions[0]);

        AssertNoOperationOn(LastPlan, a);
        Assert.False(Assert.Single(vm.Sessions).IsIncluded);
    }

    [Fact]
    public async Task DateChoice_WhilePlanning_LastPlanHasAll_ExecuteBlocked()
    {
        var a = Add($@"{Daily15}\a.jpg", Oct14);
        var b = Add(@"D:\Archive\[2026-10-16] 소풍\b.jpg", Oct14);
        var vm = await CreateAsync();
        var first = vm.Sessions.Single(c => c.Files.Contains(a));
        var second = vm.Sessions.Single(c => c.Files.Contains(b));

        _gate.Reset();
        first.SendToPhotoDateCommand.Execute(null);
        second.SendToPhotoDateCommand.Execute(null);

        Assert.False(vm.ExecuteCommand.CanExecute(null));
        _gate.Set();
        await vm.WhenPlanned;

        Assert.True(vm.ExecuteCommand.CanExecute(null));
        Assert.Contains("2026-10-14", DestinationOf(LastPlan, a), StringComparison.Ordinal);
        Assert.Contains("2026-10-14", DestinationOf(LastPlan, b), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscardedCard_DateButton_IsIgnored()
    {
        var a = Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync();
        var old = Assert.Single(vm.Sessions);
        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        Assert.NotSame(old, vm.Sessions[0]);
        var plans = _plans.Count;

        await SendAsync(vm, old);

        Assert.Equal(plans, _plans.Count);
        Assert.Contains("2026-10-15", DestinationOf(LastPlan, a), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameCards_StaySameObjects_WithDateGroups()
    {
        var sent = Add(@"D:\Archive\[2026-10-16] 일상\b.jpg", Oct15);
        Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync();
        await SendAsync(vm, vm.Sessions.Single(c => c.Files.Contains(sent)));
        var card = Assert.Single(vm.Sessions);
        Assert.NotEmpty(card.FolderDateGroups);
        Assert.NotEmpty(card.PhotoDateGroups);

        vm.KeepReviewInPlace = false; // 판단 필요 파일이 없으니 카드에 영향이 없다
        await vm.WhenPlanned;

        Assert.Same(card, Assert.Single(vm.Sessions));
    }

    [Fact]
    public async Task EventNameEdit_OnMergedCard_IsDropped()
    {
        var a = Add($@"{Daily15}\a.jpg", Oct14);
        var b = Add($@"{Daily15}\b.jpg", Oct15);
        var other = Add(@"D:\Archive\[2026-10-16] 소풍\c.jpg", new DateTime(2026, 10, 16, 10, 0, 0));
        var vm = await CreateAsync();
        var mixed = vm.Sessions.Single(c => c.Files.Contains(a));
        mixed.EventName = "고친 이름";
        await vm.WhenPlanned;
        vm.Sessions.Single(c => c.Files.Contains(other)).EventName = "남을 이름";
        await vm.WhenPlanned;

        await SendAsync(vm, mixed);

        // 파일 묶음이 달라진 카드의 이름은 사라지고 읽어 온 이름으로 돌아간다. 같은 파일 묶음으로 남은 카드의 이름은 유지한다.
        Assert.Equal("일상", vm.Sessions.Single(c => c.Files.Contains(a)).EventName);
        Assert.Equal("일상", vm.Sessions.Single(c => c.Files.Contains(b)).EventName);
        Assert.Equal("남을 이름", vm.Sessions.Single(c => c.Files.Contains(other)).EventName);
    }

    [Fact]
    public async Task DayFolderLostNotice_OnlyWhenLayoutDropsTheDay()
    {
        const string text = "이 정리 방식은 하루 폴더를 남기지 않아, 다시 재정리하면 사진 날짜로 옮겨질 수 있습니다";
        const string differs = @"D:\Archive\다름";
        const string matches = @"D:\Archive\같음";
        Add($@"{differs}\[2026-10-15]\a.jpg", Oct14);
        Add($@"{matches}\[2026-10-14]\b.jpg", Oct14);

        var dateEvent = await CreateAsync(Settings(preset: BuiltInPresets.DateEvent, source: differs));
        Assert.Null(dateEvent.DayFolderLostNotice);

        var noDifference = await CreateAsync(Settings(preset: BuiltInPresets.Monthly, source: matches));
        Assert.Null(noDifference.DayFolderLostNotice);

        var monthly = await CreateAsync(Settings(preset: BuiltInPresets.Monthly, source: differs));
        Assert.Equal(text, monthly.DayFolderLostNotice);

        await SendAsync(monthly, monthly.Sessions[0]);
        Assert.Null(monthly.DayFolderLostNotice);
    }

    [Fact]
    public async Task DayFolderLostNotice_FollowsThePreset_EvenWhenCardsStayTheSame()
    {
        Add(@"D:\Archive\[2026-10-15]\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateEvent));
        var card = Assert.Single(vm.Sessions);
        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.SelectedPreset = vm.Presets.Single(p => p.Preset == BuiltInPresets.Monthly);
        await vm.WhenPlanned;

        Assert.Same(card, Assert.Single(vm.Sessions));
        Assert.NotNull(vm.DayFolderLostNotice);
        Assert.Contains(nameof(vm.DayFolderLostNotice), notified);
        Assert.Contains(nameof(vm.DateDifferText), notified);
    }

    [Fact]
    public async Task DayFolderLostNotice_KeptNamedFolder_NoNotice_AndRerunHasNoOperations()
    {
        // 하위 폴더 유지에서 이름이 있는 하루 폴더는 유지할 폴더로 남으므로 폴더 날짜를 다음 재정리에도 읽을 수 있다.
        var path = Add($@"{Daily15}\a.jpg", Oct14);

        var first = await CreateAsync(Settings(preset: BuiltInPresets.Monthly));

        Assert.Equal(@"D:\Archive\2026\2026-10\[2026-10-15] 일상\a.jpg", DestinationOf(LastPlan, path));
        Assert.NotNull(first.DateDifferText);
        Assert.Null(first.DayFolderLostNotice);
        foreach (var op in LastPlan.Operations)
        {
            if (op.Source is null)
            {
                _fs.CreateDirectory(op.Destination);
            }
            else
            {
                _fs.Move(op.Source, op.Destination);
                _reader.Set(op.Destination, Oct14);
            }
        }

        var second = await CreateAsync(Settings(preset: BuiltInPresets.Monthly));

        Assert.Empty(LastPlan.Operations);
        Assert.Null(second.DayFolderLostNotice);
    }

    [Fact]
    public async Task DayFolderLostNotice_Merge_DropsTheNamedFolder_ShowsNotice()
    {
        var path = Add($@"{Daily15}\a.jpg", Oct14);

        var vm = await CreateAsync(Settings(preset: BuiltInPresets.Monthly) with { Subfolders = SubfolderPolicy.Merge });

        Assert.Equal(@"D:\Archive\2026\2026-10\a.jpg", DestinationOf(LastPlan, path));
        Assert.NotNull(vm.DayFolderLostNotice);
    }

    [Fact]
    public async Task DayFolderLostNotice_ExcludedGroup_DoesNotCount()
    {
        Add(@"D:\Archive\[2026-10-15]\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.Monthly));
        Assert.NotNull(vm.DayFolderLostNotice);

        await ToggleAsync(vm, vm.Sessions[0]);
        Assert.Null(vm.DayFolderLostNotice);

        await ToggleAsync(vm, vm.Sessions[0]);
        Assert.NotNull(vm.DayFolderLostNotice);
    }

    /// <summary>판단 필요 갈래에도 날짜 폴더가 있는 정리 방식(내장 프리셋의 판단 필요 갈래에는 날짜가 없다).</summary>
    private static readonly Preset ReviewByDate = new("판단 필요도 날짜별", TimeOnly.MinValue,
        [
            new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new("판단 필요"), new("{날짜}"), new("{사유}")]),
            new LayoutBranch("기본", BranchCondition.Always, [new("{연도}"), new("{날짜}"), new("{기기}")]),
        ], [], BuiltInPresets.DateEvent.FormatGroups);

    /// <summary>어느 갈래에도 날짜 토큰이 없는 정리 방식.</summary>
    private static Preset NoDate(params string[] levels) => new("날짜 없음", TimeOnly.MinValue,
        [new LayoutBranch("기본", BranchCondition.Always, levels.Select(level => new LevelTemplate(level)).ToList())], [],
        BuiltInPresets.DateEvent.FormatGroups);

    private const string PhotoDateRevertText = "유지한 폴더 이름의 날짜 때문에, 다시 재정리하면 폴더 날짜로 돌아갈 수 있습니다";
    private const string KeptDay15 = @"D:\Archive\2026\[2026-10-15] 일상";

    [Fact]
    public async Task PhotoDateRevertNotice_KeptFolderCarriesTheOldDate_ShowsNotice_AndRevertClearsIt()
    {
        var path = Add($@"{KeptDay15}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateOnly));
        Assert.Null(vm.PhotoDateRevertNotice);

        await SendAsync(vm, Assert.Single(vm.Sessions));

        Assert.Equal(@"D:\Archive\2026\2026-10-14\[2026-10-15] 일상\a.jpg", DestinationOf(LastPlan, path));
        Assert.Equal(PhotoDateRevertText, vm.PhotoDateRevertNotice);
        Assert.Null(vm.DayFolderLostNotice);

        await RevertAsync(vm, Assert.Single(vm.Sessions));

        Assert.Null(vm.PhotoDateRevertNotice);
    }

    [Fact]
    public async Task PhotoDateRevertNotice_Merge_LeavesNoOldFolder_NoNotice()
    {
        var path = Add($@"{KeptDay15}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateOnly) with { Subfolders = SubfolderPolicy.Merge });

        await SendAsync(vm, Assert.Single(vm.Sessions));

        Assert.Equal(@"D:\Archive\2026\2026-10-14\a.jpg", DestinationOf(LastPlan, path));
        Assert.Null(vm.PhotoDateRevertNotice);
    }

    [Fact]
    public async Task PhotoDateRevertNotice_ExcludedGroup_DoesNotCount()
    {
        Add($@"{KeptDay15}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateOnly));
        await SendAsync(vm, Assert.Single(vm.Sessions));
        Assert.NotNull(vm.PhotoDateRevertNotice);

        await ToggleAsync(vm, Assert.Single(vm.Sessions));
        Assert.Null(vm.PhotoDateRevertNotice);

        await ToggleAsync(vm, Assert.Single(vm.Sessions));
        Assert.NotNull(vm.PhotoDateRevertNotice);
    }

    [Fact]
    public async Task PhotoDateRevertNotice_ChosenGroupThatStays_HasNoOperation_NoNotice()
    {
        // 이름이 있는 하루 폴더가 유지할 폴더로 붙어, 사진 날짜로 보내도 그 폴더 이름의 날짜(14일)가 남는다.
        Add(@"D:\Archive\[2026-10-14~10-15] 소풍\[2026-10-14] 일상\[2026-10-14] 보정\a.jpg", Oct15);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateEvent));
        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;
        await SendAsync(vm, vm.Sessions[0]);
        Assert.NotNull(vm.PhotoDateRevertNotice); // 옮겨지는 동안에는 알린다

        vm.SplitMultiDayFolders = false;
        await vm.WhenPlanned;

        Assert.Null(vm.PhotoDateRevertNotice);
    }

    /// <summary>판단 필요 갈래에 날짜와 원래 폴더가 있는 정리 방식. 원래 폴더 이름의 날짜가 대상 경로에 남는다.</summary>
    private static readonly Preset ReviewByDateKeepsName = new("판단 필요도 날짜별, 원래 폴더 유지", TimeOnly.MinValue,
        [
            new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new("판단 필요"), new("{날짜}"), new("{사유}"), new("{원래폴더}")]),
            new LayoutBranch("기본", BranchCondition.Always, [new("{연도}"), new("{날짜}"), new("{기기}")]),
        ], [], BuiltInPresets.DateEvent.FormatGroups);

    [Fact]
    public async Task PhotoDateRevertNotice_ReviewGroup_WithDateInItsBranch_ShowsNotice()
    {
        var path = Add(@"D:\Archive\[2026-10-15] 일상\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: ReviewByDateKeepsName) with { KeepReviewInPlace = false, UseFolderNames = false });
        Assert.True(Assert.Single(vm.Sessions).IsReviewGroup);
        Assert.Null(vm.PhotoDateRevertNotice);

        await SendAsync(vm, Assert.Single(vm.Sessions));

        Assert.Contains("[2026-10-15] 일상", DestinationOf(LastPlan, path), StringComparison.Ordinal);
        Assert.Equal(PhotoDateRevertText, vm.PhotoDateRevertNotice);

        await RevertAsync(vm, Assert.Single(vm.Sessions));

        Assert.Null(vm.PhotoDateRevertNotice);
    }

    [Fact]
    public async Task PhotoDateRevertNotice_ReviewGroup_BuiltInPreset_NoNotice_EvenWhenChosen()
    {
        // 내장 프리셋의 판단 필요 갈래에는 날짜가 없다. 원래 폴더 이름의 날짜가 남아도 날짜 선택이 대상을 바꾸지 않으므로 알리지 않는다.
        var path = Add(@"D:\Archive\[2026-10-15] 일상\a.jpg", Oct14);
        var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateDeviceFormat)
            with { KeepReviewInPlace = false, UseFolderNames = false, PhotoDateGroups = chosen });

        Assert.True(Assert.Single(vm.Sessions).IsReviewGroup);
        Assert.Contains("[2026-10-15] 일상", DestinationOf(LastPlan, path), StringComparison.Ordinal);
        Assert.Null(vm.PhotoDateRevertNotice);
    }

    [Fact]
    public async Task PhotoDateChoice_SurvivesAPresetWithoutDate_NoteAndRevertStay_ButNotCounted()
    {
        var path = Add($@"{Daily15}\a.jpg", Oct14);
        var noDate = new PresetEntry("날짜 없음", NoDate("{포맷}"), "", null);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateOnly), [.. PresetLibrary.BuiltIn, noDate]);
        await SendAsync(vm, Assert.Single(vm.Sessions));
        Assert.Equal("날짜 다른 파일 1개 · 사진 날짜로 보냄 1개", vm.DateDifferText);

        vm.SelectedPreset = noDate;
        await vm.WhenPlanned;

        var card = Assert.Single(vm.Sessions);
        Assert.Equal(Sent14From15, card.PhotoDateNote);
        Assert.Equal([path], card.PhotoDateGroups);
        Assert.Empty(card.FolderDateGroups);
        Assert.Contains(path, _plans[^1].Settings.PhotoDateGroups!);
        Assert.Null(vm.DateDifferText); // 옮길 날짜가 없으니 날짜 다른 파일로 세지 않는다
        Assert.Null(vm.PhotoDateRevertNotice);

        await RevertAsync(vm, card);

        card = Assert.Single(vm.Sessions);
        Assert.Null(card.PhotoDateNote);
        Assert.Empty(card.PhotoDateGroups);
        Assert.Null(card.FolderDateNote); // 고르지 않은 묶음은 날짜가 없는 갈래에서 아무것도 받지 않는다
        Assert.Null(_plans[^1].Settings.PhotoDateGroups);
    }

    [Fact]
    public async Task ReviewCard_SendAndRevert_FollowTheChoice()
    {
        // 기기를 쓰는 프리셋에서 기기를 모르면 촬영 시각이 있어도 판단 필요다. 판단 필요 갈래에 날짜가 있어야 날짜 선택이 대상을 바꾼다.
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: ReviewByDate) with { KeepReviewInPlace = false, UseFolderNames = false });
        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        Assert.Equal(FolderNote, card.FolderDateNote);

        await SendAsync(vm, card);

        card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        Assert.Equal(Sent14From15, card.PhotoDateNote);
        Assert.Null(card.FolderDateNote);
        Assert.Equal([path], card.PhotoDateGroups);
        Assert.Empty(card.FolderDateGroups);
        Assert.Equal(Sent14From15, vm.Detail?.DateMismatch);
        Assert.Contains(path, _plans[^1].Settings.PhotoDateGroups!);

        await RevertAsync(vm, card);

        card = Assert.Single(vm.Sessions);
        Assert.Equal(FolderNote, card.FolderDateNote);
        Assert.Null(card.PhotoDateNote);
        Assert.Equal([path], card.FolderDateGroups);
        Assert.Empty(card.PhotoDateGroups);
        Assert.Null(_plans[^1].Settings.PhotoDateGroups);
    }

    [Fact]
    public async Task ReviewCard_BuiltInPreset_HasNoDateButtonsOrNotes_BecauseTheReviewFolderHasNoDate()
    {
        // 내장 프리셋의 판단 필요 갈래(판단 필요\사유\원래폴더)에는 날짜가 없어, 어느 날짜로 보내도 대상이 같다.
        Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: BuiltInPresets.DateDeviceFormat) with { KeepReviewInPlace = false, UseFolderNames = false });

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        Assert.Null(card.FolderDateNote);
        Assert.Null(card.PhotoDateNote);
        Assert.Empty(card.FolderDateGroups);
        Assert.Empty(card.PhotoDateGroups);
        Assert.Null(vm.DateDifferText);
        Assert.Null(vm.Detail?.DateMismatch);
        Assert.Null(vm.DayFolderLostNotice);
    }

    [Theory]
    [InlineData("{포맷}")] // 기기를 쓰지 않아 판단 필요가 없는 카드
    [InlineData("{기기}", "{포맷}")] // 기기를 몰라 판단 필요 카드가 되는 경우
    public async Task PresetWithoutDateTokens_HasNoDateButtonsOrNotes(params string[] levels)
    {
        Add($@"{Daily15}\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: NoDate(levels)) with { KeepReviewInPlace = false });

        var card = Assert.Single(vm.Sessions);
        Assert.Null(card.FolderDateNote);
        Assert.Null(card.PhotoDateNote);
        Assert.Null(card.NoCaptureNote);
        Assert.Empty(card.FolderDateGroups);
        Assert.Null(vm.DateDifferText);
        Assert.Null(vm.Detail?.DateMismatch);
    }

    [Fact]
    public async Task PresetWithoutDateTokens_NoCaptureTimeNote_StaysAsIs()
    {
        Add($@"{Daily15}\a.jpg", capture: null, lastWrite: new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc));
        var vm = await CreateAsync(Settings(preset: NoDate("{포맷}")) with { KeepReviewInPlace = false });

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("폴더 날짜 2026-10-15 · 촬영 시각 없음 (수정 시각 2026-10-20)", card.NoCaptureNote);
        Assert.Empty(card.FolderDateGroups);
        Assert.Null(vm.DateDifferText);
    }

    [Fact]
    public async Task ChosenGroup_ThatStays_KeepsNoteAndRevert()
    {
        var path = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\a.jpg", Oct15);
        var vm = await CreateAsync();
        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;
        await SendAsync(vm, vm.Sessions[0]);

        vm.SplitMultiDayFolders = false;
        await vm.WhenPlanned;

        // 제자리로 남아도 고른 값은 살아 있어 카드가 사진 날짜로 묶인다. 고른 것이 보이고 되돌릴 수 있어야 한다.
        const string note = "폴더 날짜 2026-10-14 → 사진 날짜 2026-10-15 (사진 정보)";
        var card = Assert.Single(vm.Sessions);
        Assert.Equal("2026-10-15 · 1장", card.Title);
        Assert.Equal(note, card.PhotoDateNote);
        Assert.Equal([path], card.PhotoDateGroups);
        Assert.Equal(note, vm.Detail?.DateMismatch);
        Assert.Null(vm.DateDifferText); // 옮길 수 있는 묶음만 센다
        AssertNoOperationOn(LastPlan, path);

        await RevertAsync(vm, card);

        card = Assert.Single(vm.Sessions);
        Assert.Equal("2026-10-14 · 1장", card.Title);
        Assert.Null(card.PhotoDateNote);
        Assert.Empty(card.PhotoDateGroups);
        Assert.Empty(card.FolderDateGroups);
        Assert.Null(_plans[^1].Settings.PhotoDateGroups);
    }

    [Fact]
    public async Task ChosenReviewGroup_KeptInPlace_KeepsNoteAndRevert()
    {
        var path = Add(@"D:\Archive\2026-10-15\a.jpg", Oct14);
        var vm = await CreateAsync(Settings(preset: ReviewByDate) with { KeepReviewInPlace = false, UseFolderNames = false });
        await SendAsync(vm, vm.Sessions[0]);

        vm.KeepReviewInPlace = true;
        await vm.WhenPlanned;

        var card = Assert.Single(vm.Sessions);
        Assert.Equal(Sent14From15, card.PhotoDateNote);
        Assert.Equal([path], card.PhotoDateGroups);
        Assert.Null(vm.DateDifferText);

        await RevertAsync(vm, card);

        Assert.Null(Assert.Single(vm.Sessions).PhotoDateNote);
        Assert.Empty(vm.Sessions[0].FolderDateGroups); // 제자리이고 고르지 않은 묶음은 맞춰 보지 않는다
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

    private const string TripFolder = @"D:\Archive\[2026-10-14~10-15] 소풍";

    [Fact]
    public async Task MultiDay_AllKept_HidesCheckBox_AndSaysWhy_UntilSplitIsOn()
    {
        var path = Add($@"{TripFolder}\a.jpg", Oct14);

        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        var card = Assert.Single(vm.Sessions);
        Assert.False(card.ShowIncludeCheckBox);
        Assert.Equal("여러 날 폴더라 옮기지 않습니다", card.Note);
        Assert.Null(card.ExcludedNote);

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        card = Assert.Single(vm.Sessions);
        Assert.True(card.ShowIncludeCheckBox);
        Assert.Null(card.Note);
        Assert.True(card.IsIncluded);
        Assert.Equal([path], Sources(LastPlan));
    }

    [Fact]
    public async Task MultiDay_MixedCard_ShowsCheckBox_AndCountsKeptFiles()
    {
        Add($@"{TripFolder}\IMG.NEF", Oct14);
        _fs.AddFile($@"{TripFolder}\IMG.xmp", [99]);
        Add($@"{Old}\b.jpg", Oct14);

        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.ShowIncludeCheckBox);
        Assert.True(card.IsIncluded);
        Assert.Equal("여러 날 폴더라 옮기지 않는 파일 2개", card.Note);

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.Null(Assert.Single(vm.Sessions).Note);
    }

    [Fact]
    public async Task MultiDay_MixedCard_CheckBoxActsOnMovableFiles_AndCountsAgree()
    {
        var kept = Add($@"{TripFolder}\a.jpg", Oct14);
        var movable = Add($@"{Old}\b.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        var card = Assert.Single(vm.Sessions);

        await ToggleAsync(vm, card);

        // 옮길 수 있는 파일을 모두 뺐으므로 일부가 아니라 꺼짐이다. 여러 날 폴더의 파일은 뺀 수가 아니라 그대로 수에 든다.
        Assert.False(card.IsIncluded);
        Assert.Null(card.ExcludedNote);
        Assert.Equal("제외 1개", vm.ExcludedText);
        Assert.Equal("그대로 1개", vm.KeptInPlaceText);
        Assert.Contains(new PlannedSkip(kept, kept, SkipReason.MultiDayFolderKept), LastPlan.Skipped);
        Assert.Contains(new PlannedSkip(movable, movable, SkipReason.ExcludedByUser), LastPlan.Skipped);

        await ToggleAsync(vm, card);

        Assert.True(card.IsIncluded);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Equal([movable], Sources(LastPlan));
    }

    [Fact]
    public async Task MultiDay_ExcludeAll_CountsOnlyWhatCouldMove_ButStaysOutWhenSplitIsOn()
    {
        var kept = Add($@"{TripFolder}\a.jpg", Oct14);
        var movable = Add($@"{Old}\b.jpg", Oct15);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal("제외 1개", vm.ExcludedText);
        Assert.Equal("그대로 1개", vm.KeptInPlaceText);
        Assert.Equal("옮길 파일이 없습니다", vm.BlockingReason);

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        // 뺀 파일은 옵션을 켜도 다시 포함되지 않는다.
        Assert.Empty(LastPlan.Operations);
        Assert.Equal("제외 2개", vm.ExcludedText);
        Assert.All(vm.Sessions, card => Assert.False(card.IsIncluded));
        AssertNoOperationOn(LastPlan, kept, movable);

        vm.SplitMultiDayFolders = false;
        await vm.WhenPlanned;

        Assert.Equal("제외 1개", vm.ExcludedText);
        Assert.Equal(SkipReason.MultiDayFolderKept, LastPlan.Skipped.Single(s => s.Source == kept).Reason);
    }

    [Fact]
    public async Task MultiDay_OnlyKeptCards_ExcludeAll_KeepsTheInPlaceMessage()
    {
        Add($@"{TripFolder}\a.jpg", Oct14);
        var vm = await CreateAsync();

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Equal("그대로 1개", vm.KeptInPlaceText);
        Assert.Equal("옮길 파일이 없습니다 — 모두 제자리에 있습니다", vm.BlockingReason);
        Assert.Null(vm.Sessions[0].ExcludedNote);
    }

    [Fact]
    public async Task MultiDay_MixedCardUnchecked_ThenSplit_KeptFileStaysOutToo()
    {
        var kept = Add($@"{TripFolder}\a.jpg", Oct14);
        var movable = Add($@"{Old}\b.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        await ToggleAsync(vm, Assert.Single(vm.Sessions));
        Assert.Equal("제외 1개", vm.ExcludedText); // 제자리인 동안에는 그대로 수에 든다

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        // 모두 빼기와 같이 카드의 묶음을 모두 기억한다. 옵션을 켜도 다시 포함되지 않는다.
        var card = Assert.Single(vm.Sessions);
        Assert.False(card.IsIncluded);
        Assert.Equal("제외 2개", vm.ExcludedText);
        Assert.Empty(LastPlan.Operations);
        AssertNoOperationOn(LastPlan, kept, movable);

        await ToggleAsync(vm, card);

        Assert.True(card.IsIncluded);
        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Equal(new[] { kept, movable }.Order(StringComparer.Ordinal), Sources(LastPlan));
    }

    [Fact]
    public async Task MultiDay_MixedCardUncheckedAndChecked_WhileKept_ThenSplit_MovesBoth()
    {
        var kept = Add($@"{TripFolder}\a.jpg", Oct14);
        var movable = Add($@"{Old}\b.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        await ToggleAsync(vm, Assert.Single(vm.Sessions));
        await ToggleAsync(vm, Assert.Single(vm.Sessions));

        vm.SplitMultiDayFolders = true;
        await vm.WhenPlanned;

        Assert.True(Assert.Single(vm.Sessions).IsIncluded);
        Assert.Equal(new[] { kept, movable }.Order(StringComparer.Ordinal), Sources(LastPlan));
    }

    [Fact]
    public async Task ReviewKept_ExcludeAll_IsNotCountedAsExcluded_ButStaysOutWhenMovable()
    {
        var review = Add($@"{Old}\x.jpg", null);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });

        vm.ExcludeAllCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal("제외 0개", vm.ExcludedText);
        Assert.Equal(SkipReason.ReviewKeptInPlace, Assert.Single(LastPlan.Skipped).Reason);

        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        Assert.Equal("제외 1개", vm.ExcludedText);
        AssertNoOperationOn(LastPlan, review);
    }

    [Fact]
    public async Task MultiDay_ResultFacts_MatchTheSummary()
    {
        Add($@"{TripFolder}\a.jpg", Oct14);
        Add($@"{Old}\b.jpg", Oct15);
        Add($@"{Old}\c.jpg", Oct14);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        await ToggleAsync(vm, vm.Sessions.Single(card => card.SessionId == "2026-10-14"));

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Equal(1, _runFacts!.Excluded);
        Assert.Equal(1, _runFacts.KeptInPlace);
    }

    [Fact]
    public void Card_IsIncludedSetter_FollowsTheSameRuleAsTheCommand()
    {
        var toggled = 0;
        var card = new SessionItemViewModel("2026-10-14", "2026-10-14 · 1장", "", false, [@"D:\a.jpg"], null, null,
            includeToggled: _ => toggled++);

        card.IsIncluded = false;

        Assert.Equal(1, toggled);
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
