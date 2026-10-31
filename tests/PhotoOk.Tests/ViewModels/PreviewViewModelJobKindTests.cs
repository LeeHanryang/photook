using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

/// <summary>미리보기 화면이 폴더 이름에서 읽은 이벤트 이름과 흡수·재정리의 표시를 보이는 부분. 가져오기 자체의 시험은 <see cref="PreviewViewModelTests"/>.</summary>
public class PreviewViewModelJobKindTests
{
    private const string Archive = @"D:\Archive";
    private const string Old = @"D:\Archive\모음";
    private const string LogFolder = @"C:\Users\test\AppData\Local\PhotoOk\logs";
    private const string IgnoredPath = @"C:\Users\test\AppData\Roaming\PhotoOk\ignored-folder-names.txt";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FileTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeThumbnailProvider _thumbnails = new();
    private readonly ShellViewModel _shell;
    private readonly ImportWorkflow _workflow;
    private readonly List<IReadOnlyDictionary<string, string>> _planNames = [];
    private ScanFacts? _runFacts;
    private bool _failRunScreen;

    public PreviewViewModelJobKindTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(Old);
        _fs.SetFreeSpace(@"D:\", 50L * 1024 * 1024 * 1024);
        _workflow = new ImportWorkflow(_fs, new FakeMetadataReader(), new XxHasher(_fs), TimeZoneInfo.Utc, IgnoredPath);
    }

    private sealed class ScreenStub : ObservableObject;

    private static ImportSettings Settings(JobKind kind = JobKind.Reorganize, bool keepReview = true) =>
        new([Old], true, Archive, BuiltInPresets.DateEvent, TransferMode.Move, kind, UseFolderNames: true,
            KeepReviewInPlace: keepReview);

    private FileGroup Add(string path, DateTime? capture, byte fill = 1)
    {
        byte[] content = [fill, fill, fill];
        _fs.AddFile(path, content);
        return new FileGroup(new PhotoInfo(path, content.Length, FileTime, FileKinds.Default.Classify(path), capture,
            null, null, null), []);
    }

    private static ScanOutcome Scan(IReadOnlyList<FileGroup> groups, Func<FileGroup, string?>? nameOf = null) =>
        new(SessionGrouper.GroupByDay(groups, TimeOnly.MinValue, TimeZoneInfo.Utc, nameOf), [], [],
            groups.Sum(g => g.AllFiles.Count()), false);

    private async Task<PreviewViewModel> CreateAsync(ScanOutcome scan, ImportSettings? settings = null,
        IFileSystem? fileSystem = null, Func<PlanOutcome, PlanOutcome>? rewrite = null)
    {
        settings ??= Settings();
        var vm = new PreviewViewModel(_shell, _workflow, settings, scan, fileSystem ?? _fs, _thumbnails, _dialogs,
            new FileLog(_fs, LogFolder),
            createRun: (_, facts) =>
            {
                if (_failRunScreen)
                {
                    throw new InvalidOperationException("실행 화면을 만들지 못함");
                }

                _runFacts = facts;
                return new ScreenStub();
            }, createBack: _ => new ScreenStub(), replanDelay: TimeSpan.Zero,
            ignoredNamesPath: IgnoredPath, presets: PresetLibrary.BuiltIn, saveChoices: _ => { },
            buildPlan: (s, scanned, names) =>
            {
                lock (_planNames)
                {
                    _planNames.Add(new Dictionary<string, string>(names));
                }

                var outcome = _workflow.BuildPlan(s, scanned, names);
                return rewrite is null ? outcome : rewrite(outcome);
            });
        _shell.Navigate(vm);
        await vm.WhenPlanned;
        return vm;
    }

