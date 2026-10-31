using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Settings;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;
using PhotoOk.Views;

namespace PhotoOk.Tests.ViewModels;

/// <summary>미리보기 위쪽의 옵션: 바꾸면 다시 스캔하지 않고 촬영 묶기와 계획만 다시 세워 카드와 트리를 바꿔 끼운다.</summary>
public sealed class PreviewViewModelOptionsTests : IDisposable
{
    private const string Archive = @"D:\Archive";
    private const string Old = @"D:\Archive\DCIM";
    private const string LogFolder = @"C:\Users\test\AppData\Local\PhotoOk\logs";
    private const string IgnoredPath = @"C:\Users\test\AppData\Roaming\PhotoOk\ignored-folder-names.txt";
    private const string SettingsPath = @"C:\Users\test\AppData\Roaming\PhotoOk\settings.jsonl";
    private const string PresetFolder = @"C:\Users\test\AppData\Roaming\PhotoOk\presets";
    private const string Trip = "바닷가 나들이";
    private const string Walk = "동네 산책";
    private const string Oct14Text = "2026-10-14";
    private const string Oct15Text = "2026-10-15";
    private const string SplitName = "가족여행_제주 바닷가";
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
    private readonly List<PlanCall> _plans = [];
    private readonly List<Plan> _runs = [];
    private readonly List<ImportSettings> _backs = [];
    private readonly List<ImportSettings> _saved = [];
    private int _reads;
    private int _failPlans;
    private int _failRegroups;

    public PreviewViewModelOptionsTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(Old);
        _fs.SetFreeSpace(@"D:\", 50L * 1024 * 1024 * 1024);
        _reader.OnRead = _ => Interlocked.Increment(ref _reads);
        _workflow = new ImportWorkflow(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc, IgnoredPath);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class ScreenStub : ObservableObject;

    private sealed record PlanCall(ImportSettings Settings, ScanOutcome Scan, IReadOnlyDictionary<string, string> Names, PlanOutcome? Outcome);

    private static ImportSettings Settings(JobKind kind = JobKind.Reorganize) =>
        new([Old], true, Archive, BuiltInPresets.DateEvent, kind == JobKind.Import ? TransferMode.Copy : TransferMode.Move, kind,
            UseFolderNames: kind != JobKind.Import);

    private void Add(string relative, DateTime? capture, string? model = null, byte fill = 1)
    {
        var path = Old + @"\" + relative;
        _fs.AddFile(path, [fill, fill, fill]);
        _reader.Set(path, capture, model is null ? null : "Maker", model);
    }

    private PreviewViewModel Create(ImportSettings settings, ScanOutcome scan, Action<ImportSettings>? saveChoices = null,
        IReadOnlyList<PresetEntry>? presets = null)
    {
        var vm = new PreviewViewModel(_shell, _workflow, settings, scan, _fs, _thumbnails, _dialogs, new FileLog(_fs, LogFolder),
            createRun: (plan, _) =>
            {
                _runs.Add(plan);
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
            saveChoices: saveChoices ?? _saved.Add,
            buildPlan: (s, scanned, names) =>
            {
                _gate.Wait(Wait);
                if (Interlocked.Exchange(ref _failPlans, 0) > 0)
                {
                    throw new InvalidOperationException("시험용 실패");
                }

                var outcome = _workflow.BuildPlan(s, scanned, names);
                lock (_plans)
                {
                    _plans.Add(new PlanCall(s, scanned, new Dictionary<string, string>(names), outcome));
                }

                return outcome;
            },
            regroup: (s, scanned) =>
            {
                if (Interlocked.Exchange(ref _failRegroups, 0) > 0)
                {
                    throw new InvalidOperationException("시험용 실패");
                }

                return _workflow.Regroup(s, scanned);
            });
        _shell.Navigate(vm);
        return vm;
    }

    private async Task<PreviewViewModel> CreateAsync(ImportSettings? settings = null, Action<ImportSettings>? saveChoices = null,
        IReadOnlyList<PresetEntry>? presets = null)
    {
        settings ??= Settings();
        var scan = await _workflow.ScanAsync(settings, null, CancellationToken.None);
        var vm = Create(settings, scan, saveChoices, presets);
        await vm.WhenPlanned;
        return vm;
    }

    private static IEnumerable<FolderNodeViewModel> Flatten(IEnumerable<FolderNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static List<string> NodeNames(PreviewViewModel vm) => Flatten(vm.FolderTree).Select(n => n.Name).ToList();

    private static PresetEntry Preset(PreviewViewModel vm, string name) => vm.Presets.Single(p => p.Name == name);

    // ----- 옵션을 바꾸면 트리가 바뀐다 -----

    [Fact]
    public async Task ChangingOption_RebuildsTreeWithoutRescan()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.Contains($"{Oct14Text} {Trip}", NodeNames(vm));
        Assert.Equal(Trip, vm.Sessions[0].EventName);
        var reads = _reads;
        var plans = _plans.Count;

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.False(vm.UseFolderNames);
        Assert.DoesNotContain($"{Oct14Text} {Trip}", NodeNames(vm));
        Assert.Contains(Oct14Text, NodeNames(vm));
        Assert.Equal("", Assert.Single(vm.Sessions).EventName);
        Assert.Equal(Oct14Text, vm.Sessions[0].SessionId);
        Assert.Equal(reads, _reads);
        Assert.Equal(plans + 1, _plans.Count);
    }

    [Fact]
    public async Task SettingSameValue_DoesNotReplan()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        var plans = _plans.Count;

        vm.UseFolderNames = true;
        vm.SplitUnderscores = false;
        vm.Subfolders = SubfolderPolicy.Keep;
        vm.KeepReviewInPlace = true;
        vm.SelectedPreset = vm.SelectedPreset;
        await vm.WhenPlanned;

        Assert.Equal(plans, _plans.Count);
    }

    [Fact]
    public async Task SplitUnderscores_ChangesTreeAndCardText()
    {
        Add(SplitName + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.Equal(SplitName, vm.Sessions[0].EventName);
        Assert.Contains($"{Oct14Text} {SplitName}", NodeNames(vm));

        vm.SplitUnderscores = true;
        await vm.WhenPlanned;

        var card = Assert.Single(vm.Sessions);
        Assert.Equal(@"가족여행\제주 바닷가", card.EventName);
        Assert.True(card.NameFromFolder);
        var parent = Flatten(vm.FolderTree).Single(n => n.Name == $"{Oct14Text} 가족여행");
        Assert.Equal(["제주 바닷가"], parent.Children.Select(n => n.Name));
        Assert.DoesNotContain($"{Oct14Text} {SplitName}", NodeNames(vm));
    }

    [Fact]
    public async Task Subfolders_Options_ChangeTree()
    {
        Add(Trip + @"\b.jpg", Oct14);
        Add(Trip + @"\라이트룸\a.jpg", Oct14, fill: 2);
        var kept = Old + @"\" + Trip + @"\라이트룸\a.jpg";
        var vm = await CreateAsync();
        Assert.Equal(SubfolderPolicy.Keep, vm.Subfolders);
        Assert.Contains("라이트룸", NodeNames(vm));
        Assert.Equal(2, vm.TransferCount);

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;

        Assert.DoesNotContain("라이트룸", NodeNames(vm));
        Assert.Equal(2, vm.TransferCount);

        vm.Subfolders = SubfolderPolicy.InPlace;
        await vm.WhenPlanned;

        var plan = _plans[^1].Outcome!.Plan;
        Assert.DoesNotContain("라이트룸", NodeNames(vm));
        Assert.Equal(1, vm.TransferCount);
        Assert.Equal(1, vm.KeptInPlaceCount);
        Assert.DoesNotContain(plan.Operations, op => string.Equals(op.Source, kept, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SkipReason.SubfolderKeptInPlace, plan.Skipped.Single(s => s.Source == kept).Reason);
    }

    [Fact]
    public async Task ReviewOption_ChangesTree()
    {
        Add(Trip + @"\a.jpg", Oct14);
        Add(Trip + @"\b.jpg", null, fill: 2);
        var vm = await CreateAsync();
        Assert.True(vm.KeepReviewInPlace);
        Assert.DoesNotContain("판단 필요", NodeNames(vm));
        Assert.Equal("판단 필요 · 1장 (그대로 둠)", vm.Sessions[^1].Title);
        Assert.Equal(1, vm.KeptInPlaceCount);

        vm.KeepReviewInPlace = false;
        await vm.WhenPlanned;

        Assert.Contains("판단 필요", NodeNames(vm));
        Assert.Equal("판단 필요 · 1장", vm.Sessions[^1].Title);
        Assert.Null(vm.Sessions[^1].Note);
        Assert.Equal(0, vm.KeptInPlaceCount);
        Assert.Equal(2, vm.TransferCount);
    }

    [Fact]
    public async Task PresetChange_RebuildsCardsAndTree()
    {
        Add(Trip + @"\a.jpg", Oct14, "CAM-1");
        Add(Trip + @"\b.jpg", Oct14, fill: 2); // 기기를 모른다: 기기를 쓰는 방식에서만 판단 필요다
        var vm = await CreateAsync();
        Assert.Equal("날짜 + 이벤트", vm.SelectedPreset?.Name);
        Assert.Equal(0, vm.ReviewCount);
        Assert.Equal(Trip, Assert.Single(vm.Sessions).EventName);
        var reads = _reads;

        vm.SelectedPreset = Preset(vm, "날짜·기기·포맷");
        await vm.WhenPlanned;

        Assert.Equal(reads, _reads);
        Assert.Equal(1, vm.ReviewCount);
        Assert.Equal(2, vm.Sessions.Count);
        Assert.Equal("", vm.Sessions[0].EventName); // 이벤트를 쓰지 않는 방식이라 읽어 온 이름이 없다
        Assert.Equal(Oct14Text, vm.Sessions[0].SessionId);
        Assert.Equal("판단 필요 · 1장 (그대로 둠)", vm.Sessions[1].Title);
        Assert.Contains("CAM-1", NodeNames(vm));
        Assert.Contains("JPG", NodeNames(vm));
        Assert.Equal(1, vm.TransferCount);
        Assert.Equal(1, vm.KeptInPlaceCount);
        Assert.Equal("기본", vm.Detail?.BranchName);
        Assert.Contains("{기기}", vm.Detail?.Template);

        vm.SelectedPreset = Preset(vm, "날짜 + 이벤트");
        await vm.WhenPlanned;

        Assert.Equal(0, vm.ReviewCount);
        Assert.Equal(Trip, Assert.Single(vm.Sessions).EventName);
        Assert.Equal(2, vm.TransferCount);
    }

    [Fact]
    public async Task PresetChange_DetailFollowsReviewBranchOfNewPreset()
    {
        Add(Trip + @"\b.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.Equal("기본", vm.Detail?.BranchName);

        vm.SelectedPreset = Preset(vm, "날짜·기기·포맷");
        await vm.WhenPlanned;

        var card = Assert.Single(vm.Sessions);
        Assert.True(card.IsReviewGroup);
        Assert.Same(card, vm.SelectedSession);
        Assert.Equal("판단 필요", vm.Detail?.BranchName);
    }

    // ----- 고친 이름 -----

    [Fact]
    public async Task EditedName_SurvivesOptionChange()
    {
        Add(SplitName + @"\a.jpg", Oct14);
        Add(Walk + @"\b.jpg", Oct15, fill: 2);
        var vm = await CreateAsync();
        vm.Sessions.Single(c => c.EventName == SplitName).EventName = "고친 이름";
        await vm.WhenPlanned;

        vm.SplitUnderscores = true; // 이름이 나뉘어 촬영 Id가 바뀌지만 파일은 그대로다
        await vm.WhenPlanned;

        var id = $@"{Oct14Text}|가족여행\제주 바닷가";
        var card = vm.Sessions.Single(c => c.SessionId == id);
        Assert.Equal("고친 이름", card.EventName);
        Assert.False(card.NameFromFolder);
        Assert.Equal(Walk, vm.Sessions.Single(c => c.SessionId == $"{Oct15Text}|{Walk}").EventName);
        Assert.Equal("고친 이름", _plans[^1].Names[id]);
        Assert.Contains($"{Oct14Text} 고친 이름", NodeNames(vm));
    }

    [Fact]
    public async Task LeftAloneName_FollowsSplit()
    {
        Add(SplitName + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.Sessions[0].EventName = "고친 이름";
        await vm.WhenPlanned;
        vm.Sessions[0].EventName = SplitName; // 읽어 온 이름으로 되돌리면 손대지 않은 것과 같다
        await vm.WhenPlanned;

        vm.SplitUnderscores = true;
        await vm.WhenPlanned;

        Assert.Equal(@"가족여행\제주 바닷가", Assert.Single(vm.Sessions).EventName);
        Assert.True(vm.Sessions[0].NameFromFolder);
    }

    [Fact]
    public async Task EditedName_SurvivesFolderNamesOffAndOn()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.Sessions[0].EventName = "고친 이름";
        await vm.WhenPlanned;

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.Equal(Oct14Text, Assert.Single(vm.Sessions).SessionId);
        Assert.Equal("고친 이름", vm.Sessions[0].EventName);
        Assert.Equal("고친 이름", _plans[^1].Names[Oct14Text]);
        Assert.Contains($"{Oct14Text} 고친 이름", NodeNames(vm));

        vm.UseFolderNames = true;
        await vm.WhenPlanned;

        Assert.Equal($"{Oct14Text}|{Trip}", Assert.Single(vm.Sessions).SessionId);
        Assert.Equal("고친 이름", vm.Sessions[0].EventName);
        Assert.False(vm.Sessions[0].NameFromFolder);
        Assert.Equal("고친 이름", _plans[^1].Names[$"{Oct14Text}|{Trip}"]);
    }

    [Fact]
    public async Task ClearedName_StaysClearedThroughFolderNamesOffAndOn()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.Sessions[0].EventName = "";
        await vm.WhenPlanned;

        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        Assert.Equal("", Assert.Single(vm.Sessions).EventName);

        vm.UseFolderNames = true;
        await vm.WhenPlanned;

        Assert.Equal($"{Oct14Text}|{Trip}", Assert.Single(vm.Sessions).SessionId);
        Assert.Equal("", vm.Sessions[0].EventName);
        Assert.False(vm.Sessions[0].NameFromFolder);
        Assert.Equal("", _plans[^1].Names[$"{Oct14Text}|{Trip}"]);
        Assert.DoesNotContain($"{Oct14Text} {Trip}", NodeNames(vm));
    }

    [Fact]
    public async Task MergedCards_DoNotTransferName_AndEditIsDropped()
    {
        Add("a.jpg", Oct14);
        Add(Trip + @"\b.jpg", Oct14, fill: 2);
        var vm = await CreateAsync();
        vm.Sessions.Single(c => c.SessionId == Oct14Text).EventName = "직접 쓴 이름";
        await vm.WhenPlanned;

        vm.UseFolderNames = false; // 두 촬영이 합쳐져 같은 Id로 남지만 파일 묶음은 다르다
        await vm.WhenPlanned;

        var merged = Assert.Single(vm.Sessions);
        Assert.Equal(Oct14Text, merged.SessionId);
        Assert.Equal("", merged.EventName);
        Assert.False(_plans[^1].Names.ContainsKey(Oct14Text));
        Assert.DoesNotContain($"{Oct14Text} 직접 쓴 이름", NodeNames(vm));

        // 합쳐지면서 버린 이름은 다시 나뉘어도 돌아오지 않는다.
        vm.UseFolderNames = true;
        await vm.WhenPlanned;

        Assert.Equal("", vm.Sessions.Single(c => c.SessionId == Oct14Text).EventName);
        Assert.Equal(Trip, vm.Sessions.Single(c => c.SessionId == $"{Oct14Text}|{Trip}").EventName);
    }

    [Fact]
    public async Task DiscardedMerge_KeepsEdit()
    {
        Add("a.jpg", Oct14);
        Add(Trip + @"\b.jpg", Oct14, fill: 2);
        var vm = await CreateAsync();
        vm.Sessions.Single(c => c.SessionId == Oct14Text).EventName = "직접 쓴 이름";
        await vm.WhenPlanned;

        // 합쳐지는 결과가 반영되기 전에 되돌리면 이름을 버리지 않는다.
        _gate.Reset();
        vm.UseFolderNames = false;
        vm.UseFolderNames = true;
        _gate.Set();
        await vm.WhenPlanned;

        Assert.Equal(2, vm.Sessions.Count);
        Assert.Equal("직접 쓴 이름", vm.Sessions.Single(c => c.SessionId == Oct14Text).EventName);

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;
        Assert.Equal("직접 쓴 이름", _plans[^1].Names[Oct14Text]);
    }

    [Fact]
    public void FilesKey_IgnoresOrderAndCase()
    {
        Assert.Equal(SessionItemViewModel.KeyOf([@"D:\a.jpg", @"D:\B.jpg"]), SessionItemViewModel.KeyOf([@"d:\b.JPG", @"D:\A.jpg"]));
        Assert.NotEqual(SessionItemViewModel.KeyOf([@"D:\a.jpg"]), SessionItemViewModel.KeyOf([@"D:\a.jpg", @"D:\b.jpg"]));
    }

    [Fact]
    public async Task SplitCards_DoNotInheritName()
    {
        Add("a.jpg", Oct14);
        Add(Trip + @"\b.jpg", Oct14, fill: 2);
        var vm = await CreateAsync(Settings() with { UseFolderNames = false });
        Assert.Equal(Oct14Text, Assert.Single(vm.Sessions).SessionId);
        vm.Sessions[0].EventName = "직접 쓴 이름";
        await vm.WhenPlanned;

        vm.UseFolderNames = true; // 한 촬영이 둘로 나뉘면 어느 쪽도 그 파일 묶음이 아니다
        await vm.WhenPlanned;

        Assert.Equal("", vm.Sessions.Single(c => c.SessionId == Oct14Text).EventName);
        Assert.Equal(Trip, vm.Sessions.Single(c => c.SessionId == $"{Oct14Text}|{Trip}").EventName);
    }

    [Fact]
    public async Task ClearedSuggestion_StaysEmptyAfterOptionChange()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.Sessions[0].EventName = "";
        await vm.WhenPlanned;

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;

        Assert.Equal("", Assert.Single(vm.Sessions).EventName);
        Assert.Equal("", _plans[^1].Names[$"{Oct14Text}|{Trip}"]);
        Assert.DoesNotContain($"{Oct14Text} {Trip}", NodeNames(vm));
    }

    [Fact]
    public async Task HandTypedName_OnDateCard_SurvivesOptionChange()
    {
        Add("a.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.Equal(Oct14Text, vm.Sessions[0].SessionId);
        vm.Sessions[0].EventName = "직접 쓴 이름";
        await vm.WhenPlanned;

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.Equal("직접 쓴 이름", Assert.Single(vm.Sessions).EventName);
        Assert.Equal("직접 쓴 이름", _plans[^1].Names[Oct14Text]);
    }

    [Fact]
    public async Task NeverUseName_StaysExcludedAfterOptionChange()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();

        vm.Sessions[0].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;
        vm.SplitUnderscores = true;
        await vm.WhenPlanned;

        Assert.Equal(Oct14Text, Assert.Single(vm.Sessions).SessionId);
        Assert.Equal("", vm.Sessions[0].EventName);
        Assert.False(vm.Sessions[0].NameFromFolder);
    }

    // ----- 고른 카드와 썸네일 -----

    [Fact]
    public async Task SelectedCard_FollowsFileSet_WhenIdChanges()
    {
        Add("a.jpg", Oct14);
        Add(Trip + @"\b.jpg", Oct15, fill: 2);
        var vm = await CreateAsync();
        vm.SelectedSession = vm.Sessions.Single(c => c.SessionId == $"{Oct15Text}|{Trip}");

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.Equal(Oct15Text, vm.SelectedSession?.SessionId);
        Assert.Contains(vm.SelectedSession, vm.Sessions);
        Assert.Equal("b.jpg", vm.Detail?.FileName);
    }

    [Fact]
    public async Task SelectedCard_StaysIfIdSurvives_WhenFileSetChanged()
    {
        Add("z.jpg", Oct14);
        Add("a.jpg", Oct15, fill: 2);
        Add(Trip + @"\b.jpg", Oct15, fill: 3);
        var vm = await CreateAsync();
        vm.SelectedSession = vm.Sessions.Single(c => c.SessionId == Oct15Text);

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.Equal(Oct15Text, vm.SelectedSession?.SessionId);
        Assert.Same(vm.Sessions[1], vm.SelectedSession);
    }

    [Fact]
    public async Task SelectedCard_GoesToFirst_WhenFileSetAndIdAreGone()
    {
        Add("z.jpg", Oct14);
        Add("a.jpg", Oct15, fill: 2);
        Add(Trip + @"\b.jpg", Oct15, fill: 3);
        var vm = await CreateAsync();
        vm.SelectedSession = vm.Sessions.Single(c => c.SessionId == $"{Oct15Text}|{Trip}");

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.Same(vm.Sessions[0], vm.SelectedSession);
        Assert.Equal(Oct14Text, vm.SelectedSession?.SessionId);
        Assert.Equal("z.jpg", vm.Detail?.FileName);
    }

    [Fact]
    public async Task RebuildingCards_CancelsCardThumbnailRequests()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        _ = vm.Sessions[0].Thumbnail;
        var token = _thumbnails.Tokens[^1];
        Assert.False(token.IsCancellationRequested);

        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.True(token.IsCancellationRequested);
        _ = vm.Sessions[0].Thumbnail;
        Assert.False(_thumbnails.Tokens[^1].IsCancellationRequested);
    }

    [Fact]
    public async Task SameCardContents_KeepCardObjectsAndThumbnails()
    {
        Add(Trip + @"\a.jpg", Oct14);
        Add(Walk + @"\b.jpg", Oct15, fill: 2);
        var vm = await CreateAsync();
        var cards = vm.Sessions.ToList();
        vm.SelectedSession = cards[1];
        cards[0].EventName = "고친 이름";
        await vm.WhenPlanned;
        _ = cards[0].Thumbnail;
        var token = _thumbnails.Tokens[^1];
        var requests = _thumbnails.Requests.Count;

        vm.Subfolders = SubfolderPolicy.Merge; // 트리는 바뀌어도 카드 내용은 그대로다
        await vm.WhenPlanned;

        Assert.Equal(cards, vm.Sessions);
        Assert.Same(cards[1], vm.SelectedSession);
        Assert.Equal("고친 이름", cards[0].EventName);
        Assert.False(token.IsCancellationRequested);
        Assert.Equal(requests, _thumbnails.Requests.Count); // 고른 카드의 썸네일도 다시 요청하지 않는다
        Assert.Equal(SubfolderPolicy.Merge, _plans[^1].Settings.Subfolders);

        // 그 뒤의 이름 변경도 새 값으로 계획한다.
        cards[0].EventName = "또 고친 이름";
        await vm.WhenPlanned;
        Assert.Equal(SubfolderPolicy.Merge, _plans[^1].Settings.Subfolders);
        Assert.Equal("또 고친 이름", _plans[^1].Names[$"{Oct14Text}|{Trip}"]);
    }

    // ----- 늦은 결과와 실행 -----

    [Fact]
    public async Task QuickOptionChanges_LatestWins()
    {
        Add(SplitName + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        var plans = _plans.Count;
        var treeChanges = 0;
        vm.PropertyChanged += (_, e) => treeChanges += e.PropertyName == nameof(vm.FolderTree) ? 1 : 0;

        _gate.Reset();
        vm.SplitUnderscores = true;
        vm.Subfolders = SubfolderPolicy.Merge;
        vm.SplitUnderscores = false;
        vm.UseFolderNames = false;
        _gate.Set();
        await vm.WhenPlanned;

        // 먼저 시작한 하나는 끝까지 돌지만 결과를 버리고, 그 사이의 요청은 계획을 세우지도 않는다.
        Assert.InRange(_plans.Count - plans, 1, 2);
        var last = _plans[^1].Settings;
        Assert.False(last.SplitUnderscores);
        Assert.False(last.UseFolderNames);
        Assert.Equal(SubfolderPolicy.Merge, last.Subfolders);
        Assert.Equal(1, treeChanges);
        Assert.Equal(Oct14Text, Assert.Single(vm.Sessions).SessionId);
        Assert.Contains(Oct14Text, NodeNames(vm));
        Assert.False(vm.IsReplanning);
    }

    [Fact]
    public async Task ExecuteDisabled_WhileRegrouping()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        Assert.True(vm.ExecuteCommand.CanExecute(null));

        _gate.Reset();
        vm.SplitUnderscores = true;

        Assert.True(vm.IsReplanning);
        Assert.Equal("정리 계획을 세우는 중…", vm.PlanningText);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.ExecuteCommand.Execute(null);
        Assert.Empty(_runs);
        Assert.Empty(_saved);

        _gate.Set();
        await vm.WhenPlanned;

        Assert.False(vm.IsReplanning);
        Assert.True(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditWhileRegrouping_KeepsEditAndNewOption()
    {
        Add(SplitName + @"\a.jpg", Oct14);
        Add(Walk + @"\b.jpg", Oct15, fill: 2);
        var vm = await CreateAsync();

        _gate.Reset();
        vm.SplitUnderscores = true;
        vm.Sessions.Single(c => c.SessionId == $"{Oct15Text}|{Walk}").EventName = "고친 이름"; // 아직 옛 카드다
        _gate.Set();
        await vm.WhenPlanned;

        Assert.Equal([@"가족여행\제주 바닷가", "고친 이름"], vm.Sessions.Select(c => c.EventName));
        Assert.True(_plans[^1].Settings.SplitUnderscores);
        Assert.Equal("고친 이름", _plans[^1].Names[$"{Oct15Text}|{Walk}"]);
        Assert.Contains($"{Oct15Text} 고친 이름", NodeNames(vm));
        Assert.Contains($"{Oct14Text} 가족여행", NodeNames(vm));
    }

    [Fact]
    public async Task ReplacedCard_EditIsIgnored()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        var oldCard = vm.Sessions[0];
        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        Assert.DoesNotContain(oldCard, vm.Sessions);
        var plans = _plans.Count;

        oldCard.EventName = "버려진 카드";
        await vm.WhenPlanned;

        Assert.Equal(plans, _plans.Count);
        Assert.False(vm.IsReplanning);
        Assert.Equal("", vm.Sessions[0].EventName);
    }

    [Fact]
    public async Task EventNameEdit_DoesNotRegroup()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        var card = vm.Sessions[0];
        var scan = _plans[^1].Scan;

        card.EventName = "고친 이름";
        await vm.WhenPlanned;

        Assert.Same(card, vm.Sessions[0]); // 카드를 바꿔 끼우지 않으므로 입력 중인 칸이 그대로다
        Assert.Same(scan, _plans[^1].Scan);
    }

    [Fact]
    public async Task EventNameEdit_AfterOptionChange_UsesRegroupedScan()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        var original = _plans[^1].Scan;
        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        var card = vm.Sessions[0];

        card.EventName = "고친 이름";
        await vm.WhenPlanned;

        Assert.Same(card, vm.Sessions[0]);
        Assert.NotSame(original, _plans[^1].Scan);
        Assert.Equal(Oct14Text, _plans[^1].Scan.Sessions[0].Id);
        Assert.False(_plans[^1].Settings.UseFolderNames);
        Assert.Contains($"{Oct14Text} 고친 이름", NodeNames(vm));
    }

