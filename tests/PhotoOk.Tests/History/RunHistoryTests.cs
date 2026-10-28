using System.IO;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.History;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.History;

public class RunHistoryTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string Unreadable = "기록을 읽을 수 없습니다";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();

    public RunHistoryTests()
    {
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
    }

    private ExecutionContext Context() => new(_fs, new XxHasher(_fs), AppData, () => Now);

    private RunHistory History() => new(_fs, AppData);

    private static PlannedOperation Copy(string id, string source, string destination, long length) =>
        new(id, OperationKind.Copy, source, destination, length, Array.Empty<ReviewReason>());

    private static PlannedOperation Mkdir(string id, string destination) =>
        new(id, OperationKind.CreateDirectory, null, destination, 0, Array.Empty<ReviewReason>());

    private static Plan PlanOf(params PlannedOperation[] ops) =>
        new(Archive, ops, Array.Empty<PlannedSkip>(), Array.Empty<string>());

    private void AddFile(string path, int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        _fs.AddFile(path, data);
    }

    private void AddSources()
    {
        AddFile(@"E:\DCIM\a.jpg", 100, 1);
        AddFile(@"E:\DCIM\b.jpg", 100, 2);
    }

    private ExecutionResult Execute(CancellationToken ct = default) =>
        new Executor(Context()).Execute(
            PlanOf(
                Mkdir("op0", @"D:\Archive\2026"),
                Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", 100),
                Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", 100)),
            ct);

    private RunSummary Single()
    {
        var (runs, errors) = History().List();
        Assert.Empty(errors);
        return Assert.Single(runs);
    }

    private void WriteMinimalJournal(string runId, bool finished)
    {
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 0, Destination: Archive, TimestampUtc: Now));
        if (finished)
        {
            writer.Write(new JournalEntry(runId, JournalEvent.RunFinished, TimestampUtc: Now));
        }
    }

    [Fact]
    public void Status_Completed()
    {
        AddSources();
        var result = Execute();
        Assert.Equal(RunOutcome.Completed, result.Outcome);

        var run = Single();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(result.RunId, run.RunId);
        Assert.Equal(Archive, run.ArchiveRoot);
        Assert.Equal(2, run.FileOperations);
        Assert.Equal(2, run.CompletedFiles);
        Assert.Equal(0, run.SkippedFiles);
        Assert.Equal(0, run.FailedFiles);
        Assert.False(run.CanResume);
        Assert.True(run.CanUndo);
    }

    [Fact]
    public void SkippedFiles_AreCountedSeparately()
    {
        const string runId = "20261027-103000-ab12";
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 3, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op0", OperationKind.CreateDirectory, null, @"D:\Archive\2026", 0,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\a.jpg", @"D:\Archive\2026\a.jpg", 5,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op2", OperationKind.Copy, @"E:\b.jpg", @"D:\Archive\2026\b.jpg", 5,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Skipped, "op0", TimestampUtc: Now)); // 폴더는 세지 않는다
        writer.Write(new JournalEntry(runId, JournalEvent.Skipped, "op1", TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Completed, "op2", TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.RunFinished, TimestampUtc: Now));

        var run = Single();
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(2, run.FileOperations);
        Assert.Equal(1, run.CompletedFiles);
        Assert.Equal(1, run.SkippedFiles);
        Assert.Equal(0, run.FailedFiles);
    }

    [Fact]
    public void Status_Unfinished_WithStartedOperation()
    {
        const string runId = "20261027-103000-ab12";
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\a.jpg", @"D:\Archive\a.jpg", 5,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Started, "op1", TimestampUtc: Now)); // 파일 도중에 꺼졌다

        var run = Single();

        Assert.Equal(RunStatus.Unfinished, run.Status);
        Assert.Equal(0, run.CompletedFiles);
        Assert.True(run.CanResume);
        Assert.True(run.CanUndo);
    }

    [Fact]
    public void Status_Undone_WhenRemainingOperationsWereUndoSkipped()
    {
        const string runId = "20261027-103000-ab12";
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\a.jpg", @"D:\Archive\a.jpg", 5,
            TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Completed, "op1", TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.RunFinished, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.UndoStarted, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.UndoSkipped, "op1", Message: "원본이 없어 복사본을 남김", TimestampUtc: Now));

        var run = Single();

        Assert.Equal(RunStatus.Undone, run.Status);
        Assert.False(run.CanResume);
        Assert.False(run.CanUndo);
    }

    [Fact]
    public void Status_CompletedWithFailures()
    {
        AddSources();
        _fs.FailReadsOf(@"E:\DCIM\a.jpg");
        var result = Execute();
        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);

        var run = Single();

        Assert.Equal(RunStatus.CompletedWithFailures, run.Status);
        Assert.Equal(2, run.FileOperations);
        Assert.Equal(1, run.CompletedFiles);
        Assert.Equal(1, run.FailedFiles);
        Assert.False(run.CanResume);
        Assert.True(run.CanUndo);
    }

    [Fact]
    public void Status_Unfinished_WhenCancelled()
    {
        AddSources();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = Execute(cts.Token);
        Assert.Equal(RunOutcome.Cancelled, result.Outcome);

        var run = Single();

        Assert.Equal(RunStatus.Unfinished, run.Status);
        Assert.Equal(2, run.FileOperations);
        Assert.Equal(0, run.CompletedFiles);
        Assert.True(run.CanResume);
        Assert.True(run.CanUndo);
    }

    [Fact]
    public void Status_PlanIncomplete_WhenPlanWasCutShort()
    {
        const string runId = "20261027-103000-ab12";
        var writer = new JournalWriter(_fs, [JournalPaths.InAppData(AppData, runId)]);
        writer.Write(new JournalEntry(runId, JournalEvent.RunStarted, Length: 3, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(runId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\a.jpg", @"D:\Archive\a.jpg", 5,
            TimestampUtc: Now));

        var run = Single();

        Assert.Equal(RunStatus.PlanIncomplete, run.Status);
        Assert.False(run.CanResume);
        Assert.False(run.CanUndo);
    }

    [Fact]
    public void Status_UndoInProgress_ThenUndone()
    {
        AddSources();
        var result = Execute();
        var journal = result.JournalPath!;

        // 원본 드라이브를 뺀 채 되돌리면 일시적 사유로 아무것도 못 하고 UndoStarted만 남는다.
        _fs.RemoveVolume(@"E:\");
        new Undoer(Context()).Undo(journal);
        _fs.RestoreVolume(@"E:\");
        var inProgress = Single();
        Assert.Equal(RunStatus.UndoInProgress, inProgress.Status);
        Assert.False(inProgress.CanResume);
        Assert.True(inProgress.CanUndo);

        new Undoer(Context()).Undo(journal);
        var undone = Single();
        Assert.Equal(RunStatus.Undone, undone.Status);
        Assert.False(undone.CanResume);
        Assert.False(undone.CanUndo);
    }

    [Fact]
    public void List_IsNewestFirst_AndReportsUnreadableJournal()
    {
        foreach (var runId in new[] { "20261026-090000-0001", "20261027-120000-0002", "20261025-080000-0003" })
        {
            WriteMinimalJournal(runId, finished: true);
        }

        var broken = Path.Combine(AppData, "journal", "20261027-130000-0004.jsonl");
        _fs.AddFile(broken, "{\"runId\":\"x\"}\n{\"nope\": }\n{\"runId\":\"x\"}\n"u8.ToArray());
        var badName = Path.Combine(AppData, "journal", "notarun.jsonl");
        WriteMinimalJournal("notarun", finished: true);
        _fs.AddFile(Path.Combine(AppData, "journal", "readme.txt"), "x"u8.ToArray());
        _fs.AddFile(Path.Combine(AppData, "journal", "sub", "20261001-000000-0005.jsonl"), "{}"u8.ToArray());

        var (runs, errors) = History().List();

        Assert.Equal(["20261027-120000-0002", "20261026-090000-0001", "20261025-080000-0003"], runs.Select(r => r.RunId));
        Assert.Equal(new DateTime(2026, 10, 27, 12, 0, 0), runs[0].StartedLocal);
        Assert.All(runs, r => Assert.Equal(RunStatus.Completed, r.Status));
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.JournalPath == broken && e.Message == Unreadable);
        Assert.Contains(errors, e => e.JournalPath == badName && e.Message == Unreadable);
    }

    [Fact]
    public void List_IsEmpty_WhenJournalFolderIsMissing()
    {
        var (runs, errors) = History().List();

        Assert.Empty(runs);
        Assert.Empty(errors);
    }

    [Fact]
    public void List_ReportsFolderThatCannotBeListed()
    {
        var folder = Path.Combine(AppData, "journal");
        _fs.AddDirectory(folder);
        _fs.DenyDirectory(folder);

        var (runs, errors) = History().List();

        Assert.Empty(runs);
        var error = Assert.Single(errors);
        Assert.Equal(folder, error.JournalPath);
        Assert.Equal(Unreadable, error.Message);
    }

    [Fact]
    public void List_UsesTheLongerCopy_WhenAppDataCopyIsBehind()
    {
        const string runId = "20261027-103000-ab12";
        var archiveCopy = JournalPaths.InArchive(Archive, runId);
        var appCopy = JournalPaths.InAppData(AppData, runId);
        var started = new JournalEntry(runId, JournalEvent.RunStarted, Length: 0, Destination: Archive, TimestampUtc: Now);
        new JournalWriter(_fs, [appCopy]).Write(started);
        var full = new JournalWriter(_fs, [archiveCopy]);
        full.Write(started);
        full.Write(new JournalEntry(runId, JournalEvent.RunFinished, TimestampUtc: Now));

        var run = Single();

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(archiveCopy, run.JournalPath);
    }
}
