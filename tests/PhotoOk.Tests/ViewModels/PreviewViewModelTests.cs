using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class PreviewViewModelTests
{
    private const string Archive = @"D:\Archive";
    private const string LogFolder = @"C:\Users\test\AppData\Local\PhotoOk\logs";
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeThumbnailProvider _thumbnails = new();
    private readonly ShellViewModel _shell;
    private readonly ObservableObject _runScreen = new ScreenStub();
    private readonly ObservableObject _backScreen = new ScreenStub();
    private readonly List<(Plan Plan, int ScanErrors, int Ignored, bool SubfoldersNotImported)> _runRequests = [];
    private readonly ImportSettings _settings = new([@"E:\DCIM"], true, Archive, BuiltInPresets.DateEvent, TransferMode.Copy);
    private readonly ImportWorkflow _workflow;

    public PreviewViewModelTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(Archive);
        _fs.SetFreeSpace(@"D:\", 50 * Gb);
        _workflow = new ImportWorkflow(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc);
    }

    private sealed class ScreenStub : ObservableObject;

    private void AddPhoto(string path, byte[] content, DateTime? capture, string? model = null)
    {
        _fs.AddFile(path, content);
        _reader.Set(path, capture, model is null ? null : "Maker", model);
    }

    /// <summary>
    /// 14일 A(3바이트)·B(2바이트), 15일 C(보관 폴더에 같은 파일이 있음), 촬영일 없는 D(4바이트).
    /// </summary>
    private void AddStandardPhotos()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1, 1, 1], Oct14, "CAM-1");
        AddPhoto(@"E:\DCIM\B.JPG", [2, 2], Oct14, "CAM-2");
        AddPhoto(@"E:\DCIM\C.JPG", [3, 3, 3, 3, 3], Oct15);
        _fs.AddFile(@"D:\Archive\2026\2026-10-15\C.JPG", [3, 3, 3, 3, 3]);
        _fs.AddFile(@"E:\DCIM\D.JPG", [4, 4, 4, 4], new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
        _reader.Set(@"E:\DCIM\D.JPG", null);
    }

    private async Task<ScanOutcome> ScanAsync() => await _workflow.ScanAsync(_settings, null, CancellationToken.None);

    private async Task<PreviewViewModel> CreateAsync(ScanOutcome? scan = null,
        Func<ImportSettings, ScanOutcome, IReadOnlyDictionary<string, string>, PlanOutcome>? buildPlan = null,
        IFileSystem? fileSystem = null)
    {
        scan ??= await ScanAsync();
        var vm = new PreviewViewModel(_shell, _workflow, _settings, scan, fileSystem ?? _fs, _thumbnails, _dialogs,
            new FileLog(_fs, LogFolder),
            createRun: (plan, scan) =>
            {
                _runRequests.Add((plan, scan.ScanErrors, scan.IgnoredFiles, scan.SubfoldersNotImported));
                return _runScreen;
            },
            createBack: () => _backScreen,
            replanDelay: TimeSpan.Zero,
            buildPlan: buildPlan);
        _shell.Navigate(vm);
        await vm.WhenPlanned;
        return vm;
    }

    private static IEnumerable<FolderNodeViewModel> Flatten(IEnumerable<FolderNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    private static FolderNodeViewModel Node(PreviewViewModel vm, params string[] path)
    {
        var nodes = (IEnumerable<FolderNodeViewModel>)vm.FolderTree;
        FolderNodeViewModel? node = null;
        foreach (var name in path)
        {
            node = nodes.SingleOrDefault(n => n.Name == name)
                ?? throw new Xunit.Sdk.XunitException(
                    $"트리에 '{name}'이 없습니다. 있는 것: {string.Join(", ", Flatten(vm.FolderTree).Select(n => n.Name))}");
            nodes = node.Children;
        }

        return node!;
    }

    [Fact]
    public async Task Preview_ShowsSummaryCounts()
    {
        AddStandardPhotos();

        var vm = await CreateAsync();

        Assert.False(vm.IsReplanning);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, vm.ReviewCount);
        Assert.Equal(1, vm.DuplicateCount);
        Assert.Equal(3, vm.TransferCount);
        Assert.Equal(9, vm.TransferBytes);
        Assert.Equal(50 * Gb, vm.FreeBytes);
        Assert.Equal("50.0 GB", vm.FreeSpaceText);
        Assert.Null(vm.BlockingReason);
        Assert.Empty(vm.ScanErrors);
        Assert.Null(vm.ScanErrorNotice);
        Assert.True(vm.ExecuteCommand.CanExecute(null));

        // 날짜순 촬영 카드 뒤에 판단 필요 묶음
        Assert.Equal("2026-10-14 · 2장", vm.Sessions[0].Title);
        Assert.Equal("CAM-1, CAM-2", vm.Sessions[0].Devices);
        Assert.Equal("2026-10-15 · 1장", vm.Sessions[1].Title);
        var review = vm.Sessions[^1];
        Assert.True(review.IsReviewGroup);
        Assert.Equal("판단 필요 · 1장", review.Title);
        Assert.All(vm.Sessions.Take(vm.Sessions.Count - 1), s => Assert.False(s.IsReviewGroup));

        // 폴더 트리: 이번 실행으로 놓이는 파일만, 새 폴더 표시
        var day = Node(vm, "2026", "2026-10-14");
        Assert.Equal(2, day.FileCount);
        Assert.True(day.IsNew);
        Assert.Equal(2, Node(vm, "2026").FileCount);
        Assert.DoesNotContain(Flatten(vm.FolderTree), n => n.Name == "2026-10-15");
        Assert.Equal(1, Node(vm, "판단 필요").FileCount);
    }

    [Fact]
    public async Task Preview_ExistingFolder_IsNotNew()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        _fs.AddDirectory(@"D:\Archive\2026");

        var vm = await CreateAsync();

        Assert.False(Node(vm, "2026").IsNew);
        Assert.True(Node(vm, "2026", "2026-10-14").IsNew);
    }

    [Fact]
    public async Task EventName_Change_ReplansAndUpdatesTree()
    {
        AddStandardPhotos();
        var vm = await CreateAsync();
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        vm.Sessions[0].EventName = "결혼식";
        await vm.WhenPlanned;

        var renamed = Node(vm, "2026", "2026-10-14 결혼식");
        Assert.Equal(2, renamed.FileCount);
        Assert.DoesNotContain(Flatten(vm.FolderTree), n => n.Name == "2026-10-14");
        Assert.False(vm.IsReplanning);
        Assert.Equal(1, busyEnded);
        Assert.Equal(@"D:\Archive\2026\2026-10-14 결혼식\A.JPG", vm.Detail!.Destination);
    }

    [Fact]
    public async Task Replan_LatestEditWins()
    {
        AddStandardPhotos();
        using var gate = new ManualResetEventSlim();
        using var slowStarted = new ManualResetEventSlim();
        var vm = await CreateAsync(buildPlan: (settings, scan, names) =>
        {
            if (names.Values.Contains("a"))
            {
                slowStarted.Set();
                gate.Wait(Wait);
            }

            return _workflow.BuildPlan(settings, scan, names);
        });

        vm.Sessions[0].EventName = "a";
        var first = vm.WhenPlanned;
        Assert.True(slowStarted.Wait(Wait));
        Assert.True(vm.IsReplanning);
        Assert.False(vm.ExecuteCommand.CanExecute(null));

        // 새 요청은 앞선 계획 세우기가 끝난 뒤에 계획을 세운다. 앞선 결과는 늦게 와도 버린다.
        vm.Sessions[0].EventName = "b";
        var second = vm.WhenPlanned;
        await first; // 앞선 요청은 취소되어 바로 끝난다(계획 세우기는 아직 돌고 있다)
        Assert.True(vm.IsReplanning);
        Assert.False(second.IsCompleted);

        gate.Set();
        await second;
        await Task.Delay(50);

        Assert.NotNull(Node(vm, "2026", "2026-10-14 b"));
        Assert.DoesNotContain(Flatten(vm.FolderTree), n => n.Name == "2026-10-14 a");
        Assert.False(vm.IsReplanning);
        Assert.True(vm.ExecuteCommand.CanExecute(null));
    }

    [Fact]
    public async Task Replan_DoesNotRunBuildsConcurrently_AndSkipsSupersededRequests()
    {
        AddStandardPhotos();
        using var gate = new ManualResetEventSlim();
        using var slowStarted = new ManualResetEventSlim();
        var running = 0;
        var maxRunning = 0;
        var built = new List<string>();
        var vm = await CreateAsync(buildPlan: (settings, scan, names) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (built)
            {
                maxRunning = Math.Max(maxRunning, now);
                built.Add(string.Join(",", names.Values));
            }

            try
            {
                if (names.Values.Contains("a"))
                {
                    slowStarted.Set();
                    gate.Wait(Wait);
                }

                return _workflow.BuildPlan(settings, scan, names);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        });

        vm.Sessions[0].EventName = "a";
        Assert.True(slowStarted.Wait(Wait));
        vm.Sessions[0].EventName = "b";
        vm.Sessions[0].EventName = "c";
        await Task.Delay(100);
        lock (built)
        {
            Assert.Equal(["", "a"], built); // "a"가 끝나기 전에는 다음 계획을 세우지 않는다
        }

        gate.Set();
        await vm.WhenPlanned;

        Assert.Equal(1, maxRunning);
        Assert.Equal(["", "a", "c"], built); // "b"는 차례가 오기 전에 더 새 요청으로 바뀌었다
        Assert.NotNull(Node(vm, "2026", "2026-10-14 c"));
        Assert.False(vm.IsReplanning);
    }

    [Fact]
    public async Task Execute_IsBlocked_WhenNothingToTransfer()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1, 1, 1], Oct14);
        _fs.AddFile(@"D:\Archive\2026\2026-10-14\A.JPG", [1, 1, 1]);

        var vm = await CreateAsync();

        Assert.Equal(0, vm.TransferCount);
        Assert.Equal("옮길 파일이 없습니다 — 모두 보관 폴더에 있거나 중복입니다", vm.BlockingReason);
        Assert.False(vm.ExecuteCommand.CanExecute(null));

        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(_runRequests);
    }

    [Fact]
    public async Task Back_DuringReplan_CancelsAndNavigates()
    {
        AddStandardPhotos();
        using var gate = new ManualResetEventSlim();
        using var slowStarted = new ManualResetEventSlim();
        var vm = await CreateAsync(buildPlan: (settings, scan, names) =>
        {
            if (names.Values.Contains("a"))
            {
                slowStarted.Set();
                gate.Wait(Wait);
            }

            return _workflow.BuildPlan(settings, scan, names);
        });
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        vm.Sessions[0].EventName = "a";
        Assert.True(slowStarted.Wait(Wait));
        vm.BackCommand.Execute(null);

        // 계획 세우기가 끝나기(gate)를 기다리지 않고 멈춘다.
        var stopped = vm.WhenPlanned;
        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(5))));
        gate.Set();

        Assert.Same(_backScreen, _shell.Current);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Close_DuringReplan_DoesNotAsk_BecausePlanningOnlyReads()
    {
        AddStandardPhotos();
        using var gate = new ManualResetEventSlim();
        using var slowStarted = new ManualResetEventSlim();
        var vm = await CreateAsync(buildPlan: (settings, scan, names) =>
        {
            if (names.Values.Contains("a"))
            {
                slowStarted.Set();
                gate.Wait(Wait);
            }

            return _workflow.BuildPlan(settings, scan, names);
        });

        vm.Sessions[0].EventName = "a";
        Assert.True(slowStarted.Wait(Wait));
        Assert.True(vm.IsReplanning);
        Assert.False(vm.IsBusy);
        Assert.False(_shell.IsBusy);

        // 확인 창 없이 바로 닫을 수 있다(FakeDialogService는 예상하지 못한 확인 창에서 예외를 던진다).
        Assert.True(_shell.ConfirmClose());
        Assert.Empty(_dialogs.Confirms);

        gate.Set();
        await vm.WhenPlanned;
    }

    [Fact]
    public async Task Execute_IsBlocked_ByPreflightError()
    {
        AddStandardPhotos();
        _fs.SetFreeSpace(@"D:\", 100);

        var vm = await CreateAsync();

        Assert.NotNull(vm.BlockingReason);
        Assert.StartsWith("대상 드라이브 공간이 부족합니다", vm.BlockingReason, StringComparison.Ordinal);
        Assert.False(vm.ExecuteCommand.CanExecute(null));

        await vm.ExecuteCommand.ExecuteAsync(null);
        Assert.Empty(_runRequests);
        Assert.Same(vm, _shell.Current);
    }

    [Fact]
    public async Task Execute_AsksConfirmation_WhenScanErrors()
    {
        AddStandardPhotos();
        var scan = await ScanAsync();
        scan = scan with { Errors = [new ScanError(@"E:\DCIM\bad.jpg", "파일을 읽지 못했습니다: boom")] };
        var vm = await CreateAsync(scan);

        Assert.Equal([@"파일을 읽지 못했습니다: E:\DCIM\bad.jpg"], vm.ScanErrors);
        Assert.Equal("오류 1건 확인 필요", vm.ScanErrorNotice);
        Assert.True(vm.ExecuteCommand.CanExecute(null));

        _dialogs.ConfirmAnswers.Enqueue(false);
        await vm.ExecuteCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Equal("확인이 필요한 오류가 있습니다", confirm.Title);
        Assert.Equal("읽지 못한 폴더나 파일 1건이 있습니다. 그래도 실행할까요?", confirm.Message);
        Assert.Empty(_runRequests);
        Assert.Same(vm, _shell.Current);

        _dialogs.ConfirmAnswers.Enqueue(true);
        await vm.ExecuteCommand.ExecuteAsync(null);

        var run = Assert.Single(_runRequests);
        Assert.Equal(1, run.ScanErrors);
        Assert.Contains(run.Plan.Operations, o => o.Destination == @"D:\Archive\2026\2026-10-14\A.JPG");
        Assert.Same(_runScreen, _shell.Current);
    }

    [Fact]
    public async Task Execute_WithoutScanErrors_NavigatesWithoutAsking()
    {
        AddStandardPhotos();
        var vm = await CreateAsync();

        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Confirms);
        var run = Assert.Single(_runRequests);
        Assert.Equal(0, run.ScanErrors);
        Assert.Same(_runScreen, _shell.Current);
    }

    [Fact]
    public async Task IgnoredFiles_AreShown_AndPassedToRun()
    {
        AddStandardPhotos();
        _fs.AddFile(@"E:\DCIM\report.pptx", [9]);
        _fs.AddFile(@"E:\DCIM\budget.xlsx", [8]);

        var vm = await CreateAsync();

        Assert.Equal(2, vm.IgnoredCount);
        Assert.Equal("가져오지 않는 파일 2개", vm.IgnoredNotice);
        Assert.Equal([@"E:\DCIM\budget.xlsx", @"E:\DCIM\report.pptx"], vm.IgnoredFiles);

        await vm.ExecuteCommand.ExecuteAsync(null);

        var run = Assert.Single(_runRequests);
        Assert.Equal(2, run.Ignored);
        Assert.Equal(0, run.ScanErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubfoldersLeftOut_IsPassedToRun(bool leftOut)
    {
        AddStandardPhotos();
        var scan = await ScanAsync() with { SubfoldersNotScanned = leftOut };
        var vm = await CreateAsync(scan);

        await vm.ExecuteCommand.ExecuteAsync(null);

        var run = Assert.Single(_runRequests);
        Assert.Equal(leftOut, run.SubfoldersNotImported);
    }

    [Fact]
    public async Task ReturnHomeAfterError_CancelsPendingThumbnails()
    {
        AddStandardPhotos();
        await CreateAsync();
        Assert.NotEmpty(_thumbnails.Tokens);
        Assert.All(_thumbnails.Tokens, t => Assert.False(t.IsCancellationRequested));
        var home = new ScreenStub();

        Assert.True(_shell.ReturnHomeAfterError(() => home));

        Assert.Same(home, _shell.Current);
        Assert.All(_thumbnails.Tokens, t => Assert.True(t.IsCancellationRequested));
    }

    [Fact]
    public async Task IgnoredFiles_None_HasNoNotice()
    {
        AddStandardPhotos();

        var vm = await CreateAsync();

        Assert.Equal(0, vm.IgnoredCount);
        Assert.Null(vm.IgnoredNotice);
        Assert.Empty(vm.IgnoredFiles);
    }

    [Fact]
    public async Task IgnoredFiles_ListIsCappedAt200_WithRemainderCount()
    {
        AddStandardPhotos();
        var scan = await ScanAsync();
        var ignored = Enumerable.Range(0, 205).Select(i => $@"E:\DCIM\doc{i:000}.pdf").ToList();
        scan = scan with { Ignored = ignored };

        var vm = await CreateAsync(scan);

        Assert.Equal(205, vm.IgnoredCount);
        Assert.Equal("가져오지 않는 파일 205개", vm.IgnoredNotice);
        Assert.Equal(201, vm.IgnoredFiles.Count);
        Assert.Equal(ignored.Take(200), vm.IgnoredFiles.Take(200));
        Assert.Equal("외 5건", vm.IgnoredFiles[^1]);
    }

    [Fact]
    public async Task Detail_ShowsWhyThisPath()
    {
        AddStandardPhotos();
        var vm = await CreateAsync();

        vm.SelectedSession = vm.Sessions[0];

        var detail = vm.Detail!;
        Assert.Equal("A.JPG", detail.FileName);
        Assert.Equal("CAM-1", detail.Device);
        Assert.Equal("2026-10-14 10:00:00", detail.CaptureTime);
        Assert.Equal(@"D:\Archive\2026\2026-10-14\A.JPG", detail.Destination);
        Assert.Equal("기본", detail.BranchName);
        Assert.Equal(@"{연도}\{날짜} {이벤트}", detail.Template);
        Assert.Empty(detail.Reasons);
        Assert.Null(detail.SkipNote);
        Assert.Contains(@"E:\DCIM\A.JPG", _thumbnails.Requests);

        vm.SelectedSession = vm.Sessions[^1];

        detail = vm.Detail!;
        Assert.Equal("D.JPG", detail.FileName);
        Assert.Equal("판단 필요", detail.BranchName);
        Assert.Equal(["촬영일 없음"], detail.Reasons);
        Assert.StartsWith(@"D:\Archive\판단 필요\촬영일 없음\", detail.Destination, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ShowsCaptureSource()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1, 1, 1], Oct14, "CAM-1");
        AddPhoto(@"E:\DCIM\photo_2026-10-17_00-32-09.jpg", [2, 2], new DateTime(2026, 10, 17, 0, 32, 9));
        _reader.Set(@"E:\DCIM\photo_2026-10-17_00-32-09.jpg", new DateTime(2026, 10, 17, 0, 32, 9),
            source: CaptureTimeSource.FileName);
        _fs.AddFile(@"E:\DCIM\D.JPG", [4, 4, 4, 4]);
        _reader.Set(@"E:\DCIM\D.JPG", null);
        var vm = await CreateAsync();

        vm.SelectedSession = vm.Sessions.Single(s => s.Files.Any(f => f.EndsWith("A.JPG", StringComparison.Ordinal)));
        Assert.Equal("(사진 정보)", vm.Detail!.CaptureSource);

        vm.SelectedSession = vm.Sessions.Single(s =>
            s.Files.Any(f => f.EndsWith("photo_2026-10-17_00-32-09.jpg", StringComparison.Ordinal)));
        Assert.Equal("2026-10-17 00:32:09", vm.Detail!.CaptureTime);
        Assert.Contains("(파일 이름)", vm.Detail.CaptureSource, StringComparison.Ordinal);

        vm.SelectedSession = vm.Sessions.Single(s => s.Files.Any(f => f.EndsWith("D.JPG", StringComparison.Ordinal)));
        Assert.Equal("촬영 시각 없음 — 파일 수정 시각으로 대체", vm.Detail!.CaptureTime);
        Assert.Equal("", vm.Detail.CaptureSource);
    }

    [Fact]
    public async Task Detail_SkippedDuplicate_SaysWhy()
    {
        AddStandardPhotos();
        var vm = await CreateAsync();

        vm.SelectedSession = vm.Sessions[1];

        var detail = vm.Detail!;
        Assert.Equal("C.JPG", detail.FileName);
        Assert.Equal(@"D:\Archive\2026\2026-10-15\C.JPG", detail.Destination);
        Assert.Equal("보관 폴더에 같은 파일이 있어 건너뜁니다", detail.SkipNote);
    }

    [Fact]
    public async Task Thumbnails_RequestedOnlyForFirst12OfSelectedSession()
    {
        for (var i = 0; i < 15; i++)
        {
            AddPhoto($@"E:\DCIM\P{i:00}.JPG", [(byte)i], Oct14.AddMinutes(i));
        }

        AddPhoto(@"E:\DCIM\Z.JPG", [99], Oct15);
        AddPhoto(@"E:\DCIM\Y.JPG", [98], Oct15.AddMinutes(1));

        var vm = await CreateAsync();
        vm.SelectedSession = vm.Sessions[0];

        Assert.Equal(12, vm.Thumbnails.Count);
        Assert.Equal(@"E:\DCIM\P00.JPG", vm.Thumbnails[0].Path);
        Assert.DoesNotContain(@"E:\DCIM\P12.JPG", _thumbnails.Requests);
        Assert.DoesNotContain(@"E:\DCIM\Y.JPG", _thumbnails.Requests); // 다른 촬영은 대표 한 장만

        vm.SelectedThumbnail = vm.Thumbnails[3];
        Assert.Equal("P03.JPG", vm.Detail!.FileName);
    }

    [Fact]
    public async Task Cards_DoNotRequestThumbnails_UntilRead()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        AddPhoto(@"E:\DCIM\C.JPG", [3], Oct15);

        var vm = await CreateAsync();

        // 선택된 촬영의 격자만 요청했다. 카드는 아직 아무도 그리지(읽지) 않았다.
        Assert.Equal([@"E:\DCIM\A.JPG"], _thumbnails.Requests);

        _ = vm.Sessions[1].Thumbnail;
        _ = vm.Sessions[1].Thumbnail;

        Assert.Equal([@"E:\DCIM\A.JPG", @"E:\DCIM\C.JPG"], _thumbnails.Requests);
    }

    [Fact]
    public async Task Grid_Thumbnails_RequestedOnSelection_WithoutCardRequests()
    {
        for (var day = 0; day < 30; day++)
        {
            AddPhoto($@"E:\DCIM\D{day:00}.JPG", [(byte)day], Oct14.AddDays(day));
        }

        AddPhoto(@"E:\DCIM\D05b.JPG", [200], Oct14.AddDays(5).AddMinutes(1));
        var vm = await CreateAsync();

        vm.SelectedSession = vm.Sessions[5];

        // 30장의 카드 썸네일이 앞에 쌓이지 않고, 격자 요청만 차례대로 나간다.
        Assert.Equal([@"E:\DCIM\D00.JPG", @"E:\DCIM\D05.JPG", @"E:\DCIM\D05b.JPG"], _thumbnails.Requests);
    }

    [Fact]
    public async Task Review_Files_NotCountedOnDateCards()
    {
        AddStandardPhotos();
        _fs.AddFile(@"E:\DCIM\B.xmp", [7]); // B.JPG에 딸린 사이드카

        var vm = await CreateAsync();

        Assert.Equal(["2026-10-14 · 3장", "2026-10-15 · 1장", "판단 필요 · 1장"], vm.Sessions.Select(c => c.Title));
        Assert.Equal(3, Node(vm, "2026", "2026-10-14").FileCount);
        Assert.DoesNotContain(vm.Sessions, c => !c.IsReviewGroup && c.Files.Contains(@"E:\DCIM\D.JPG"));
        Assert.Equal([@"E:\DCIM\D.JPG"], vm.Sessions[^1].Files);
    }

    [Fact]
    public async Task ReviewCard_IncludesDuplicateSkippedFiles_AndCountsAllFiles()
    {
        AddStandardPhotos();
        // 촬영일 없는 D.JPG(판단 필요)가 판단 필요 분기에 이미 있다 → 중복으로 건너뛰어도 판단 필요 카드에 보인다.
        _fs.AddFile(@"D:\Archive\판단 필요\촬영일 없음\DCIM\D.JPG", [4, 4, 4, 4]);
        _fs.AddFile(@"E:\DCIM\D.xmp", [7]); // D.JPG에 딸린 사이드카

        var vm = await CreateAsync();

        var review = vm.Sessions[^1];
        Assert.True(review.IsReviewGroup);
        Assert.Equal("판단 필요 · 2장", review.Title); // 대표와 딸린 파일을 모두 센다
        Assert.Equal([@"E:\DCIM\D.JPG"], review.Files);
        Assert.Single(vm.Sessions, s => s.IsReviewGroup);
        Assert.Equal(2, vm.DuplicateCount); // C.JPG와 D.JPG

        vm.SelectedSession = review;
        Assert.Equal("보관 폴더에 같은 파일이 있어 건너뜁니다", vm.Detail?.SkipNote);
    }

    [Fact]
    public async Task AllReview_Import_SelectsReviewCard_AndShowsDetail()
    {
        _fs.AddFile(@"E:\DCIM\D.JPG", [4, 4, 4, 4]);
        _reader.Set(@"E:\DCIM\D.JPG", null);

        var vm = await CreateAsync();

        var review = Assert.Single(vm.Sessions);
        Assert.True(review.IsReviewGroup);
        Assert.Same(review, vm.SelectedSession);
        Assert.Equal("D.JPG", vm.Detail?.FileName);
        Assert.Equal([@"E:\DCIM\D.JPG"], vm.Thumbnails.Select(t => t.Path));
    }

    [Fact]
    public async Task Cards_AreBuiltBeforeFirstPlan()
    {
        AddStandardPhotos();
        using var gate = new ManualResetEventSlim();
        var scan = await ScanAsync();

        var vm = new PreviewViewModel(_shell, _workflow, _settings, scan, _fs, _thumbnails, _dialogs, new FileLog(_fs, LogFolder),
            createRun: (_, _) => _runScreen, createBack: () => _backScreen, replanDelay: TimeSpan.Zero,
            buildPlan: (settings, s, names) =>
            {
                gate.Wait(Wait);
                return _workflow.BuildPlan(settings, s, names);
            });

        Assert.Equal(["2026-10-14 · 2장", "2026-10-15 · 1장", "판단 필요 · 1장"], vm.Sessions.Select(c => c.Title));
        Assert.Same(vm.Sessions[0], vm.SelectedSession);

        gate.Set();
        await vm.WhenPlanned;
        Assert.Equal(3, vm.Sessions.Count);
    }

    [Fact]
    public async Task FreeSpace_Unknown_ShowsText()
    {
        AddStandardPhotos();
        var hooked = new HookedFileSystem(_fs) { FreeSpace = _ => throw new IOException("드라이브 없음") };

        var vm = await CreateAsync(fileSystem: hooked);

        Assert.Equal(0, vm.FreeBytes);
        Assert.Equal("알 수 없음", vm.FreeSpaceText);
        Assert.Null(vm.BlockingReason); // 계획 쪽 점검은 진짜(가짜) 파일 시스템으로 했다
    }
}