    private static IEnumerable<FolderNodeViewModel> Flatten(IEnumerable<FolderNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private string[] IgnoredLines()
    {
        using var stream = _fs.OpenReadShared(IgnoredPath);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    // ----- 읽어 온 이벤트 이름 -----

    [Fact]
    public async Task Suggestion_FillsEventName_AndGoesIntoFirstPlan()
    {
        var group = Add(Old + @"\a.jpg", Oct14);

        var vm = await CreateAsync(Scan([group], _ => "데이트"));

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("데이트", card.EventName);
        Assert.Equal("2026-10-14|데이트", card.SessionId);
        var first = Assert.Single(_planNames);
        Assert.Equal("데이트", first["2026-10-14|데이트"]);
        Assert.Contains(@"2026\2026-10-14 데이트", vm.Detail?.Destination);
    }

    [Fact]
    public async Task ClearedSuggestion_StaysEmptyAfterReplan()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var vm = await CreateAsync(Scan([group], _ => "데이트"));

        vm.Sessions[0].EventName = "";
        await vm.WhenPlanned;

        // 제안이 되살아나지 않도록 빈 문자열로 넘긴다.
        Assert.Equal("", _planNames[^1]["2026-10-14|데이트"]);
        Assert.EndsWith(@"2026\2026-10-14\a.jpg", vm.Detail?.Destination);
    }

    [Fact]
    public async Task CardTitle_UsesDate_NotSessionId()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", Oct14, 2);

        var vm = await CreateAsync(Scan([a, b], _ => "데이트"));

        var card = Assert.Single(vm.Sessions);
        Assert.Equal("2026-10-14|데이트", card.SessionId);
        Assert.Equal("2026-10-14 · 2장", card.Title);
    }

    [Fact]
    public async Task SameDayTwoNames_TwoCards()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", Oct14, 2);
        var c = Add(Old + @"\c.jpg", Oct14, 3);

        var vm = await CreateAsync(Scan([a, b, c], g => g == a ? "데이트" : g == b ? "여행" : null));

        Assert.Equal(["2026-10-14 · 1장", "2026-10-14 · 1장", "2026-10-14 · 1장"], vm.Sessions.Select(s => s.Title));
        Assert.Equal(["", "데이트", "여행"], vm.Sessions.Select(s => s.EventName));
        Assert.Equal(3, vm.Sessions.Select(s => s.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task NameFromFolder_FalseAfterEdit()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var vm = await CreateAsync(Scan([group], _ => "데이트"));
        var card = vm.Sessions[0];
        var changes = new List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(card.NameFromFolder);

        card.EventName = "데이트 2";
        Assert.False(card.NameFromFolder);
        Assert.Contains(nameof(SessionItemViewModel.NameFromFolder), changes);

        card.EventName = "데이트";
        Assert.True(card.NameFromFolder);
    }

    [Fact]
    public async Task NameFromFolder_FalseWithoutSuggestion_AndForReviewCard()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", null, 2);

        var vm = await CreateAsync(Scan([a, b]));

        Assert.All(vm.Sessions, card => Assert.False(card.NameFromFolder));
        vm.Sessions[0].EventName = "직접 쓴 이름";
        Assert.False(vm.Sessions[0].NameFromFolder);
    }

    // ----- 이 이름 다시 쓰지 않기 -----

    [Fact]
    public async Task NeverUseName_AppendsAndClearsAllCardsWithName()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", Oct15, 2);
        var c = Add(Old + @"\c.jpg", Oct15, 3);
        var vm = await CreateAsync(Scan([a, b, c], g => g == c ? "여행" : "데이트"));
        var before = _planNames.Count;

        vm.Sessions[0].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(["데이트"], IgnoredLines());
        Assert.Equal(["", "", "여행"], vm.Sessions.Select(s => s.EventName));
        Assert.Equal(before + 1, _planNames.Count); // 카드를 둘 비워도 계획은 한 번만 다시 세운다
        Assert.Equal("", _planNames[^1]["2026-10-14|데이트"]);
        Assert.Equal("", _planNames[^1]["2026-10-15|데이트"]);
        Assert.Equal("여행", _planNames[^1]["2026-10-15|여행"]);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task NeverUseName_MatchesCardsIgnoringCaseAndSpaces()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", Oct15, 2);
        var vm = await CreateAsync(Scan([a, b], g => g == a ? "Cafe" : " cafe "));
        Assert.Equal(["Cafe", "cafe"], vm.Sessions.Select(s => s.EventName)); // 묶을 때 이름을 다듬는다

