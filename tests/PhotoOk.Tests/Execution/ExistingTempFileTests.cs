using System.IO;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.Execution;

/// <summary>복사 대상 자리에 이번 실행이 만들지 않은 임시 이름 파일이 있으면 되돌림 폴더로 옮기고 결과에 알린다(숨기지 않는다).</summary>
public class ExistingTempFileTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string RunId = "20261027-103000-ab12";
    private const string Destination = @"D:\Archive\2026\a.jpg";
    private const string Temp = Destination + ".photook-tmp";
    private const string Warning = "임시 이름 파일을 되돌림 폴더로 옮김: " + Temp;
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly HookedFileSystem _hooked;
    private readonly byte[] _source;
    private readonly PlannedOperation _copy;

    public ExistingTempFileTests()
    {
        _hooked = new HookedFileSystem(_fs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
        _fs.AddDirectory(@"D:\Archive\2026");
        _source = Bytes(500, 1);
        _fs.AddFile(@"E:\DCIM\a.jpg", _source);
        _copy = new PlannedOperation("op1", OperationKind.Copy, @"E:\DCIM\a.jpg", Destination, _source.Length, []);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private Executor NewExecutor() => new(new ExecutionContext(_hooked, new XxHasher(_hooked), AppData, () => Now));

    private byte[] AddExistingTemp()
    {
        var content = Bytes(120, 2);
        _fs.AddFile(Temp, content);
        return content;
    }

    private string WriteJournal(params JournalEvent[] events)
    {
        var path = JournalPaths.InArchive(Archive, RunId);
        var writer = new JournalWriter(_fs, [path, JournalPaths.InAppData(AppData, RunId)]);
        writer.Write(new JournalEntry(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(RunId, JournalEvent.Planned, _copy.Id, _copy.Kind, _copy.Source, _copy.Destination, _copy.Length, TimestampUtc: Now));
        foreach (var ev in events)
        {
            writer.Write(new JournalEntry(RunId, ev, _copy.Id, _copy.Kind, TimestampUtc: Now));
        }

        return path;
    }

    /// <summary>실행이 Started를 쓰기 전에 있던 임시 파일을 알아채 경고를 남기고, Started를 쓴 직후 끊긴 기록.</summary>
    private string WriteJournalWithWarningBeforeStarted()
    {
        var path = JournalPaths.InArchive(Archive, RunId);
        var writer = new JournalWriter(_fs, [path, JournalPaths.InAppData(AppData, RunId)]);
        writer.Write(new JournalEntry(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(RunId, JournalEvent.Planned, _copy.Id, _copy.Kind, _copy.Source, _copy.Destination, _copy.Length, TimestampUtc: Now));
        writer.Write(new JournalEntry(RunId, JournalEvent.Warning, Message: Warning, TimestampUtc: Now));
        writer.Write(new JournalEntry(RunId, JournalEvent.Started, _copy.Id, _copy.Kind, TimestampUtc: Now));
        return path;
    }

    private List<JournalEntry> Entries(string path) =>
        System.Text.Encoding.UTF8.GetString(_fs.ReadAllBytes(path))
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<JournalEntry>(line, JournalWriter.JsonOptions)!)
            .ToList();

    private Undoer NewUndoer() => new(new ExecutionContext(_hooked, new XxHasher(_hooked), AppData, () => Now));

    private string Quarantined(string runId) => new SafeDelete(_fs, Archive, runId).QuarantineRoot + @"\2026\a.jpg.photook-tmp";

    [Fact]
    public void Execute_ExistingTemp_MovedAside_AndWarned()
    {
        var existing = AddExistingTemp();

        var result = NewExecutor().Execute(new Plan(Archive, [_copy], [], []));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(result.RunId)));
        Assert.Equal(_source, _fs.ReadAllBytes(Destination));
        Assert.False(_fs.FileExists(Temp));
    }

    [Fact]
    public void Execute_NoExistingTemp_NoWarning()
    {
        var result = NewExecutor().Execute(new Plan(Archive, [_copy], [], []));

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Execute_DestinationTaken_LeavesExistingTempAlone()
    {
        var existing = AddExistingTemp();
        _fs.AddFile(Destination, Bytes(30, 3));

        var result = NewExecutor().Execute(new Plan(Archive, [_copy], [], []));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Temp));
    }

    [Fact]
    public void Execute_QuarantineFails_FailsTheOperation_WithoutClaimingTheMove()
    {
        var existing = AddExistingTemp();
        _hooked.BeforeMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("옮길 수 없음");
            }
        };

        var result = NewExecutor().Execute(new Plan(Archive, [_copy], [], []));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Temp));
        Assert.False(_fs.FileExists(Destination));
    }

    [Fact]
    public void Warning_IsJournaledBeforeTheMove_SoACrashCannotLoseIt()
    {
        var existing = AddExistingTemp();
        _hooked.BeforeMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException();
            }
        };
        Assert.Throws<SimulatedCrashException>(() => NewExecutor().Execute(new Plan(Archive, [_copy], [], [])));
        _hooked.BeforeMove = null;
        var journal = _fs.AllFiles().First(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        var runId = JournalReader.Load(_fs, journal).RunId;

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(runId)));
    }

    [Fact]
    public void Warning_SurvivesCrashAfterTheMove()
    {
        AddExistingTemp();
        var moves = 0;
        _hooked.AfterMove = (source, _) =>
        {
            // 첫 이동은 있던 파일을 치우는 것이고, 끊기는 것은 복사한 임시 파일에 이름을 붙이는 때다.
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase) && ++moves == 2)
            {
                throw new SimulatedCrashException();
            }
        };
        Assert.Throws<SimulatedCrashException>(() => NewExecutor().Execute(new Plan(Archive, [_copy], [], [])));
        _hooked.AfterMove = null;
        var journal = _fs.AllFiles().First(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
    }

    [Fact]
    public void Warning_IsWrittenBeforeStarted_AndOnlyOnce()
    {
        AddExistingTemp();

        var result = NewExecutor().Execute(new Plan(Archive, [_copy], [], []));

        var entries = Entries(result.JournalPath!);
        var warnings = entries.FindAll(e => e.Event == JournalEvent.Warning);
        Assert.Equal(Warning, Assert.Single(warnings).Message);
        Assert.True(entries.IndexOf(warnings[0]) < entries.FindIndex(e => e.Event == JournalEvent.Started));
    }

    [Fact]
    public void DestinationTaken_NothingToMove_NothingWrittenAboutTheTemp()
    {
        AddExistingTemp();
        _fs.AddFile(Destination, Bytes(30, 3));

        var result = NewExecutor().Execute(new Plan(Archive, [_copy], [], []));

        Assert.DoesNotContain(Entries(result.JournalPath!), e => e.Event == JournalEvent.Warning);
    }

    [Fact]
    public void Resume_CrashRightAfterStarted_ExistingTempIsStillReported()
    {
        var existing = AddExistingTemp();
        var journal = WriteJournalWithWarningBeforeStarted();

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(RunId)));
    }

    [Fact]
    public void Undo_CrashRightAfterStarted_ExistingTempIsReported()
    {
        var existing = AddExistingTemp();
        var journal = WriteJournalWithWarningBeforeStarted();

        var result = NewUndoer().Undo(journal);

        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(RunId)));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Undo_OwnLeftover_StaysSilent()
    {
        var partial = _source[..100];
        _fs.AddFile(Temp, partial);
        var journal = WriteJournal(JournalEvent.Started);

        var result = NewUndoer().Undo(journal);

        Assert.Empty(result.Warnings);
        Assert.Equal(partial, _fs.ReadAllBytes(Quarantined(RunId)));
    }

    [Fact]
    public void Undo_DestinationPresent_TempIsNotALeftover_Warns_AndRecords()
    {
        var existing = AddExistingTemp();
        _fs.AddFile(Destination, Bytes(30, 3)); // 복사는 임시 파일을 만들기 전에 실패했다
        var journal = WriteJournal(JournalEvent.Started, JournalEvent.Failed);

        var result = NewUndoer().Undo(journal);

        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(RunId)));
        Assert.Contains(Entries(journal), e => e.Event == JournalEvent.Warning && e.Message == Warning);
    }

    [Fact]
    public void Resume_PendingCopy_ExistingTempIsNotThisRuns_Warns()
    {
        var existing = AddExistingTemp();
        var journal = WriteJournal();

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(RunId)));
    }

    [Fact]
    public void Resume_StartedCopy_OwnLeftover_StaysSilent()
    {
        var partial = _source[..100];
        _fs.AddFile(Temp, partial);
        var journal = WriteJournal(JournalEvent.Started);

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.Equal(partial, _fs.ReadAllBytes(Quarantined(RunId)));
    }

    [Fact]
    public void Resume_FailedCopy_DriveWasPulled_OwnLeftover_StaysSilent()
    {
        var partial = _source[..100];
        _fs.AddFile(Temp, partial);
        var journal = WriteJournal(JournalEvent.Started, JournalEvent.Failed);

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Resume_DestinationTakenByOtherFile_TempIsNotALeftover_Warns()
    {
        // 앞선 복사는 대상이 있어 임시 파일을 만들기 전에 실패했다.
        var existing = AddExistingTemp();
        _fs.AddFile(Destination, Bytes(30, 3));
        var journal = WriteJournal(JournalEvent.Started, JournalEvent.Failed);

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(RunId)));
    }

    [Fact]
    public void Resume_DestinationAlreadyComplete_TempIsNotALeftover_Warns()
    {
        // 임시 파일은 이름을 붙이면 없어지므로, 복사가 끝났는데 남아 있다면 이번 실행의 것이 아니다.
        var existing = AddExistingTemp();
        _fs.AddFile(Destination, _source);
        var journal = WriteJournal(JournalEvent.Started);

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([Warning], result.Warnings);
        Assert.Equal(existing, _fs.ReadAllBytes(Quarantined(RunId)));
    }
}
