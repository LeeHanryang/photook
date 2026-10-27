using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Execution;
using PhotoOk.Core.History;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class StartViewModelTests
{
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string Archive = @"D:\Archive";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeDrives _drives = new();
    private readonly ShellViewModel _shell = new(new FakeDialogService());
    private readonly List<string?> _requestedRoots = [];
    private readonly ObservableObject _sourceScreen = new SourceStub();

    private sealed class SourceStub : ObservableObject;

    private readonly ObservableObject _historyScreen = new SourceStub();
    private readonly ObservableObject _resumeScreen = new SourceStub();
    private readonly List<string?> _requestedHistorySelections = [];
    private readonly List<string> _requestedResumes = [];

    private StartViewModel Create() => new(_shell, _drives, new RunHistory(_fs, AppData),
        createSource: root =>
        {
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
    public void Start_ShowsDrivesAndUnfinishedRun()
    {
        _drives.Choices.Add(new DriveChoice(@"E:\", "EOS_DIGITAL"));
        _drives.Choices.Add(new DriveChoice(@"F:\", "이동식 디스크"));
        WriteJournal("20261026-100000-aaaa", finished: true);
        WriteJournal("20261027-090000-bbbb", finished: false);

        var start = Create();

        Assert.Equal([@"E:\", @"F:\"], start.Drives.Select(d => d.Root));
        Assert.Equal(["20261027-090000-bbbb", "20261026-100000-aaaa"], start.RecentRuns.Select(r => r.RunId));
        Assert.NotNull(start.UnfinishedRun);
        Assert.Equal("20261027-090000-bbbb", start.UnfinishedRun!.RunId);
        Assert.Equal(RunStatus.Unfinished, start.UnfinishedRun.Status);
    }

    [Fact]
    public void Start_WithoutRuns_HasNoUnfinishedRun()
    {
        WriteJournal("20261026-100000-aaaa", finished: true);

        var start = Create();

        Assert.Single(start.RecentRuns);
        Assert.Null(start.UnfinishedRun);
        Assert.Empty(start.Drives);
    }

    [Fact]
    public void RecentRuns_AreLimitedToFive_NewestFirst()
    {
        for (var day = 1; day <= 7; day++)
        {
            WriteJournal($"2026100{day}-100000-aaaa", finished: true);
        }

        var start = Create();

        Assert.Equal(5, start.RecentRuns.Count);
        Assert.Equal("20261007-100000-aaaa", start.RecentRuns[0].RunId);
        Assert.Equal("20261003-100000-aaaa", start.RecentRuns[4].RunId);
    }

    [Fact]
    public void Refresh_ReadsDrivesAndRunsAgain()
    {
        var start = Create();
        Assert.Empty(start.Drives);
        Assert.Null(start.UnfinishedRun);

        _drives.Choices.Add(new DriveChoice(@"E:\", "EOS_DIGITAL"));
        WriteJournal("20261027-090000-bbbb", finished: false);
        start.Refresh();

        Assert.Single(start.Drives);
        Assert.Single(start.RecentRuns);
        Assert.NotNull(start.UnfinishedRun);

        // 이전 목록을 쌓지 않고 바꾼다.
        start.Refresh();
        Assert.Single(start.Drives);
        Assert.Single(start.RecentRuns);
    }

    [Fact]
    public void StartImport_NavigatesToSourceScreen_WithSavedSettings()
    {
        var start = Create();

        start.StartImportCommand.Execute(null);

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal([(string?)null], _requestedRoots);
    }

    [Fact]
    public void ImportFromDrive_PrefillsSource()
    {
        var drive = new DriveChoice(@"E:\", "EOS_DIGITAL");
        _drives.Choices.Add(drive);
        var start = Create();

        start.ImportFromDriveCommand.Execute(drive);

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal([@"E:\"], _requestedRoots);
    }

    [Fact]
    public void OpenHistory_NavigatesToHistoryScreen_WithoutSelection()
    {
        var start = Create();

        start.OpenHistoryCommand.Execute(null);

        Assert.Same(_historyScreen, _shell.Current);
        Assert.Equal([(string?)null], _requestedHistorySelections);
    }

    [Fact]
    public void UnfinishedBanner_ResumeGoesToRun_UndoGoesToHistoryWithThatRunSelected()
    {
        WriteJournal("20261027-090000-bbbb", finished: false);
        var start = Create();
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
    public void UnfinishedBanner_Commands_AreDisabledWithoutUnfinishedRun()
    {
        var start = Create();

        Assert.False(start.ResumeUnfinishedCommand.CanExecute(null));
        Assert.False(start.UndoUnfinishedCommand.CanExecute(null));
    }

    [Fact]
    public void UnfinishedBanner_ResumeOnlyForUnfinished_NotForUndoInProgress()
    {
        var runId = "20261027-090000-cccc";
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\DCIM\a.jpg", @"D:\Archive\a.jpg", 5,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Completed, "op1", TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.UndoStarted, TimestampUtc: Now));

        var start = Create();

        Assert.Equal(RunStatus.UndoInProgress, start.UnfinishedRun!.Status);
        Assert.False(start.ResumeUnfinishedCommand.CanExecute(null));
        Assert.True(start.UndoUnfinishedCommand.CanExecute(null));
    }
}
