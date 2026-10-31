using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Execution;
using PhotoOk.Core.History;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class StartViewModelTests
{
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string Archive = @"D:\Archive";
    private const string LogFolder = @"L:\PhotoOk\logs";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeDrives _drives = new();
    private readonly ShellViewModel _shell = new(new FakeDialogService());
    private readonly List<string?> _requestedRoots = [];
    private readonly List<JobKind> _requestedKinds = [];
    private readonly ObservableObject _sourceScreen = new SourceStub();

    private sealed class SourceStub : ObservableObject;

    private readonly ObservableObject _historyScreen = new SourceStub();
    private readonly ObservableObject _resumeScreen = new SourceStub();
    private readonly List<string?> _requestedHistorySelections = [];
    private readonly List<string> _requestedResumes = [];

    private StartViewModel Create(IRunHistory? history = null) => new(_shell, _drives, history ?? new RunHistory(_fs, AppData),
        new FileLog(_fs, LogFolder),
        createSource: (kind, root) =>
        {
            _requestedKinds.Add(kind);
            _requestedRoots.Add(root);
            return _sourceScreen;
        },
        createHistory: runId =>
        {
            _requestedHistorySelections.Add(runId);
            return _historyScreen;
        },
        createResume: journalPath =>
        {
            _requestedResumes.Add(journalPath);
            return _resumeScreen;
        });

    private async Task<StartViewModel> CreateAsync(IRunHistory? history = null)
    {
        var start = Create(history);
        await start.WhenLoaded;
        return start;
    }

    private string LogText() => string.Concat(_fs.AllFiles()
        .Where(p => p.StartsWith(LogFolder, StringComparison.OrdinalIgnoreCase))
        .Select(p => System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(p))));

    private void WriteJournal(string runId, bool finished)
    {
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 0, Destination: Archive, TimestampUtc: Now));
        if (finished)
        {
            writer.Write(new JournalEntry(runId, JournalEvent.RunFinished, TimestampUtc: Now));
        }
    }

    [Fact]
    public async Task Start_ShowsDrivesAndUnfinishedRun()
    {
        _drives.Choices.Add(new DriveChoice(@"E:\", "EOS_DIGITAL"));
        _drives.Choices.Add(new DriveChoice(@"F:\", "이동식 디스크"));
        WriteJournal("20261026-100000-aaaa", finished: true);
        WriteJournal("20261027-090000-bbbb", finished: false);

        var start = await CreateAsync();

        Assert.Equal([@"E:\", @"F:\"], start.Drives.Select(d => d.Root));
        Assert.Equal(["20261027-090000-bbbb", "20261026-100000-aaaa"], start.RecentRuns.Select(r => r.RunId));
        Assert.NotNull(start.UnfinishedRun);
        Assert.Equal("20261027-090000-bbbb", start.UnfinishedRun!.RunId);
        Assert.Equal(RunStatus.Unfinished, start.UnfinishedRun.Status);
    }

    [Fact]
    public async Task Start_WithoutRuns_HasNoUnfinishedRun()
    {
        WriteJournal("20261026-100000-aaaa", finished: true);

        var start = await CreateAsync();

        Assert.Single(start.RecentRuns);
        Assert.Null(start.UnfinishedRun);
        Assert.Empty(start.Drives);
    }

    [Fact]
    public async Task RecentRuns_AreLimitedToFive_NewestFirst()
    {
        for (var day = 1; day <= 7; day++)
        {
            WriteJournal($"2026100{day}-100000-aaaa", finished: true);
        }

        var start = await CreateAsync();

        Assert.Equal(5, start.RecentRuns.Count);
        Assert.Equal("20261007-100000-aaaa", start.RecentRuns[0].RunId);
        Assert.Equal("20261003-100000-aaaa", start.RecentRuns[4].RunId);
    }

    [Fact]
    public async Task Refresh_ReadsDrivesAndRunsAgain()
    {
        var start = await CreateAsync();
        Assert.Empty(start.Drives);
        Assert.Null(start.UnfinishedRun);

        _drives.Choices.Add(new DriveChoice(@"E:\", "EOS_DIGITAL"));
        WriteJournal("20261027-090000-bbbb", finished: false);
        await start.RefreshAsync();

        Assert.Single(start.Drives);
        Assert.Single(start.RecentRuns);
        Assert.NotNull(start.UnfinishedRun);

        // 이전 목록을 쌓지 않고 바꾼다.
        await start.RefreshAsync();
        Assert.Single(start.Drives);
        Assert.Single(start.RecentRuns);
    }

    [Fact]
    public async Task StartImport_NavigatesToSourceScreen_WithSavedSettings()
    {
        var start = await CreateAsync();

        start.StartImportCommand.Execute(null);

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal([(string?)null], _requestedRoots);
        Assert.Equal([JobKind.Import], _requestedKinds);
    }

    [Fact]
    public async Task StartAbsorb_NavigatesWithKind()
    {
        var start = await CreateAsync();

        start.StartAbsorbCommand.Execute(null);

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal([JobKind.Absorb], _requestedKinds);
        Assert.Equal([(string?)null], _requestedRoots);
    }

    [Fact]
    public async Task StartReorganize_NavigatesWithKind()
    {
        var start = await CreateAsync();

        start.StartReorganizeCommand.Execute(null);

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal([JobKind.Reorganize], _requestedKinds);
        Assert.Equal([(string?)null], _requestedRoots);
    }

    [Fact]
    public async Task ImportFromDrive_PrefillsSource()
    {
        var drive = new DriveChoice(@"E:\", "EOS_DIGITAL");
        _drives.Choices.Add(drive);
        var start = await CreateAsync();

        start.ImportFromDriveCommand.Execute(drive);

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal([@"E:\"], _requestedRoots);
        Assert.Equal([JobKind.Import], _requestedKinds);
    }

    [Fact]
    public async Task OpenHistory_NavigatesToHistoryScreen_WithoutSelection()
    {
        var start = await CreateAsync();

        start.OpenHistoryCommand.Execute(null);

        Assert.Same(_historyScreen, _shell.Current);
        Assert.Equal([(string?)null], _requestedHistorySelections);
    }

    [Fact]
    public async Task UnfinishedBanner_ResumeGoesToRun_UndoGoesToHistoryWithThatRunSelected()
    {
        WriteJournal("20261027-090000-bbbb", finished: false);
        var start = await CreateAsync();
        Assert.True(start.ResumeUnfinishedCommand.CanExecute(null));
        Assert.True(start.UndoUnfinishedCommand.CanExecute(null));

        start.UndoUnfinishedCommand.Execute(null);
        Assert.Same(_historyScreen, _shell.Current);
        Assert.Equal(["20261027-090000-bbbb"], _requestedHistorySelections);

        start.ResumeUnfinishedCommand.Execute(null);
        Assert.Same(_resumeScreen, _shell.Current);
        Assert.Equal([start.UnfinishedRun!.JournalPath], _requestedResumes);
    }

    [Fact]
    public async Task UnfinishedBanner_Commands_AreDisabledWithoutUnfinishedRun()
    {
        var start = await CreateAsync();

        Assert.False(start.ResumeUnfinishedCommand.CanExecute(null));
        Assert.False(start.UndoUnfinishedCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnfinishedBanner_ResumeOnlyForUnfinished_NotForUndoInProgress()
    {
        var runId = "20261027-090000-cccc";
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\DCIM\a.jpg", @"D:\Archive\a.jpg", 5,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Completed, "op1", TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.UndoStarted, TimestampUtc: Now));

        var start = await CreateAsync();

        Assert.Equal(RunStatus.UndoInProgress, start.UnfinishedRun!.Status);
        Assert.False(start.ResumeUnfinishedCommand.CanExecute(null));
        Assert.True(start.UndoUnfinishedCommand.CanExecute(null));
    }

    [Fact]
    public async Task History_IsReadInBackground_WithLoadingNotice()
    {
        var history = new FakeRunHistory { Gate = new ManualResetEventSlim() };
        history.Runs.Add(new RunSummary("20261027-090000-bbbb", @"C:\j.jsonl", Archive, new DateTime(2026, 10, 27, 9, 0, 0),
            RunStatus.Unfinished, 1, 0, 0, 0, CanResume: true, CanUndo: true));
        _drives.Choices.Add(new DriveChoice(@"E:\", "EOS_DIGITAL"));
        var uiThread = Environment.CurrentManagedThreadId;

        // 기록 읽기가 막혀 있어도 생성자는 바로 돌아온다(화면이 멈추지 않는다).
        var start = Create(history);

        Assert.True(history.Started.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(start.IsLoadingHistory);
        Assert.Equal("작업 기록을 읽는 중…", start.HistoryNotice);
        Assert.Single(start.Drives);
        Assert.Empty(start.RecentRuns);
        Assert.Null(start.UnfinishedRun);

        history.Gate.Set();
        await start.WhenLoaded;

        Assert.DoesNotContain(uiThread, history.CallingThreads);
        Assert.False(start.IsLoadingHistory);
        Assert.Null(start.HistoryNotice);
        Assert.Single(start.RecentRuns);
        Assert.Equal("20261027-090000-bbbb", start.UnfinishedRun?.RunId);
    }

    [Fact]
    public async Task HistoryNotice_SaysEmpty_WhenNoRuns()
    {
        var start = await CreateAsync();

        Assert.Equal("아직 작업 기록이 없습니다.", start.HistoryNotice);
    }

    [Fact]
    public async Task History_Failure_DoesNotEscape_ShowsEmptyList_AndLogs()
    {
        var history = new FakeRunHistory { Failure = new ArgumentException("bad archive root") };
        _drives.Choices.Add(new DriveChoice(@"E:\", "EOS_DIGITAL"));

        var start = await CreateAsync(history);

        Assert.Empty(start.RecentRuns);
        Assert.Null(start.UnfinishedRun);
        Assert.False(start.IsLoadingHistory);
        Assert.Equal("아직 작업 기록이 없습니다.", start.HistoryNotice);
        Assert.Single(start.Drives);
        Assert.Contains("작업 기록을 읽지 못했습니다", LogText(), StringComparison.Ordinal);
        Assert.Contains("bad archive root", LogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drives_Failure_DoesNotEscape_ShowsEmptyList_AndLogs()
    {
        _drives.Failure = new UnauthorizedAccessException("drive denied");
        WriteJournal("20261026-100000-aaaa", finished: true);

        var start = await CreateAsync();

        Assert.Empty(start.Drives);
        Assert.Single(start.RecentRuns);
        Assert.Contains("이동식 드라이브 목록을 읽지 못했습니다", LogText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_LatestRequestWins()
    {
        var history = new FakeRunHistory { Gate = new ManualResetEventSlim() };
        var start = Create(history);
        var first = start.WhenLoaded;
        Assert.True(history.Started.Wait(TimeSpan.FromSeconds(10)));

        history.Runs.Add(new RunSummary("20261027-090000-bbbb", @"C:\j.jsonl", Archive, new DateTime(2026, 10, 27, 9, 0, 0),
            RunStatus.Completed, 1, 1, 0, 0, CanResume: false, CanUndo: true));
        var second = start.RefreshAsync();
        history.Gate.Set();
        await Task.WhenAll(first, second);

        Assert.Single(start.RecentRuns);
        Assert.False(start.IsLoadingHistory);
    }

    [Fact]
    public async Task LeavingScreen_CancelsHistoryRead()
    {
        var history = new FakeRunHistory { Gate = new ManualResetEventSlim() };
        var start = Create(history);
        _shell.Navigate(start);
        Assert.True(history.Started.Wait(TimeSpan.FromSeconds(10)));

        start.OpenHistoryCommand.Execute(null);
        await start.WhenLoaded.WaitAsync(TimeSpan.FromSeconds(10)); // 게이트를 열지 않아도 끝난다

        Assert.Same(_historyScreen, _shell.Current);
        Assert.Equal(1, history.Cancelled);
        Assert.Empty(start.RecentRuns);
        Assert.DoesNotContain("작업 기록을 읽지 못했습니다", LogText(), StringComparison.Ordinal);
    }
}
