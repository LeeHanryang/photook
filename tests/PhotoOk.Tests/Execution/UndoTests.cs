using System.IO;
using System.Text;
using System.Text.Json;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.Execution;

public class UndoTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string RunId = "20261027-103000-ab12";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private const string KeptNoSource = "원본이 없어 복사본을 유지합니다";
    private const string KeptModified = "실행 후 바뀐 파일이라 그대로 둡니다";
    private const string NoCopy = "복사본이 없습니다";
    private const string SpotTaken = "원래 자리에 다른 파일이 있습니다";
    private const string MovedMissing = "옮긴 파일을 찾을 수 없습니다";
    private const string FolderNotEmpty = "폴더가 비어 있지 않습니다";
    private const string ArchiveMissing = "보관 드라이브가 연결되어 있지 않습니다";
    private const string NotPerformed = "실행되지 않은 작업이라 되돌릴 것이 없습니다";

    private readonly FakeFileSystem _fs = new();
    private readonly HookedFileSystem _hooked;

    public UndoTests()
    {
        _hooked = new HookedFileSystem(_fs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
    }

    // ------------------------------------------------------------------
    // 도우미
    // ------------------------------------------------------------------

    private ExecutionContext Context() => new(_hooked, new XxHasher(_hooked), AppData, () => Now);

    private Executor NewExecutor() => new(Context());

    private Undoer NewUndoer() => new(Context());

    private static PlannedOperation Copy(string id, string source, string destination, long length) =>
        new(id, OperationKind.Copy, source, destination, length, Array.Empty<ReviewReason>());

    private static PlannedOperation Move(string id, string source, string destination, long length) =>
        new(id, OperationKind.Move, source, destination, length, Array.Empty<ReviewReason>());

    private static PlannedOperation Mkdir(string id, string destination) =>
        new(id, OperationKind.CreateDirectory, null, destination, 0, Array.Empty<ReviewReason>());

    private static Plan PlanOf(params PlannedOperation[] ops) =>
        new(Archive, ops, Array.Empty<PlannedSkip>(), Array.Empty<string>());

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private byte[] AddFile(string path, int length, int seed)
    {
        var data = Bytes(length, seed);
        _fs.AddFile(path, data);
        return data;
    }

    private ExecutionResult Run(Plan plan)
    {
        var result = NewExecutor().Execute(plan);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        return result;
    }

    private List<JournalEntry> Entries(string path) =>
        Encoding.UTF8.GetString(_fs.ReadAllBytes(path))
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JournalEntry>(line, JournalWriter.JsonOptions)!)
            .ToList();

    private static OpState StateOf(RunState state, string opId) => state.Operations.Single(o => o.Operation.Id == opId);

    /// <summary>아카이브에서 <c>.photook</c> 밖에 있는 파일.</summary>
    private List<string> ArchiveFiles() =>
        _fs.AllFiles()
            .Where(p => p.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains(@"\.photook\", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string Quarantined(string runId, string relative) =>
        Path.Combine(Archive, ".photook", "되돌림", runId, relative);

    private static string ArchiveJournal => JournalPaths.InArchive(Archive, RunId);

    private static string AppDataJournal => JournalPaths.InAppData(AppData, RunId);

    /// <summary>손으로 만든 기록: RunStarted, 모든 작업의 Planned, 그리고 작업별로 주어진 사건들. 두 사본에 쓴다.</summary>
    private string WriteJournal(PlannedOperation[] ops, params (string OpId, JournalEvent Event, string? Hash)[] events)
    {
        var writer = new JournalWriter(_fs, [ArchiveJournal, AppDataJournal]);
        writer.Write(new JournalEntry(RunId, JournalEvent.RunStarted, Length: ops.Length, Destination: Archive, TimestampUtc: Now));
        foreach (var op in ops)
        {
            writer.Write(new JournalEntry(RunId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
                TimestampUtc: Now));
        }

        foreach (var (opId, ev, hash) in events)
        {
            var op = ops.Single(o => o.Id == opId);
            writer.Write(new JournalEntry(RunId, ev, op.Id, op.Kind, Hash: hash, TimestampUtc: Now));
        }

        return ArchiveJournal;
    }

    private string Hash(string path) => new XxHasher(_fs).Hash(path);

    // ------------------------------------------------------------------
    // 복사 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_QuarantinesCopies_AndRemovesCreatedFolders()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 1);
        var b = AddFile(@"E:\DCIM\b.jpg", 200, 2);
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Mkdir("op2", @"D:\Archive\2026\10"),
            Copy("op3", @"E:\DCIM\a.jpg", @"D:\Archive\2026\10\a.jpg", a.Length),
            Copy("op4", @"E:\DCIM\b.jpg", @"D:\Archive\2026\10\b.jpg", b.Length)));

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(4, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.Empty(ArchiveFiles());
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026\10"));
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
        Assert.True(_fs.DirectoryExists(Archive));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"E:\DCIM\b.jpg"));
        Assert.Equal(a, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\10\a.jpg")));
        Assert.Equal(b, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\10\b.jpg")));

        var state = JournalReader.Load(_fs, run.JournalPath!);
        Assert.All(state.Operations, o => Assert.Equal(OpStatus.Undone, o.Status));
        var undone = Entries(run.JournalPath!).Where(e => e.Event == JournalEvent.Undone).Select(e => e.OpId).ToList();
        Assert.Equal(["op4", "op3", "op2", "op1"], undone); // 완료 순서의 역순
    }

    [Fact]
    public void Undo_KeepsCopy_WhenSourceIsGone()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 3);
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));
        _fs.RemoveVolume(@"E:\"); // SD 카드를 뺐다

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(0, result.Undone);
        Assert.Equal(
            [
                new UndoSkip("op2", @"D:\Archive\2026\a.jpg", KeptNoSource),
                new UndoSkip("op1", @"D:\Archive\2026", FolderNotEmpty),
            ],
            result.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.False(_fs.DirectoryExists(Path.Combine(Archive, ".photook", "되돌림")));

        // 드라이브가 빠진 것은 일시적이므로 기록하지 않는다. 다시 연결하면 되돌릴 수 있다.
        Assert.Equal(OpStatus.Completed, StateOf(JournalReader.Load(_fs, run.JournalPath!), "op2").Status);
        _fs.RestoreVolume(@"E:\");

        var again = NewUndoer().Undo(run.JournalPath!);

        // 일시적인 사유가 있던 되돌리기에서는 "폴더가 비어 있지 않습니다"도 기록하지 않았으므로 이번에 폴더까지 치운다.
        Assert.Equal(2, again.Undone);
        Assert.Empty(again.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\a.jpg")));
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
    }

    [Fact]
    public void Undo_KeepsCopy_WhenSourceContentNoLongerMatches()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 4);
        _fs.AddDirectory(@"D:\Archive\2026");
        var run = Run(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));
        _fs.AddFile(@"E:\DCIM\a.jpg", Bytes(100, 5)); // SD 카드를 정리하고 새로 찍었다(같은 이름, 다른 사진)

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(0, result.Undone);
        Assert.Equal([new UndoSkip("op1", @"D:\Archive\2026\a.jpg", KeptNoSource)], result.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));

        var last = Entries(run.JournalPath!).Last();
        Assert.Equal(JournalEvent.UndoSkipped, last.Event);
        Assert.Equal(KeptNoSource, last.Message);
        Assert.Equal(OpStatus.UndoSkipped, StateOf(JournalReader.Load(_fs, run.JournalPath!), "op1").Status);
    }

    [Fact]
    public void Undo_KeepsCopy_WhenModifiedAfterRun()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 6);
        var b = AddFile(@"E:\DCIM\b.jpg", 100, 7);
        _fs.AddDirectory(@"D:\Archive\2026");
        var run = Run(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));
        var edited = Bytes(100, 8);
        _fs.AddFile(@"D:\Archive\2026\a.jpg", edited); // 실행 후 사용자가 보정했다

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(1, result.Undone);
        Assert.Equal([new UndoSkip("op1", @"D:\Archive\2026\a.jpg", KeptModified)], result.Skipped);
        Assert.Equal(edited, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\b.jpg"));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));

        var skipped = Entries(run.JournalPath!).Single(e => e.Event == JournalEvent.UndoSkipped);
        Assert.Equal("op1", skipped.OpId);
        Assert.Equal(KeptModified, skipped.Message);
    }

    [Fact]
    public void Undo_SkipsCopy_WhenCopyIsAlreadyGone()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 9);
        _fs.AddDirectory(@"D:\Archive\2026");
        _fs.AddDirectory(@"D:\Elsewhere");
        var run = Run(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));
        _fs.Move(@"D:\Archive\2026\a.jpg", @"D:\Elsewhere\a.jpg"); // 사용자가 직접 치웠다

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(0, result.Undone);
        Assert.Equal([new UndoSkip("op1", @"D:\Archive\2026\a.jpg", NoCopy)], result.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Elsewhere\a.jpg"));
    }

    [Fact]
    public void Undo_KeepsPreexistingFolders()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 10);
        _fs.AddDirectory(@"D:\Archive\2026"); // 원래 있던(비어 있는) 폴더
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Mkdir("op2", @"D:\Archive\2026\10"),
            Copy("op3", @"E:\DCIM\a.jpg", @"D:\Archive\2026\10\a.jpg", a.Length)));
        Assert.Equal(OpStatus.Skipped, StateOf(JournalReader.Load(_fs, run.JournalPath!), "op1").Status);

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(2, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.True(_fs.DirectoryExists(@"D:\Archive\2026"));
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026\10"));
        Assert.Equal(OpStatus.Skipped, StateOf(JournalReader.Load(_fs, run.JournalPath!), "op1").Status);
    }

    [Fact]
    public void Undo_IsIdempotent()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 11);
        var b = AddFile(@"E:\DCIM\b.jpg", 100, 12);
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op3", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));
        _fs.AddFile(@"D:\Archive\2026\b.jpg", Bytes(100, 13)); // 바뀐 파일 하나는 남는다

        var first = NewUndoer().Undo(run.JournalPath!);
        var entriesAfterFirst = Entries(run.JournalPath!).Count;
        var opsAfterFirst = _fs.MutatingOps;
        var second = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(1, first.Undone);
        Assert.Equal(2, first.Skipped.Count);
        Assert.Equal(0, second.Undone);
        Assert.Empty(second.Skipped);
        Assert.Equal(entriesAfterFirst, Entries(run.JournalPath!).Count);
        Assert.Equal(opsAfterFirst, _fs.MutatingOps);
        Assert.Equal(a, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\a.jpg")));
    }

    [Fact]
    public void Undo_ContinuesAfterCrash_BetweenQuarantineAndRecord()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 14);
        var b = AddFile(@"E:\DCIM\b.jpg", 100, 15);
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op3", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));
        _hooked.AfterMove = (_, destination) =>
        {
            if (destination.Contains("되돌림", StringComparison.Ordinal))
            {
                throw new SimulatedCrashException(); // b를 격리한 직후, Undone을 쓰기 전에 죽는다
            }
        };
        Assert.Throws<SimulatedCrashException>(() => NewUndoer().Undo(run.JournalPath!));
        _hooked.AfterMove = null;

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(3, result.Undone); // b는 이미 격리 폴더에 있으므로 "복사본이 없습니다"가 아니다
        Assert.Empty(result.Skipped);
        Assert.Empty(ArchiveFiles());
        Assert.Equal(b, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\b.jpg")));
        Assert.Equal(a, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\a.jpg")));
        Assert.All(JournalReader.Load(_fs, run.JournalPath!).Operations, o => Assert.Equal(OpStatus.Undone, o.Status));
    }

    // ------------------------------------------------------------------
    // 이동 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_MovesFilesBack_ForMoveRun()
    {
        var a = AddFile(@"D:\Inbox\a.jpg", 100, 20);
        var b = AddFile(@"D:\Inbox\sub\b.jpg", 200, 21);
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Move("op2", @"D:\Inbox\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Move("op3", @"D:\Inbox\sub\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));
        _fs.RemoveEmptyDirectory(@"D:\Inbox\sub"); // 비게 된 원본 폴더를 사용자가 지웠다

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(3, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Inbox\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Inbox\sub\b.jpg")); // 원본 폴더를 다시 만들어 돌려놓는다
        Assert.Empty(ArchiveFiles());
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
        Assert.False(_fs.DirectoryExists(Path.Combine(Archive, ".photook", "되돌림")));
    }

    [Fact]
    public void Undo_SkipsMoveBack_WhenSpotTakenOrFileMissing()
    {
        var a = AddFile(@"D:\Inbox\a.jpg", 100, 22);
        var b = AddFile(@"D:\Inbox\b.jpg", 200, 23);
        _fs.AddDirectory(@"D:\Archive\2026");
        _fs.AddDirectory(@"D:\Elsewhere");
        var run = Run(PlanOf(
            Move("op1", @"D:\Inbox\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Move("op2", @"D:\Inbox\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));
        var newcomer = AddFile(@"D:\Inbox\a.jpg", 50, 24);                  // 원래 자리에 새 파일
        _fs.Move(@"D:\Archive\2026\b.jpg", @"D:\Elsewhere\b.jpg");          // 옮긴 파일을 사용자가 다른 곳으로

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(0, result.Undone);
        Assert.Equal(
            [
                new UndoSkip("op2", @"D:\Archive\2026\b.jpg", MovedMissing),
                new UndoSkip("op1", @"D:\Archive\2026\a.jpg", SpotTaken),
            ],
            result.Skipped);
        Assert.Equal(newcomer, _fs.ReadAllBytes(@"D:\Inbox\a.jpg"));
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Elsewhere\b.jpg"));
    }

    [Fact]
    public void Undo_ReversesSwapViaTempName()
    {
        const string Day14 = @"D:\Archive\2026\2026-10-14";
        const string Day15 = @"D:\Archive\2026\2026-10-15";
        var oct14 = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
        var oct15 = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);
        byte[] pa = [1, 1, 1];
        byte[] pb = [2, 2, 2];
        _fs.AddFile($@"{Day15}\P.JPG", pa); // 14일 사진이 15일 폴더에
        _fs.AddFile($@"{Day14}\P.JPG", pb); // 15일 사진이 14일 폴더에
        var groups = new[]
        {
            new FileGroup(Info($@"{Day15}\P.JPG", pa, oct14), []),
            new FileGroup(Info($@"{Day14}\P.JPG", pb, oct15), []),
        };
        var preset = BuiltInPresets.DateOnly;
        var sessions = SessionGrouper.GroupByDay(groups, preset.DayStart, TimeZoneInfo.Utc);
        var plan = new Planner(_fs, new XxHasher(_fs))
            .Create(new PlanRequest(sessions, preset, Archive, TransferMode.Move, TimeZoneInfo.Utc));
        Assert.Contains(plan.Operations, o => o.Destination.Contains(".photook-tmp-", StringComparison.Ordinal));

        var run = Run(plan);
        Assert.Equal(pa, _fs.ReadAllBytes($@"{Day14}\P.JPG"));
        Assert.Equal(pb, _fs.ReadAllBytes($@"{Day15}\P.JPG"));

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(plan.Operations.Count, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.Equal(pa, _fs.ReadAllBytes($@"{Day15}\P.JPG"));
        Assert.Equal(pb, _fs.ReadAllBytes($@"{Day14}\P.JPG"));
        Assert.Equal(2, ArchiveFiles().Count);
    }

    private static PhotoInfo Info(string path, byte[] content, DateTime capture) =>
        new(path, content.Length, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), FileKinds.Default.Classify(path),
            capture, null, null, null);

    // ------------------------------------------------------------------
    // 기록되지 않은 채 끝난 작업 (Started / Failed)
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_RevertsPerformedButUnrecordedMove_FromStrandedSwap()
    {
        // 자리 바꾸기 도중 op3(임시 이름 → 14일)는 실제로 옮겼지만 Completed를 쓰기 전에 끊겼다.
        const string Day14 = @"D:\Archive\2026\14\P.JPG";
        const string Day15 = @"D:\Archive\2026\15\P.JPG";
        const string TempA = Day15 + ".photook-tmp-op1";
        const string TempB = Day14 + ".photook-tmp-op2";
        var pa = Bytes(100, 30);
        var pb = Bytes(100, 31); // 같은 크기: 크기만으로는 두 파일을 구별할 수 없다
        _fs.AddFile(Day14, pa);  // op3가 옮겨 놓은 A
        _fs.AddFile(TempB, pb);  // op2가 임시 이름으로 옮겨 둔 B
        _fs.AddDirectory(@"D:\Archive\2026\15");
        var ops = new[]
        {
            Move("op1", Day15, TempA, pa.Length),
            Move("op2", Day14, TempB, pb.Length),
            Move("op3", TempA, Day14, pa.Length),
            Move("op4", TempB, Day15, pb.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, null),
            ("op3", JournalEvent.Started, null));

        var result = NewUndoer().Undo(journal);

        Assert.Equal(3, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.Equal(pa, _fs.ReadAllBytes(Day15));
        Assert.Equal(pb, _fs.ReadAllBytes(Day14));
        Assert.Equal(2, ArchiveFiles().Count);

        var state = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.Undone, StateOf(state, "op3").Status);
        Assert.Equal(OpStatus.Pending, StateOf(state, "op4").Status);
    }

    [Fact]
    public void Undo_LeavesUnperformedFailedMove_AndRestoresStrandedTemp()
    {
        // 두 번째 다리(op3)가 실패해 A가 임시 이름에 남았다. 스캔에는 보이지 않으므로 되돌리기가 찾아 돌려놓아야 한다.
        const string Day14 = @"D:\Archive\2026\14\P.JPG";
        const string Day15 = @"D:\Archive\2026\15\P.JPG";
        const string TempA = Day15 + ".photook-tmp-op1";
        const string TempB = Day14 + ".photook-tmp-op2";
        var pa = Bytes(100, 32);
        var pb = Bytes(100, 33); // 같은 크기
        _fs.AddFile(TempA, pa);
        _fs.AddFile(TempB, pb);
        var ops = new[]
        {
            Move("op1", Day15, TempA, pa.Length),
            Move("op2", Day14, TempB, pb.Length),
            Move("op3", TempA, Day14, pa.Length),
            Move("op4", TempB, Day15, pb.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, null),
            ("op3", JournalEvent.Started, null), ("op3", JournalEvent.Failed, null));

        var result = NewUndoer().Undo(journal);

        Assert.Equal(2, result.Undone);
        Assert.Equal([new UndoSkip("op3", Day14, NotPerformed)], result.Skipped);
        Assert.Equal(pa, _fs.ReadAllBytes(Day15));
        Assert.Equal(pb, _fs.ReadAllBytes(Day14));
        Assert.Equal(OpStatus.UndoSkipped, StateOf(JournalReader.Load(_fs, journal), "op3").Status);
    }

    [Fact]
    public void Undo_QuarantinesVerifiedCopy_LeftStartedByCrash()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 700, 40);
        _hooked.AfterMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException(); // 진짜 이름을 붙인 직후, Completed를 쓰기 전에 죽는다
            }
        };
        Assert.Throws<SimulatedCrashException>(() => NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length))));
        _hooked.AfterMove = null;
        var journal = _fs.AllFiles().Single(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        var runId = JournalReader.Load(_fs, journal).RunId;
        Assert.Equal(OpStatus.Started, StateOf(JournalReader.Load(_fs, journal), "op2").Status);

        var result = NewUndoer().Undo(journal);

        Assert.Equal(2, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.Empty(ArchiveFiles());
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
        Assert.Equal(a, _fs.ReadAllBytes(Quarantined(runId, @"2026\a.jpg")));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
        Assert.Equal(OpStatus.Undone, StateOf(JournalReader.Load(_fs, journal), "op2").Status);
    }

    [Fact]
    public void Undo_QuarantinesLeftoverTempFile_FromCrashedCopy()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 1024 * 1024 + 500, 41); // 버퍼 두 번에 걸쳐 쓰인다
        // 폴더 준비 2 + 기록 2×(RunStarted, Planned×2) = 8, op1: Started×2·CreateDirectory·Completed×2 = 5,
        // op2: Started×2·CreateNew·Write 1번 = 4 → 17. 두 번째 조각을 쓰다 죽는다.
        _fs.CrashAfter(17);
        Assert.Throws<SimulatedCrashException>(() => NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length))));
        _fs.ClearFault();
        var journal = _fs.AllFiles().Single(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        Assert.True(_fs.FileExists(@"D:\Archive\2026\a.jpg.photook-tmp"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));

        var result = NewUndoer().Undo(journal);

        Assert.Equal(1, result.Undone); // 폴더. 임시 파일은 작업이 아니므로 세지 않는다
        Assert.Equal([new UndoSkip("op2", @"D:\Archive\2026\a.jpg", NotPerformed)], result.Skipped);
        Assert.Empty(ArchiveFiles());
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
    }

    [Fact]
    public void Undo_LeavesUnrecordedCopyDestination_WhenItDiffersFromSource()
    {
        // 실행 중 대상 자리에 다른 파일이 생겨 복사가 실패했다. 그 파일은 이번 실행이 만든 것이 아니다.
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 42);
        var other = AddFile(@"D:\Archive\2026\a.jpg", 100, 43);
        var op = Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length);
        var journal = WriteJournal([op], ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Failed, null));

        var result = NewUndoer().Undo(journal);

        Assert.Equal(0, result.Undone);
        Assert.Equal([new UndoSkip("op1", @"D:\Archive\2026\a.jpg", NotPerformed)], result.Skipped);
        Assert.Equal(other, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
    }

    [Fact]
    public void Undo_SecondRun_DoesNothing_ForSameSizeSwapWithFailedLeg()
    {
        // 같은 크기의 자리 바꾸기에서 두 번째 다리(op3)가 실패했다. 첫 되돌리기가 원래 배치로 돌려놓은 뒤,
        // 두 번째 되돌리기가 op3를 "옮긴 뒤 기록하지 못한 작업"으로 착각해 사진을 임시 이름으로 옮기면 안 된다.
        const string Day14 = @"D:\Archive\2026\14\P.JPG";
        const string Day15 = @"D:\Archive\2026\15\P.JPG";
        const string TempA = Day15 + ".photook-tmp-op1";
        const string TempB = Day14 + ".photook-tmp-op2";
        var pa = Bytes(100, 34);
        var pb = Bytes(100, 35);
        _fs.AddFile(TempA, pa);
        _fs.AddFile(TempB, pb);
        var ops = new[]
        {
            Move("op1", Day15, TempA, pa.Length),
            Move("op2", Day14, TempB, pb.Length),
            Move("op3", TempA, Day14, pa.Length),
            Move("op4", TempB, Day15, pb.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, null),
            ("op3", JournalEvent.Started, null), ("op3", JournalEvent.Failed, null));

        var first = NewUndoer().Undo(journal);
        Assert.Equal(2, first.Undone);
        Assert.Equal(pa, _fs.ReadAllBytes(Day15));
        Assert.Equal(pb, _fs.ReadAllBytes(Day14));
        var filesAfterFirst = ArchiveFiles();
        var entriesAfterFirst = Entries(journal).Count;

        var second = NewUndoer().Undo(journal);

        Assert.Equal(0, second.Undone);
        Assert.Empty(second.Skipped);
        Assert.Equal(filesAfterFirst, ArchiveFiles());
        Assert.Equal(pa, _fs.ReadAllBytes(Day15));
        Assert.Equal(pb, _fs.ReadAllBytes(Day14));
        Assert.Equal(entriesAfterFirst, Entries(journal).Count);
        var skipped = Entries(journal).Single(e => e.Event == JournalEvent.UndoSkipped);
        Assert.Equal("op3", skipped.OpId);
        Assert.Equal(NotPerformed, skipped.Message);
    }

    [Fact]
    public void Undo_RevertsEarlyFailedButPerformedMove_WhenLaterOpConsumedItsDestination()
    {
        // op1은 실제로 옮겼지만 Failed로 기록됐고, 뒤의 op2가 op1의 대상(임시 이름)을 원본으로 써서 완료했다.
        // 기록 순서의 역순(op2 → op1)으로 처리해야 파일이 원래 자리로 돌아간다.
        // 지금의 Executor·Resume로는 만들 수 없는 기록이라 손으로 쓴다(Resume은 Failed 이동을 계획 순서대로 먼저 맞춰 본다).
        const string Original = @"D:\Archive\2026\14\P.JPG";
        const string Temp = Original + ".photook-tmp-op1";
        const string Final = @"D:\Archive\2026\15\P.JPG";
        var p = Bytes(100, 36);
        _fs.AddFile(Final, p);
        _fs.AddDirectory(@"D:\Archive\2026\14");
        var ops = new[]
        {
            Move("op1", Original, Temp, p.Length),
            Move("op2", Temp, Final, p.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Failed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, null));
        var state = JournalReader.Load(_fs, journal);
        Assert.True(StateOf(state, "op2").LastSeq > StateOf(state, "op1").LastSeq);

        var result = NewUndoer().Undo(journal);

        Assert.Equal(2, result.Undone);
        Assert.Empty(result.Skipped);
        Assert.Equal(p, _fs.ReadAllBytes(Original));
        Assert.False(_fs.FileExists(Temp));
        Assert.False(_fs.FileExists(Final));
        Assert.Equal(["op2", "op1"],
            Entries(journal).Where(e => e.Event == JournalEvent.Undone).Select(e => e.OpId).ToList());
    }

    [Theory]
    [InlineData(JournalEvent.Failed)]     // 옮겼지만 Failed로 기록된 두 번째 다리
    [InlineData(JournalEvent.Completed)]
    public void Undo_StopsAtFirstTransient_AndCompletesSafelyOnRerun(JournalEvent op3Result)
    {
        // 같은 크기의 자리 바꾸기. op3(임시 이름 → 14일)는 실제로 옮겼다: A는 14일 자리에, 임시 이름 A는 비어 있다.
        const string Day14 = @"D:\Archive\2026\14\P.JPG";
        const string Day15 = @"D:\Archive\2026\15\P.JPG";
        const string TempA = Day15 + ".photook-tmp-op1";
        const string TempB = Day14 + ".photook-tmp-op2";
        var pa = Bytes(100, 38);
        var pb = Bytes(100, 39);
        _fs.AddFile(Day14, pa);
        _fs.AddFile(TempB, pb);
        _fs.AddDirectory(@"D:\Archive\2026\15");
        var ops = new[]
        {
            Move("op1", Day15, TempA, pa.Length),
            Move("op2", Day14, TempB, pb.Length),
            Move("op3", TempA, Day14, pa.Length),
            Move("op4", TempB, Day15, pb.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, null),
            ("op3", JournalEvent.Started, null), ("op3", op3Result, null));
        var countBefore = Entries(journal).Count;

        // 사진이 뷰어에 열려 있어 op3를 돌려놓는 이동이 공유 위반으로 실패한다.
        _hooked.BeforeMove = (source, _) =>
        {
            if (string.Equals(source, Day14, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("다른 프로세스가 파일을 사용 중입니다");
            }
        };

        var first = NewUndoer().Undo(journal);

        Assert.Equal(0, first.Undone);
        var skip = Assert.Single(first.Skipped);
        Assert.Equal("op3", skip.OpId);
        Assert.StartsWith("파일 작업 중 오류가 발생했습니다", skip.Reason);
        Assert.Equal([JournalEvent.UndoStarted], Entries(journal).Skip(countBefore).Select(e => e.Event).ToList());
        Assert.Equal(pa, _fs.ReadAllBytes(Day14)); // op2·op1은 건드리지 않았다
        Assert.Equal(pb, _fs.ReadAllBytes(TempB));
        var state = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op2").Status);

        _hooked.BeforeMove = null; // 뷰어를 닫았다
        var second = NewUndoer().Undo(journal);

        Assert.Equal(3, second.Undone);
        Assert.Empty(second.Skipped);
        Assert.Equal(pa, _fs.ReadAllBytes(Day15));
        Assert.Equal(pb, _fs.ReadAllBytes(Day14));
        Assert.DoesNotContain(_fs.AllFiles(), p => p.Contains(".photook-tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Undo_KeepsFolderOfUnfinishedCreateDirectory_WhenItExists()
    {
        // 폴더 만들기가 Started에서 끊겼다. 원래 있던 폴더인지(Skipped를 쓰기 전) 이번에 만든 폴더인지(Completed를 쓰기 전) 알 수 없다.
        _fs.AddDirectory(@"D:\Archive\2026");
        var ops = new[] { Mkdir("op1", @"D:\Archive\2026"), Mkdir("op2", @"D:\Archive\2027") };
        var journal = WriteJournal(ops, ("op1", JournalEvent.Started, null), ("op2", JournalEvent.Started, null));

        var result = NewUndoer().Undo(journal);

        Assert.Equal(0, result.Undone);
        Assert.Equal(
            [
                new UndoSkip("op2", @"D:\Archive\2027", NotPerformed),
                new UndoSkip("op1", @"D:\Archive\2026", "만들었는지 알 수 없는 폴더라 그대로 둡니다"),
            ],
            result.Skipped);
        Assert.True(_fs.DirectoryExists(@"D:\Archive\2026"));
        var state = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.UndoSkipped, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.UndoSkipped, StateOf(state, "op2").Status);
    }

    // ------------------------------------------------------------------
    // 임시 이름에 사진이 남지 않는다
    // ------------------------------------------------------------------

    private const string Day14Dir = @"D:\Archive\2026\2026-10-14";
    private const string Day15Dir = @"D:\Archive\2026\2026-10-15";

    /// <summary>
    /// 이동 모드에서 B(14일 폴더, 15일 사진)는 15일로 옮기고, SD 카드의 A(14일 사진)는 다른 드라이브라 복사로 B가 비우는 자리에 들어간다.
    /// 계획: 15일 폴더 만들기, B → 임시 이름, A 복사 → 14일 자리, 임시 이름 → 15일. 실행까지 마친다.
    /// </summary>
    private (ExecutionResult Run, Plan Plan, byte[] A, byte[] B) RunCrossVolumeCopyIntoVacatedSpot()
    {
        byte[] pa = [1, 1, 1, 1];
        byte[] pb = [2, 2, 2];
        _fs.AddFile(@"E:\DCIM\P.JPG", pa);
        _fs.AddFile($@"{Day14Dir}\P.JPG", pb);
        var groups = new[]
        {
            new FileGroup(Info(@"E:\DCIM\P.JPG", pa, new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified)), []),
            new FileGroup(Info($@"{Day14Dir}\P.JPG", pb, new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified)), []),
        };
        var preset = BuiltInPresets.DateOnly;
        var sessions = SessionGrouper.GroupByDay(groups, preset.DayStart, TimeZoneInfo.Utc);
        var plan = new Planner(_fs, new XxHasher(_fs))
            .Create(new PlanRequest(sessions, preset, Archive, TransferMode.Move, TimeZoneInfo.Utc));
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Copy && o.Destination == $@"{Day14Dir}\P.JPG");
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Move && o.Destination.Contains(".photook-tmp-"));

        var run = Run(plan);
        Assert.Equal(pa, _fs.ReadAllBytes($@"{Day14Dir}\P.JPG"));
        Assert.Equal(pb, _fs.ReadAllBytes($@"{Day15Dir}\P.JPG"));
        return (run, plan, pa, pb);
    }

    private List<string> TempNamedFiles() =>
        _fs.AllFiles().Where(p => Path.GetFileName(p).Contains(".photook-tmp", StringComparison.OrdinalIgnoreCase)
                                  && !p.Contains(@"\.photook\", StringComparison.OrdinalIgnoreCase)).ToList();

    [Fact]
    public void Undo_RestoresSwapOriginalUnderSuffixedName_WhenKeptCopyHoldsSpot_SourceWiped()
    {
        var (run, plan, pa, pb) = RunCrossVolumeCopyIntoVacatedSpot();
        _fs.AddDirectory(@"E:\Elsewhere");
        _fs.Move(@"E:\DCIM\P.JPG", @"E:\Elsewhere\P.JPG"); // SD 카드에서 사진을 지웠다(그 경로에는 이제 없다)
        var firstLeg = plan.Operations.Single(o => o.Kind == OperationKind.Move && o.Destination.Contains(".photook-tmp-"));
        var copy = plan.Operations.Single(o => o.Kind == OperationKind.Copy);
        var restored = $@"{Day14Dir}\P_1.JPG";

        var result = NewUndoer().Undo(run.JournalPath!);

        Assert.Empty(TempNamedFiles());
        Assert.Equal(pb, _fs.ReadAllBytes(restored));                 // 원래 사진은 보이는 이름으로 돌아왔다
        Assert.Equal(pa, _fs.ReadAllBytes($@"{Day14Dir}\P.JPG"));     // 유일한 사본이 된 복사본은 남는다
        Assert.False(_fs.FileExists($@"{Day15Dir}\P.JPG"));
        Assert.Contains(new UndoSkip(copy.Id, copy.Destination, KeptNoSource), result.Skipped);
        Assert.Contains(new UndoSkip(firstLeg.Id, firstLeg.Destination,
            $"원래 자리에 다른 파일이 있어 이름을 바꿔 되돌렸습니다: {restored}"), result.Skipped);

        var entries = Entries(run.JournalPath!);
        var undone = entries.Single(e => e.Event == JournalEvent.Undone && e.OpId == firstLeg.Id);
        Assert.Equal(restored, undone.Destination);
        Assert.Equal(OpStatus.Undone, StateOf(JournalReader.Load(_fs, run.JournalPath!), firstLeg.Id).Status);
    }

    [Fact]
    public void Undo_StopsPass_WhenUnavailableSourceCopyHoldsAnotherOpsSpot_ThenCompletesAfterReconnect()
    {
        var (run, plan, pa, pb) = RunCrossVolumeCopyIntoVacatedSpot();
        var firstLeg = plan.Operations.Single(o => o.Kind == OperationKind.Move && o.Destination.Contains(".photook-tmp-"));
        var copy = plan.Operations.Single(o => o.Kind == OperationKind.Copy);
        _fs.RemoveVolume(@"E:\"); // SD 카드를 뺐다

        var first = NewUndoer().Undo(run.JournalPath!);

        Assert.Equal(new UndoSkip(copy.Id, copy.Destination, "원본 드라이브가 연결되어 있지 않습니다"), first.Skipped[^1]);
        var state = JournalReader.Load(_fs, run.JournalPath!);
        Assert.Equal(OpStatus.Completed, StateOf(state, copy.Id).Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, firstLeg.Id).Status);
        Assert.Equal(pa, _fs.ReadAllBytes($@"{Day14Dir}\P.JPG"));
        Assert.DoesNotContain(Entries(run.JournalPath!), e => e.OpId == firstLeg.Id && e.Event is JournalEvent.Undone or JournalEvent.UndoSkipped);

        _fs.RestoreVolume(@"E:\");
        var second = NewUndoer().Undo(run.JournalPath!);

        Assert.Empty(second.Skipped);
        Assert.Empty(TempNamedFiles());
        Assert.Equal(pb, _fs.ReadAllBytes($@"{Day14Dir}\P.JPG"));
        Assert.False(_fs.DirectoryExists(Day15Dir));
        Assert.Equal(pa, _fs.ReadAllBytes(Quarantined(run.RunId, @"2026\2026-10-14\P.JPG")));
        Assert.Equal(pa, _fs.ReadAllBytes(@"E:\DCIM\P.JPG"));
        Assert.All(JournalReader.Load(_fs, run.JournalPath!).Operations, o => Assert.Equal(OpStatus.Undone, o.Status));
    }

    [Fact]
    public void Undo_NeverLeavesTempName_ForUnrecordedFirstLeg_WhenSpotTaken()
    {
        // 첫 다리(op1: 14일 → 임시 이름)는 옮겼지만 Failed로 기록됐고, op2 복사가 그 자리를 채웠다. 원본 사진은 지워졌다.
        const string Spot = @"D:\Archive\2026\14\P.JPG";
        const string Temp = Spot + ".photook-tmp-op1";
        var original = Bytes(100, 44);
        var copied = Bytes(80, 45);
        _fs.AddFile(Temp, original);
        _fs.AddFile(Spot, copied);
        var ops = new[]
        {
            Move("op1", Spot, Temp, original.Length),
            Copy("op2", @"E:\DCIM\P.JPG", Spot, copied.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Failed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, Hash(Spot)));
        _fs.AddFile(@"D:\Archive\2026\14\P_1.JPG", Bytes(10, 46)); // 첫 번째 붙임 이름도 이미 있다
        _fs.AddDirectory(@"E:\DCIM"); // SD 카드는 꽂혀 있지만 사진은 지웠다

        var result = NewUndoer().Undo(journal);

        Assert.Empty(TempNamedFiles());
        Assert.Equal(original, _fs.ReadAllBytes(@"D:\Archive\2026\14\P_2.JPG"));
        Assert.Equal(copied, _fs.ReadAllBytes(Spot));
        Assert.Equal(1, result.Undone);
        Assert.Equal(
            [
                new UndoSkip("op2", Spot, KeptNoSource),
                new UndoSkip("op1", Temp, @"원래 자리에 다른 파일이 있어 이름을 바꿔 되돌렸습니다: D:\Archive\2026\14\P_2.JPG"),
            ],
            result.Skipped);
        Assert.Equal(OpStatus.Undone, StateOf(JournalReader.Load(_fs, journal), "op1").Status);
    }

    [Fact]
    public void Undo_WritesUndoStarted_OnceBeforeFirstAction()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 37);
        _fs.AddDirectory(@"D:\Archive\2026");
        var run = Run(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));
        var countBefore = Entries(run.JournalPath!).Count;

        NewUndoer().Undo(run.JournalPath!);
        NewUndoer().Undo(run.JournalPath!);

        var entries = Entries(run.JournalPath!);
        Assert.Equal([JournalEvent.UndoStarted, JournalEvent.Undone], entries.Skip(countBefore).Select(e => e.Event).ToList());
        Assert.Null(entries[countBefore].OpId);
    }

    // ------------------------------------------------------------------
    // 드라이브와 기록 사본
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_WritesNothing_WhenArchiveVolumeMissing()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 50);
        var run = Run(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));
        var appDataJournal = JournalPaths.InAppData(AppData, run.RunId);
        var before = _fs.ReadAllBytes(appDataJournal);
        _fs.RemoveVolume(@"D:\");
        var opsBefore = _fs.MutatingOps;

        var result = NewUndoer().Undo(appDataJournal);

        Assert.Equal(0, result.Undone);
        Assert.Equal([new UndoSkip("", Archive, ArchiveMissing)], result.Skipped);
        Assert.Equal(opsBefore, _fs.MutatingOps);
        Assert.Equal(before, _fs.ReadAllBytes(appDataJournal));
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
    }

    [Fact]
    public void Undo_UsesLongestJournalCopy_AndAppendsOnlyToIt()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 51);
        var b = AddFile(@"E:\DCIM\b.jpg", 100, 52);
        _fs.AddDirectory(@"D:\Archive\2026");
        var ops = new[]
        {
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
        };
        _fs.AddFile(@"D:\Archive\2026\a.jpg", a);
        _fs.AddFile(@"D:\Archive\2026\b.jpg", b);
        WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, Hash(@"E:\DCIM\a.jpg")),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, Hash(@"E:\DCIM\b.jpg")));
        // 앱 데이터 사본은 op2를 기록하지 못했다(뒤처진 사본).
        var lagging = Encoding.UTF8.GetString(_fs.ReadAllBytes(AppDataJournal))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(5);
        _fs.AddFile(AppDataJournal, Encoding.UTF8.GetBytes(string.Join('\n', lagging) + "\n"));
        var appDataBefore = _fs.ReadAllBytes(AppDataJournal);

        var result = NewUndoer().Undo(AppDataJournal);

        Assert.Equal(2, result.Undone);
        Assert.Empty(ArchiveFiles());
        Assert.Equal(appDataBefore, _fs.ReadAllBytes(AppDataJournal));
        Assert.Equal(2, Entries(ArchiveJournal).Count(e => e.Event == JournalEvent.Undone));
    }
}