    [Fact]
    public async Task FailedPlan_ThenNextEdit_StillAppliesOption()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();

        Interlocked.Exchange(ref _failPlans, 1);
        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.False(vm.HasPlan);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        Assert.Equal($"{Oct14Text}|{Trip}", vm.Sessions[0].SessionId); // 실패한 결과는 바꿔 끼우지 않는다

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;

        Assert.True(vm.HasPlan);
        Assert.Null(vm.BlockingReason);
        Assert.Equal(Oct14Text, Assert.Single(vm.Sessions).SessionId);
        Assert.True(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task RegroupFailure_FailsSafe_ThenRecovers()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        var card = vm.Sessions[0];
        var plans = _plans.Count;

        Interlocked.Exchange(ref _failRegroups, 1);
        vm.UseFolderNames = false;
        await vm.WhenPlanned;

        Assert.Equal(plans, _plans.Count);
        Assert.False(vm.HasPlan);
        Assert.False(vm.IsReplanning);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        Assert.Empty(vm.FolderTree);
        Assert.DoesNotContain("시험용 실패", vm.BlockingReason);
        Assert.Same(card, vm.Sessions[0]);
        vm.ExecuteCommand.Execute(null);
        Assert.Empty(_runs);
        Assert.Empty(_saved);

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;

        Assert.True(vm.HasPlan);
        Assert.Null(vm.BlockingReason);
        Assert.Equal(Oct14Text, Assert.Single(vm.Sessions).SessionId);
        Assert.True(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reorganize_ModeStaysMove_AfterOptionAndPresetChanges()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();

        vm.UseFolderNames = false;
        vm.SplitUnderscores = true;
        vm.Subfolders = SubfolderPolicy.Merge;
        vm.KeepReviewInPlace = false;
        vm.SelectedPreset = Preset(vm, "날짜만");
        await vm.WhenPlanned;

        Assert.Equal(TransferMode.Move, _plans[^1].Settings.Mode);
        Assert.Equal("이동 1개", vm.TransferText);
        Assert.All(_plans[^1].Outcome!.Plan.Operations.Where(op => op.Source is not null),
            op => Assert.Equal(OperationKind.Move, op.Kind));

        vm.ExecuteCommand.Execute(null);

        Assert.Equal(TransferMode.Move, Assert.Single(_saved).Mode);
    }

    // ----- 보이는 옵션 -----

    [Theory]
    [InlineData(JobKind.Import, false, false)]
    [InlineData(JobKind.Absorb, true, false)]
    [InlineData(JobKind.Reorganize, true, true)]
    public async Task Options_VisibleByJobKind(JobKind kind, bool folderOptions, bool reviewOption)
    {
        Add("a.jpg", Oct14);

        var vm = await CreateAsync(Settings(kind));

        Assert.Equal(folderOptions, vm.ShowFolderOptions);
        Assert.Equal(reviewOption, vm.ShowReviewOption);
    }

    [Fact]
    public async Task Options_HiddenForImport()
    {
        Add("a.jpg", Oct14);

        var vm = await CreateAsync(Settings(JobKind.Import));

        Assert.False(vm.ShowFolderOptions);
        Assert.False(vm.ShowReviewOption);
        Assert.Equal(PresetLibrary.BuiltIn.Select(p => p.Name), vm.Presets.Select(p => p.Name));
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task ReviewOption_OnlyForReorganize(JobKind kind)
    {
        Add("a.jpg", Oct14);

        var vm = await CreateAsync(Settings(kind));

        Assert.False(vm.ShowReviewOption);
    }

    [Fact]
    public async Task Import_PresetChange_RebuildsTree()
    {
        Add("a.jpg", Oct14);
        var vm = await CreateAsync(Settings(JobKind.Import));
        Assert.Contains(Oct14Text, NodeNames(vm));

        vm.SelectedPreset = Preset(vm, "월 단위");
        await vm.WhenPlanned;

        Assert.Contains("2026-10", NodeNames(vm));
        Assert.DoesNotContain(Oct14Text, NodeNames(vm));
    }

    // ----- 화면(XAML) 연결: 앱을 띄우지 않고, 실제 화면 스레드에서 화면 컨트롤만 만들어 묶어 본다 -----

    private static void OnUiThread(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) =>
            {
                failure ??= e.Exception;
                e.Handled = true;
            };
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "화면 스레드 작업이 끝나지 않았습니다");
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("화면 스레드 작업 실패: " + failure);
        }
    }

    /// <summary>바인딩이 붙고 화면 작업이 끝날 때까지 화면 스레드를 돌린다.</summary>
    private static async Task SettleAsync() => await Dispatcher.Yield(DispatcherPriority.ContextIdle);

    private static List<DependencyObject> Descendants(DependencyObject parent)
    {
        var found = new List<DependencyObject>();
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            found.Add(child);
            found.AddRange(Descendants(child));
        }

        return found;
    }

    [Fact]
    public void View_ShowsExactCopy_AndRadioButtonsChangeOptions() => OnUiThread(async () =>
    {
        Add(Trip + @"\a.jpg", Oct14);
        Add(Trip + @"\b.jpg", null, fill: 2);
        var vm = await CreateAsync();
        var view = new PreviewView { DataContext = vm };
        await SettleAsync();
        var all = Descendants(view);
        var checks = all.OfType<CheckBox>().ToList();
        var radios = all.OfType<RadioButton>().ToList();

        Assert.Equal("폴더 이름을 이벤트 이름으로 쓰기", checks[0].Content);
        Assert.Equal("이름의 _ 를 폴더로 나누기", Assert.IsType<TextBlock>(checks[1].Content).Text); // 글자로 넣으면 _가 사라진다
        Assert.Equal(["유지", "제자리에 둠", "합치기", "제자리에 둠", "판단 필요 폴더로"], radios.Select(r => r.Content));
        foreach (var label in new[] { "하위 폴더", "판단 필요 파일" })
        {
            Assert.Contains(all.OfType<TextBlock>(), t => t.Text == label);
        }

        Assert.True(checks[0].IsChecked);
        Assert.False(checks[1].IsChecked);
        Assert.Equal([true, false, false, true, false], radios.Select(r => r.IsChecked));

        radios[2].IsChecked = true;
        Assert.Equal(SubfolderPolicy.Merge, vm.Subfolders);
        Assert.False(radios[0].IsChecked);
        await vm.WhenPlanned;

        var plans = _plans.Count;
        radios[4].IsChecked = true;
        Assert.False(vm.KeepReviewInPlace);
        Assert.False(radios[3].IsChecked);
        await vm.WhenPlanned;
        Assert.Equal(plans + 1, _plans.Count); // 라디오 두 개가 함께 바뀌어도 계획은 한 번만 다시 세운다

        radios[3].IsChecked = true;
        Assert.True(vm.KeepReviewInPlace);
        Assert.False(radios[4].IsChecked);

        checks[1].IsChecked = true;
        Assert.True(vm.SplitUnderscores);
        await vm.WhenPlanned;

        Assert.True(_plans[^1].Settings.SplitUnderscores);
        Assert.True(_plans[^1].Settings.KeepReviewInPlace);
        Assert.Equal(SubfolderPolicy.Merge, _plans[^1].Settings.Subfolders);
    });

    [Theory]
    [InlineData(JobKind.Import, Visibility.Collapsed, Visibility.Collapsed)]
    [InlineData(JobKind.Absorb, Visibility.Visible, Visibility.Collapsed)]
    [InlineData(JobKind.Reorganize, Visibility.Visible, Visibility.Visible)]
    public void View_HidesOptionsByJobKind(JobKind kind, Visibility folder, Visibility review) => OnUiThread(async () =>
    {
        Add("a.jpg", Oct14);
        var vm = await CreateAsync(Settings(kind));
        var view = new PreviewView { DataContext = vm };
        await SettleAsync();
        var all = Descendants(view);
        var radios = all.OfType<RadioButton>().ToList();
        var presets = all.OfType<ComboBox>().Single();

        Assert.Equal(folder, ((FrameworkElement)all.OfType<CheckBox>().First().Parent).Visibility);
        Assert.Equal(folder, ((FrameworkElement)radios[0].Parent).Visibility);
        Assert.Equal(review, ((FrameworkElement)radios[3].Parent).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)presets.Parent).Visibility);
        Assert.Same(vm.SelectedPreset, presets.SelectedItem);
    });

    [Fact]
    public void View_BoundListBox_KeepsSelectionAcrossCardSwap() => OnUiThread(async () =>
    {
        Add("a.jpg", Oct14);
        Add(Trip + @"\b.jpg", Oct15, fill: 2);
        var vm = await CreateAsync();
        var view = new PreviewView { DataContext = vm };
        await SettleAsync();
        var cards = Descendants(view).OfType<ListBox>().Single(list => ReferenceEquals(list.ItemsSource, vm.Sessions));
        vm.SelectedSession = vm.Sessions.Single(c => c.SessionId == $"{Oct15Text}|{Trip}");
        await SettleAsync();
        Assert.Same(vm.SelectedSession, cards.SelectedItem);

        vm.Subfolders = SubfolderPolicy.Merge;
        await vm.WhenPlanned;
        await SettleAsync();

        Assert.Equal($"{Oct15Text}|{Trip}", vm.SelectedSession?.SessionId);
        Assert.Same(vm.SelectedSession, cards.SelectedItem);
        Assert.Equal("b.jpg", vm.Detail?.FileName);

        vm.UseFolderNames = false;
        await vm.WhenPlanned;
        await SettleAsync();

        Assert.Equal(Oct15Text, vm.SelectedSession?.SessionId);
        Assert.Same(vm.SelectedSession, cards.SelectedItem);
        Assert.Equal("b.jpg", vm.Detail?.FileName);

        var presets = Descendants(view).OfType<ComboBox>().Single();
        presets.SelectedItem = Preset(vm, "날짜만");
        await vm.WhenPlanned;
        await SettleAsync();

        Assert.Equal("날짜만", vm.SelectedPreset?.Name);
        Assert.Same(BuiltInPresets.DateOnly, _plans[^1].Settings.Preset);
        Assert.Same(vm.SelectedSession, cards.SelectedItem);
    });

    // ----- 프리셋 목록 -----

    [Fact]
    public async Task SelectedPreset_FindsUserPresetByFile()
    {
        Add("a.jpg", Oct14);
        var mine = new PresetEntry("같은 이름", BuiltInPresets.DateOnly with { Name = "같은 이름" }, "", @"C:\p\둘.json");
        var other = new PresetEntry("같은 이름", BuiltInPresets.Monthly with { Name = "같은 이름" }, "", @"C:\p\하나.json");
        var settings = Settings() with { Preset = mine.Preset, PresetFile = @"C:\P\둘.JSON" };

        var vm = await CreateAsync(settings, presets: [other, mine]);

        Assert.Same(mine, vm.SelectedPreset);
        Assert.Equal(2, vm.Presets.Count);
    }

    [Fact]
    public async Task SelectedPreset_NotInList_IsAdded()
    {
        Add("a.jpg", Oct14);
        var opened = BuiltInPresets.DateOnly with { Name = "연 파일" };
        var settings = Settings() with { Preset = opened, PresetFile = @"C:\다른 곳\연 파일.json" };

        var vm = await CreateAsync(settings);

        Assert.Equal(PresetLibrary.BuiltIn.Count + 1, vm.Presets.Count);
        Assert.Same(opened, vm.SelectedPreset?.Preset);
        Assert.Equal(@"C:\다른 곳\연 파일.json", vm.SelectedPreset?.FilePath);
        Assert.Contains(vm.SelectedPreset, vm.Presets);
    }

    [Fact]
    public async Task SelectedPreset_Null_IsIgnored()
    {
        Add("a.jpg", Oct14);
        var vm = await CreateAsync();
        var plans = _plans.Count;
        var selected = vm.SelectedPreset;

        vm.SelectedPreset = null;
        await vm.WhenPlanned;

        Assert.Same(selected, vm.SelectedPreset);
        Assert.Equal(plans, _plans.Count);
    }

    // ----- 저장과 뒤로 -----

    [Fact]
    public async Task Execute_SavesChoices()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();
        vm.SplitUnderscores = true;
        vm.Subfolders = SubfolderPolicy.Merge;
        vm.KeepReviewInPlace = false;
        vm.SelectedPreset = Preset(vm, "날짜만");
        await vm.WhenPlanned;
        Assert.Empty(_saved); // 옵션을 바꿀 때는 저장하지 않는다

        vm.ExecuteCommand.Execute(null);

        var saved = Assert.Single(_saved);
        Assert.True(saved.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Merge, saved.Subfolders);
        Assert.False(saved.KeepReviewInPlace);
        Assert.Same(BuiltInPresets.DateOnly, saved.Preset);
        Assert.Null(saved.PresetFile);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task Execute_Twice_SavesAndNavigatesOnce()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();

        vm.ExecuteCommand.Execute(null);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        vm.ExecuteCommand.Execute(null);

        Assert.Single(_saved);
        Assert.Single(_runs);
    }

    [Fact]
    public async Task Execute_AfterRefusedConfirm_CanRunAgain()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var settings = Settings();
        var scan = await _workflow.ScanAsync(settings, null, CancellationToken.None);
        var vm = Create(settings, scan with { Errors = [new PhotoOk.Core.Scanning.ScanError(@"D:\x", "폴더를 읽지 못했습니다")] });
        await vm.WhenPlanned;
        _dialogs.ConfirmAnswers.Enqueue(false);
        _dialogs.ConfirmAnswers.Enqueue(true);

        vm.ExecuteCommand.Execute(null);
        Assert.True(vm.ExecuteCommand.CanExecute(null));
        vm.ExecuteCommand.Execute(null);

        Assert.Single(_saved);
        Assert.Single(_runs);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Execute_UnreadableSettingsFile_KeepsFile_StillRuns()
    {
        Add(Trip + @"\a.jpg", Oct14);
        AppSettingsStore.Save(_fs, SettingsPath, new AppSettings([@"E:\DCIM"], false, Archive, "날짜 + 이벤트", null, TransferMode.Copy));
        var before = _fs.ReadAllBytes(SettingsPath);
        var vm = await CreateAsync(saveChoices: s => AppSettingsStore.SaveChoices(_fs, SettingsPath, s));
        _fs.DenyReadsOf(SettingsPath);

        vm.ExecuteCommand.Execute(null);

        Assert.Single(_runs);
        Assert.Empty(_dialogs.Errors);
        Assert.Equal(before, _fs.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public async Task Execute_RefusedConfirm_DoesNotSave()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var settings = Settings();
        var scan = await _workflow.ScanAsync(settings, null, CancellationToken.None);
        var vm = Create(settings, scan with { Errors = [new PhotoOk.Core.Scanning.ScanError(@"D:\x", "폴더를 읽지 못했습니다")] });
        await vm.WhenPlanned;
        _dialogs.ConfirmAnswers.Enqueue(false);

        vm.ExecuteCommand.Execute(null);

        Assert.Empty(_saved);
        Assert.Empty(_runs);
    }

    [Fact]
    public async Task Execute_SaveFailure_StillRuns()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync(saveChoices: _ => throw new IOException("쓸 수 없음"));

        vm.ExecuteCommand.Execute(null);

        Assert.Single(_runs);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Execute_SavedChoices_BecomeNextDefaults()
    {
        Add(Trip + @"\a.jpg", Oct14);
        Add("b.jpg", Oct14, fill: 2); // 하위 폴더를 제자리에 둬도 옮길 파일이 남는다
        AppSettingsStore.Save(_fs, SettingsPath,
            new AppSettings([@"E:\DCIM"], false, Archive, "날짜 + 이벤트", null, TransferMode.Copy));
        var vm = await CreateAsync(saveChoices: s => AppSettingsStore.SaveChoices(_fs, SettingsPath, s));
        vm.SplitUnderscores = true;
        vm.Subfolders = SubfolderPolicy.InPlace;
        vm.KeepReviewInPlace = false;
        vm.SelectedPreset = Preset(vm, "날짜만");
        await vm.WhenPlanned;

        vm.ExecuteCommand.Execute(null);

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal("날짜만", loaded.PresetName);
        Assert.Equal([@"E:\DCIM"], loaded.Sources); // 재정리는 가져오기의 폴더를 건드리지 않는다

        var sent = new List<ImportSettings>();
        var source = new SourceViewModel(_shell, _fs, new PresetLibrary(_fs, PresetFolder), SettingsPath, loaded, JobKind.Reorganize,
            null, new FakeFolderPicker(), new FakeFileOpenPicker(), _dialogs, new FileLog(_fs, LogFolder),
            createScan: s =>
            {
                sent.Add(s);
                return new ScreenStub();
            },
            createStart: () => new ScreenStub(), runCheck: check => Task.FromResult(check()));
        source.Sources.Add(Old);
        await source.WhenArchiveChecked;
        source.NextCommand.Execute(null);

        var next = Assert.Single(sent);
        Assert.True(next.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.InPlace, next.Subfolders);
        Assert.False(next.KeepReviewInPlace);
        Assert.Same(BuiltInPresets.DateOnly, next.Preset);
    }

    [Fact]
    public async Task Back_CarriesCurrentOptions_EvenWhilePlanning()
    {
        Add(Trip + @"\a.jpg", Oct14);
        var vm = await CreateAsync();

        _gate.Reset();
        vm.UseFolderNames = false;
        vm.SelectedPreset = Preset(vm, "날짜만");
        vm.BackCommand.Execute(null);
        _gate.Set();
        await vm.WhenPlanned;

        var back = Assert.Single(_backs);
        Assert.False(back.UseFolderNames);
        Assert.Same(BuiltInPresets.DateOnly, back.Preset);
        Assert.Empty(_saved); // 뒤로 갈 때는 저장하지 않는다
        Assert.Empty(_runs);
    }
}