        vm.Sessions[0].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(["Cafe"], IgnoredLines());
        Assert.Equal(["", ""], vm.Sessions.Select(s => s.EventName));
    }

    [Fact]
    public async Task NeverUseName_KeepsHandTypedNames()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", Oct15, 2);
        var c = Add(Old + @"\c.jpg", Oct15, 3);
        var d = Add(Old + @"\d.jpg", Oct15, 4);
        var vm = await CreateAsync(Scan([a, b, c, d], g => g == a ? "데이트" : g == b ? "여행" : g == c ? "데이트" : null));
        var byId = vm.Sessions.ToDictionary(s => s.SessionId);
        byId["2026-10-15|여행"].EventName = "데이트";    // 읽어 온 이름이 아니라 직접 쓴 것
        byId["2026-10-15"].EventName = "데이트";          // 읽어 온 이름이 없던 카드에 직접 씀
        await vm.WhenPlanned;
        var before = _planNames.Count;

        byId["2026-10-14|데이트"].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal("", byId["2026-10-14|데이트"].EventName);
        Assert.Equal("", byId["2026-10-15|데이트"].EventName);
        Assert.Equal("데이트", byId["2026-10-15|여행"].EventName);
        Assert.Equal("데이트", byId["2026-10-15"].EventName);
        Assert.Equal(before + 1, _planNames.Count);
    }

    [Fact]
    public async Task NeverUseName_NameWithBackslash_WritesUnderscore()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var vm = await CreateAsync(Scan([group], _ => @"가\나"));

        Assert.Equal(@"가\나", vm.Sessions[0].EventName);
        vm.Sessions[0].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(["가_나"], IgnoredLines());
        Assert.Equal("", vm.Sessions[0].EventName);
    }

    [Fact]
    public async Task NeverUseName_WriteFailure_ShowsErrorKeepsName()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var hooked = new HookedFileSystem(_fs) { BeforeAppend = _ => throw new IOException("쓸 수 없음") };
        var vm = await CreateAsync(Scan([group], _ => "데이트"), fileSystem: hooked);
        var before = _planNames.Count;

        vm.Sessions[0].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        var error = Assert.Single(_dialogs.Errors);
        Assert.DoesNotContain("쓸 수 없음", error.Message); // 예외 원문은 화면에 보이지 않는다
        Assert.Equal("데이트", vm.Sessions[0].EventName);
        Assert.Equal(before, _planNames.Count);
    }

    private static ScanOutcome ScanWithFormat(IReadOnlyList<FileGroup> groups, Func<FileGroup, string?> nameOf, Func<FileGroup, bool> isFormat) =>
        new(SessionGrouper.GroupByDay(groups, TimeOnly.MinValue, TimeZoneInfo.Utc, nameOf, isFormat), [], [],
            groups.Sum(g => g.AllFiles.Count()), false);

    [Fact]
    public async Task FormatName_ShowsLabel_ButNoButton()
    {
        var group = Add(Old + @"\a.jpg", Oct14);

        var vm = await CreateAsync(ScanWithFormat([group], _ => "데이트", _ => true));

        var card = vm.Sessions[0];
        Assert.True(card.NameFromFolder);
        Assert.False(card.CanNeverUseName);

        card.NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.False(_fs.FileExists(IgnoredPath));
        Assert.Equal("데이트", card.EventName);
    }

    [Fact]
    public async Task ClassifierName_ShowsButtonOnlyWhileUnedited()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var vm = await CreateAsync(ScanWithFormat([group], _ => "데이트", _ => false));
        var card = vm.Sessions[0];
        var changes = new List<string?>();
        card.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(card.CanNeverUseName);

        card.EventName = "다른 이름";

        Assert.False(card.CanNeverUseName);
        Assert.Contains(nameof(SessionItemViewModel.CanNeverUseName), changes);
    }

    [Fact]
    public async Task NeverUseName_LeavesFormatCardWithSameName()
    {
        var classifier = Add(Old + @"\a.jpg", Oct14);
        var format = Add(Old + @"\b.jpg", Oct15, 2);
        var vm = await CreateAsync(ScanWithFormat([classifier, format], _ => "데이트", g => g == format));

        vm.Sessions[0].NeverUseNameCommand.Execute(null);
        await vm.WhenPlanned;

        Assert.Equal(["데이트"], IgnoredLines());
        Assert.Equal(["", "데이트"], vm.Sessions.Select(s => s.EventName));
    }

    // ----- 건너뜀 문구와 개수 -----

    private static Func<PlanOutcome, PlanOutcome> SkipEverything(SkipReason reason, string? existing = null) =>
        outcome => outcome with
        {
            Plan = outcome.Plan with
            {
                Operations = [],
                Skipped = [.. outcome.Plan.Operations
                    .Where(op => op.Source is not null)
                    .Select(op => new PlannedSkip(op.Source!, reason == SkipReason.DuplicateInArchive ? op.Destination : op.Source!,
                        reason, existing))],
            },
        };

    [Fact]
    public async Task DuplicateCount_IncludesDuplicateInArchive()
    {
        var group = Add(Old + @"\a.jpg", Oct14);

        var vm = await CreateAsync(Scan([group]),
            rewrite: SkipEverything(SkipReason.DuplicateInArchive, @"D:\Archive\2024\a.jpg"));

        Assert.Equal(1, vm.DuplicateCount);
        Assert.Equal(0, vm.KeptInPlaceCount);
    }

    [Fact]
    public async Task Detail_ShowsExistingPath()
    {
        var group = Add(Old + @"\a.jpg", Oct14);

        var vm = await CreateAsync(Scan([group]),
            rewrite: SkipEverything(SkipReason.DuplicateInArchive, @"D:\Archive\2024\a.jpg"));

        Assert.Equal(@"이미 있음: D:\Archive\2024\a.jpg", vm.Detail?.SkipNote);
    }

    [Theory]
    [InlineData(SkipReason.ReviewKeptInPlace, "판단 필요라 제자리에 둡니다")]
    [InlineData(SkipReason.SubfolderKeptInPlace, "하위 폴더라 제자리에 둡니다")]
    [InlineData(SkipReason.OtherVolume, "다른 볼륨에 있어 옮기지 않습니다")]
    [InlineData(SkipReason.AlreadyInPlace, "이미 제자리에 있어 건너뜁니다")]
    public async Task Detail_ShowsSkipNoteForReason(SkipReason reason, string expected)
    {
        var group = Add(Old + @"\a.jpg", Oct14);

        var vm = await CreateAsync(Scan([group]), rewrite: SkipEverything(reason));

        Assert.Equal(expected, vm.Detail?.SkipNote);
    }

    // ----- 재정리 표시 -----

    [Fact]
    public async Task Reorganize_ShowsKeptInPlaceCount_AndTreeOnlyChanged()
    {
        var moved = Add(Old + @"\x.jpg", Oct14);
        var inPlace = Add(@"D:\Archive\2026\2026-10-15\y.jpg", Oct15, 2);
        var review = Add(Old + @"\z.jpg", null, 3);

        var vm = await CreateAsync(Scan([moved, inPlace, review]));

        Assert.Equal(1, vm.TransferCount);
        Assert.Equal(2, vm.KeptInPlaceCount); // 이미 제자리인 파일과 판단 필요 파일
        Assert.Equal("그대로 2개", vm.KeptInPlaceText);
        Assert.Equal(1, vm.ReviewCount);
        var names = Flatten(vm.FolderTree).Select(n => n.Name).ToList();
        Assert.Contains("2026-10-14", names);
        Assert.DoesNotContain("2026-10-15", names);
        Assert.DoesNotContain("모음", names);
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public async Task KeptInPlaceText_OnlyForReorganize(JobKind kind)
    {
        var group = Add(Old + @"\a.jpg", Oct14);

        var vm = await CreateAsync(Scan([group]), Settings(kind));

        Assert.Equal(0, vm.KeptInPlaceCount);
        Assert.Null(vm.KeptInPlaceText);
    }

    [Fact]
    public async Task Reorganize_ReviewCardLabel()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", null, 2);

        var vm = await CreateAsync(Scan([a, b]));

        var review = vm.Sessions[^1];
        Assert.True(review.IsReviewGroup);
        Assert.Equal("판단 필요 · 1장 (그대로 둠)", review.Title);
        Assert.Equal("자동으로 정할 수 없어 옮기지 않습니다", review.Note);
        Assert.Null(vm.Sessions[0].Note);
    }

    [Fact]
    public async Task Reorganize_ReviewOptionOff_UsesOrdinaryReviewCard()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", null, 2);

        var vm = await CreateAsync(Scan([a, b]), Settings(keepReview: false));

        var review = vm.Sessions[^1];
        Assert.Equal("판단 필요 · 1장", review.Title);
        Assert.Null(review.Note);
        Assert.Equal(0, vm.KeptInPlaceCount);
    }

    [Fact]
    public async Task Absorb_ReviewCard_IsOrdinary()
    {
        var b = Add(Old + @"\b.jpg", null, 2);

        var vm = await CreateAsync(Scan([b]), Settings(JobKind.Absorb));

        Assert.Equal("판단 필요 · 1장", vm.Sessions[^1].Title);
        Assert.Null(vm.Sessions[^1].Note);
    }

    [Fact]
    public async Task Reorganize_NoMoves_BlocksExecute()
    {
        var inPlace = Add(@"D:\Archive\2026\2026-10-15\y.jpg", Oct15);
        var review = Add(Old + @"\z.jpg", null, 3);

        var vm = await CreateAsync(Scan([inPlace, review]));

        Assert.Equal(0, vm.TransferCount);
        Assert.Equal("옮길 파일이 없습니다 — 모두 제자리에 있습니다", vm.BlockingReason);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
        Assert.Equal(2, vm.KeptInPlaceCount);
    }

    [Fact]
    public async Task Absorb_NoMoves_KeepsImportMessage()
    {
        var vm = await CreateAsync(Scan([]), Settings(JobKind.Absorb));

        Assert.Equal("옮길 파일이 없습니다 — 모두 보관 폴더에 있거나 중복입니다", vm.BlockingReason);
    }

    [Fact]
    public async Task Reorganize_MultiDayNote_ReachesPlanNotes()
    {
        var group = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", Oct14);

        var vm = await CreateAsync(Scan([group]));

        Assert.Contains("여러 날을 묶은 폴더 1개가 날짜별로 나뉩니다. 여러 날 묶기가 들어오기 전에는 재정리에서 빼 주세요", vm.PlanNotes);
    }

    // ----- 실행 잠금 -----

    [Fact]
    public async Task Execute_FailsBeforeNavigation_CanRunAgain()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var vm = await CreateAsync(Scan([group]));
        _failRunScreen = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ExecuteCommand.ExecuteAsync(null));

        Assert.Same(vm, _shell.Current);
        Assert.True(vm.ExecuteCommand.CanExecute(null));

        // 화면을 떠나지 않았으므로 이름을 고치면 계획도 다시 세운다.
        vm.Sessions[0].EventName = "다른 이름";
        await vm.WhenPlanned;
        Assert.Equal("다른 이름", _planNames[^1]["2026-10-14"]);
        Assert.True(vm.ExecuteCommand.CanExecute(null));

        _failRunScreen = false;
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.IsType<ScreenStub>(_shell.Current);
        Assert.NotNull(_runFacts);
    }

    [Fact]
    public async Task Execute_FailsAfterNavigation_StaysLatched()
    {
        var group = Add(Old + @"\a.jpg", Oct14);
        var vm = await CreateAsync(Scan([group]));
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Current))
            {
                throw new InvalidOperationException("화면을 바꾼 뒤의 오류");
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ExecuteCommand.ExecuteAsync(null));

        Assert.IsType<ScreenStub>(_shell.Current);
        Assert.False(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reorganize_Execute_PassesResultFactsToRunScreen()
    {
        var moved = Add(Old + @"\x.jpg", Oct14);
        var inPlace = Add(@"D:\Archive\2026\2026-10-15\y.jpg", Oct15, 2);
        var review = Add(Old + @"\z.jpg", null, 3);
        var vm = await CreateAsync(Scan([moved, inPlace, review]));

        await vm.ExecuteCommand.ExecuteAsync(null);

        var facts = Assert.IsType<ScanFacts>(_runFacts);
        Assert.Equal(JobKind.Reorganize, facts.Kind);
        Assert.True(facts.AnyMoved);
        Assert.Equal(1, facts.KeptInPlace); // 이미 제자리인 파일. 판단 필요는 따로 센다
        Assert.Equal(1, facts.ReviewKept);
        Assert.Equal(0, facts.AlreadyInArchive);
    }

    [Fact]
    public async Task Absorb_Execute_PassesAlreadyInArchiveToRunScreen()
    {
        var a = Add(Old + @"\a.jpg", Oct14);
        var b = Add(Old + @"\b.jpg", Oct15, 2);
        var vm = await CreateAsync(Scan([a, b]), Settings(JobKind.Absorb),
            rewrite: outcome =>
            {
                var first = outcome.Plan.Operations.First(op => op.Source is not null && op.Source.EndsWith(@"\a.jpg", StringComparison.Ordinal));
                return outcome with
                {
                    Plan = outcome.Plan with
                    {
                        Operations = [.. outcome.Plan.Operations.Where(op => op != first)],
                        Skipped = [.. outcome.Plan.Skipped, new PlannedSkip(first.Source!, first.Destination, SkipReason.DuplicateInArchive, @"D:\Archive\2024\a.jpg")],
                    },
                };
            });

        await vm.ExecuteCommand.ExecuteAsync(null);

        var facts = Assert.IsType<ScanFacts>(_runFacts);
        Assert.Equal(JobKind.Absorb, facts.Kind);
        Assert.True(facts.AnyMoved);
        Assert.Equal(1, facts.AlreadyInArchive);
        Assert.Equal(0, facts.KeptInPlace);
    }
}
