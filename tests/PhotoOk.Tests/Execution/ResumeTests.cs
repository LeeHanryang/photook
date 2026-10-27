using System.IO;
using System.Text;
using System.Text.Json;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.Execution;

public class ResumeTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string RunId = "20261027-103000-ab12";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly HookedFileSystem _hooked;

    public ResumeTests()
    {
        _hooked = new HookedFileSystem(_fs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
    }

    // ------------------------------------------------------------------
    // 도우미
    // ------------------------------------------------------------------

    private Executor NewExecutor() => new(new ExecutionContext(_hooked, new XxHasher(_hooked), AppData, () => Now));

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

    private static string ArchiveJournal => JournalPaths.InArchive(Archive, RunId);

    private static string AppDataJournal => JournalPaths.InAppData(AppData, RunId);

    /// <summary>
    /// 손으로 만든 기록: RunStarted, 모든 작업의 Planned, 그리고 작업별로 주어진 사건들. RunFinished는 쓰지 않는다.
    /// </summary>
    private string WriteJournal(PlannedOperation[] ops, params (string OpId, JournalEvent Event, string? Message)[] events)
    {
        var writer = new JournalWriter(_fs, [ArchiveJournal, AppDataJournal]);
        writer.Write(new JournalEntry(RunId, JournalEvent.RunStarted, Length: ops.Length, Destination: Archive, TimestampUtc: Now));
        foreach (var op in ops)
        {
            writer.Write(new JournalEntry(RunId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
                TimestampUtc: Now));
        }

        foreach (var (opId, ev, message) in events)
        {
            var op = ops.Single(o => o.Id == opId);
            writer.Write(new JournalEntry(RunId, ev, op.Id, op.Kind, Message: message, TimestampUtc: Now));
        }

        return ArchiveJournal;
    }

    private List<JournalEntry> Entries(string path) =>
        Encoding.UTF8.GetString(_fs.ReadAllBytes(path))
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JournalEntry>(line, JournalWriter.JsonOptions)!)
            .ToList();

    private static OpState StateOf(RunState state, string opId) => state.Operations.Single(o => o.Operation.Id == opId);

    private IEnumerable<string> TempFiles() =>
        _fs.AllFiles().Where(p => Path.GetFileName(p).Contains(".photook-tmp", StringComparison.OrdinalIgnoreCase)
                                  && !p.Contains(@"\.photook\", StringComparison.OrdinalIgnoreCase));

    private string Hash(string path) => new XxHasher(_fs).Hash(path);

    // ------------------------------------------------------------------
    // 복사
    // ------------------------------------------------------------------

    [Fact]
    public void Resume_FinishesRun_AfterCrashMidCopy()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 1);
        var b = AddFile(@"E:\DCIM\b.jpg", 1024 * 1024 + 500, 2); // 버퍼 두 번에 걸쳐 쓰인다
        var c = AddFile(@"E:\DCIM\c.jpg", 300, 3);
        _fs.AddDirectory(@"D:\Archive\2026");
        var plan = PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", c.Length));

        _fs.NowUtc = Now; // 새로 쓰는 파일의 시각(원본은 기본 시각)

        // 폴더 준비 2 + 계획 묶음(RunStarted, Planned×3) 2 = 4, op1: Started×2·CreateNew·Write·시각·Move·Completed×2 = 8,
        // op2: Started×2·CreateNew·Write 1번 = 4 → 16. b의 두 번째 조각을 쓰다 죽는다.
        _fs.CrashAfter(16);
        Assert.Throws<SimulatedCrashException>(() => NewExecutor().Execute(plan));
        _fs.ClearFault();

        var journal = _fs.AllFiles().Single(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        var before = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.Completed, StateOf(before, "op1").Status);
        Assert.Equal(OpStatus.Started, StateOf(before, "op2").Status);
        Assert.Equal(OpStatus.Pending, StateOf(before, "op3").Status);
        Assert.True(_fs.FileExists(@"D:\Archive\2026\b.jpg.photook-tmp")); // 쓰다 만 임시 파일

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(before.RunId, result.RunId);
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        Assert.Equal(c, _fs.ReadAllBytes(@"D:\Archive\2026\c.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"E:\DCIM\b.jpg"));
        Assert.Empty(TempFiles());
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
        {
            // 이어 하기가 다시 복사한 파일(b)도 원본의 마지막 쓰기 시각을 가진다.
            Assert.Equal(_fs.GetFile($@"E:\DCIM\{name}").LastWriteTimeUtc, _fs.GetFile($@"D:\Archive\2026\{name}").LastWriteTimeUtc);
        }

        var after = JournalReader.Load(_fs, journal);
        Assert.True(after.Finished);
        Assert.All(after.Operations, o => Assert.Equal(OpStatus.Completed, o.Status));
        Assert.Equal(Hash(@"E:\DCIM\b.jpg"), StateOf(after, "op2").Hash);
        Assert.Equal(JournalPaths.InArchive(Archive, before.RunId), result.JournalPath); // 두 사본이 같으면 아카이브 쪽
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Resume_MarksCompleted_WhenCopyFinishedBeforeCrash()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 500, 4);
        _fs.AddFile(@"D:\Archive\2026\a.jpg", a); // 검증을 마치고 진짜 이름까지 붙였지만 Completed는 못 쓴 상태
        var op = Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length);
        var journal = WriteJournal([op], ("op1", JournalEvent.Started, null));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(RunId, result.RunId);
        Assert.Equal(1, result.Succeeded);
        Assert.Empty(_hooked.Created); // 다시 복사하지 않는다
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));

        var state = JournalReader.Load(_fs, journal);
        Assert.True(state.Finished);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(Hash(@"E:\DCIM\a.jpg"), StateOf(state, "op1").Hash);
    }

    [Fact]
    public void Resume_ReconcilesStartedCopyWhoseVerifiedFileAlreadyAtDestination()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 700, 5);
        _fs.AddDirectory(@"D:\Archive\2026");
        _hooked.AfterMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new SimulatedCrashException(); // 임시 이름 → 진짜 이름 직후, Completed를 쓰기 전에 죽는다
            }
        };

        Assert.Throws<SimulatedCrashException>(() =>
            NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length))));
        _hooked.AfterMove = null;
        _hooked.Created.Clear();

        var journal = _fs.AllFiles().Single(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(OpStatus.Started, StateOf(JournalReader.Load(_fs, journal), "op1").Status);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(_hooked.Created);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Empty(TempFiles());
        Assert.False(_fs.DirectoryExists(@"D:\Archive\.photook\되돌림"));
        var state = JournalReader.Load(_fs, journal);
        Assert.True(state.Finished);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(Hash(@"E:\DCIM\a.jpg"), StateOf(state, "op1").Hash);
    }

    [Fact]
    public void Resume_KeepsDifferentDestination_ForStartedCopy_AndQuarantinesTemp()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 500, 6);
        var other = AddFile(@"D:\Archive\2026\a.jpg", 500, 7); // 같은 이름의 다른 파일
        var stale = AddFile(@"D:\Archive\2026\a.jpg.photook-tmp", 200, 8);
        var op = Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length);
        var journal = WriteJournal([op], ("op1", JournalEvent.Started, null));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal("대상에 같은 이름의 파일이 생겼습니다", Assert.Single(result.Failures).Message);
        Assert.Equal(other, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
        Assert.Empty(TempFiles());
        var quarantined = new SafeDelete(_fs, Archive, RunId).QuarantineRoot + @"\2026\a.jpg.photook-tmp";
        Assert.Equal(stale, _fs.ReadAllBytes(quarantined));
        Assert.Equal(OpStatus.Failed, StateOf(JournalReader.Load(_fs, journal), "op1").Status);
    }

    [Fact]
    public void Resume_Interrupts_WhenSourceVolumeStillMissing_ForStartedCopy()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 500, 9);
        _fs.AddDirectory(@"D:\Archive\2026");
        var op = Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length);
        var journal = WriteJournal([op], ("op1", JournalEvent.Started, null));
        _fs.RemoveVolume(@"E:\");

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Interrupted, result.Outcome);
        Assert.Equal("원본 드라이브가 연결되어 있지 않습니다", Assert.Single(result.Failures).Message);
        Assert.Empty(_hooked.Created);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
        var state = JournalReader.Load(_fs, journal);
        Assert.False(state.Finished);
        Assert.Equal(OpStatus.Failed, StateOf(state, "op1").Status);
    }

    // ------------------------------------------------------------------
    // 이동
    // ------------------------------------------------------------------

    [Fact]
    public void Resume_FailsSafely_WhenBothMoveEndsExist()
    {
        var x = AddFile(@"D:\Archive\2025\x.jpg", 300, 10);
        var y = AddFile(@"D:\Archive\2026\x.jpg", 300, 11);
        var op = Move("op1", @"D:\Archive\2025\x.jpg", @"D:\Archive\2026\x.jpg", x.Length);
        var journal = WriteJournal([op], ("op1", JournalEvent.Started, null));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("op1", failure.OpId);
        Assert.Equal("원본과 대상에 모두 파일이 있습니다. 확인이 필요합니다", failure.Message);
        Assert.Equal(x, _fs.ReadAllBytes(@"D:\Archive\2025\x.jpg"));
        Assert.Equal(y, _fs.ReadAllBytes(@"D:\Archive\2026\x.jpg"));
        Assert.Equal(OpStatus.Failed, StateOf(JournalReader.Load(_fs, journal), "op1").Status);
    }

    [Fact]
    public void Resume_ReconcilesStartedMoves()
    {
        var done = AddFile(@"D:\Archive\2026\done.jpg", 100, 12);   // 이미 옮겨짐
        var todo = AddFile(@"D:\Archive\2025\todo.jpg", 200, 13);   // 아직 안 옮겨짐
        _fs.AddDirectory(@"D:\Archive\2025");
        var ops = new[]
        {
            Move("op1", @"D:\Archive\2025\done.jpg", @"D:\Archive\2026\done.jpg", done.Length),
            Move("op2", @"D:\Archive\2025\todo.jpg", @"D:\Archive\2026\todo.jpg", todo.Length),
            Move("op3", @"D:\Archive\2025\gone.jpg", @"D:\Archive\2026\gone.jpg", 50),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op2", JournalEvent.Started, null), ("op3", JournalEvent.Started, null));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(2, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("op3", failure.OpId);
        Assert.Equal("파일을 찾을 수 없습니다", failure.Message);
        Assert.Equal(done, _fs.ReadAllBytes(@"D:\Archive\2026\done.jpg"));
        Assert.Equal(todo, _fs.ReadAllBytes(@"D:\Archive\2026\todo.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2025\todo.jpg"));

        var state = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op2").Status);
        Assert.Equal(OpStatus.Failed, StateOf(state, "op3").Status);
    }

    [Fact]
    public void Resume_FinishesSwap_ThroughTempName()
    {
        // 자리 바꾸기: P(14일) ↔ Q(15일). op1: 15일 → 임시 이름, op2: 14일 → 15일, op3: 임시 이름 → 14일.
        var p = Bytes(100, 14);
        var q = Bytes(120, 15);
        const string Day14 = @"D:\Archive\2026\14\P.JPG";
        const string Day15 = @"D:\Archive\2026\15\P.JPG";
        const string Temp = Day15 + ".photook-tmp-op1";
        _fs.AddFile(Day15, p);   // op2가 이미 옮겼다
        _fs.AddFile(Temp, q);    // op1이 임시 이름으로 옮겨 둔 파일
        _fs.AddDirectory(@"D:\Archive\2026\14");
        var ops = new[]
        {
            Move("op1", Day15, Temp, q.Length),
            Move("op2", Day14, Day15, p.Length),
            Move("op3", Temp, Day14, q.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, null),
            ("op2", JournalEvent.Started, null), ("op2", JournalEvent.Completed, null),
            ("op3", JournalEvent.Started, null));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(p, _fs.ReadAllBytes(Day15));
        Assert.Equal(q, _fs.ReadAllBytes(Day14));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void Resume_RetriesFailedSwapSecondLeg()
    {
        var q = Bytes(120, 16);
        const string Day14 = @"D:\Archive\2026\14\P.JPG";
        const string Temp = @"D:\Archive\2026\15\P.JPG.photook-tmp-op1";
        _fs.AddFile(Temp, q);
        _fs.AddDirectory(@"D:\Archive\2026\14");
        var op = Move("op3", Temp, Day14, q.Length);
        var journal = WriteJournal([op], ("op3", JournalEvent.Started, null), ("op3", JournalEvent.Failed, "파일 작업 중 오류가 발생했습니다"));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(q, _fs.ReadAllBytes(Day14));
        Assert.Empty(TempFiles());
    }

    // ------------------------------------------------------------------
    // 폴더·실패·드라이브
    // ------------------------------------------------------------------

    [Fact]
    public void Resume_CompletesStartedCreateDirectory()
    {
        _fs.AddDirectory(@"D:\Archive\2025");
        var ops = new[] { Mkdir("op1", @"D:\Archive\2025"), Mkdir("op2", @"D:\Archive\2026") };
        var journal = WriteJournal(ops, ("op1", JournalEvent.Started, null), ("op2", JournalEvent.Started, null));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.True(_fs.DirectoryExists(@"D:\Archive\2026"));
        var state = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op2").Status);
    }

    [Fact]
    public void Resume_RetriesFailedOps()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 17);
        var b = AddFile(@"E:\DCIM\b.jpg", 200, 18);
        _fs.AddDirectory(@"D:\Archive\2026");
        var ops = new[]
        {
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Failed, "파일 작업 중 오류가 발생했습니다"));

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        var state = JournalReader.Load(_fs, journal);
        Assert.True(state.Finished);
        Assert.Equal(Hash(@"E:\DCIM\a.jpg"), StateOf(state, "op1").Hash);
    }

    [Fact]
    public void Resume_ContinuesAfterSourceVolumeReturns()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 19);
        var b = AddFile(@"E:\DCIM\b.jpg", 200, 20);
        var c = AddFile(@"E:\DCIM\c.jpg", 300, 21);
        _fs.AddDirectory(@"D:\Archive\2026");
        _hooked.AfterMove = (_, destination) =>
        {
            if (destination.EndsWith(@"\a.jpg", StringComparison.OrdinalIgnoreCase))
            {
                _fs.RemoveVolume(@"E:\");
            }
        };

        var first = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", c.Length)));
        Assert.Equal(RunOutcome.Interrupted, first.Outcome);
        _hooked.AfterMove = null;

        _fs.RestoreVolume(@"E:\");
        var result = NewExecutor().Resume(first.JournalPath!);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(first.RunId, result.RunId);
        Assert.Equal(3, result.Succeeded);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        Assert.Equal(c, _fs.ReadAllBytes(@"D:\Archive\2026\c.jpg"));
        Assert.Empty(TempFiles());
        var state = JournalReader.Load(_fs, first.JournalPath!);
        Assert.True(state.Finished);
        Assert.All(state.Operations, o => Assert.Equal(OpStatus.Completed, o.Status));
    }

    [Fact]
    public void Resume_ContinuesAfterArchiveVolumeReturns()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 22);
        var b = AddFile(@"E:\DCIM\b.jpg", 200, 23);
        _fs.AddDirectory(@"D:\Archive\2026");
        _hooked.AfterMove = (_, destination) =>
        {
            if (destination.EndsWith(@"\a.jpg", StringComparison.OrdinalIgnoreCase))
            {
                _fs.RemoveVolume(@"D:\");
            }
        };

        var first = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));
        Assert.Equal(RunOutcome.Interrupted, first.Outcome);
        Assert.Equal(JournalPaths.InAppData(AppData, first.RunId), first.JournalPath);
        _hooked.AfterMove = null;

        _fs.RestoreVolume(@"D:\");
        var result = NewExecutor().Resume(first.JournalPath!);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        Assert.Empty(TempFiles());
        var state = JournalReader.Load(_fs, first.JournalPath!);
        Assert.True(state.Finished);
        Assert.All(state.Operations, o => Assert.Equal(OpStatus.Completed, o.Status));
    }

    // ------------------------------------------------------------------
    // 하지 않는 경우
    // ------------------------------------------------------------------

    [Fact]
    public void Resume_DoesNothing_WhenRunAlreadyFinished()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 24);
        _fs.AddDirectory(@"D:\Archive\2026");
        var first = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));
        var opsBefore = _fs.MutatingOps;
        var bytesBefore = _fs.ReadAllBytes(first.JournalPath!);

        var result = NewExecutor().Resume(first.JournalPath!);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(first.RunId, result.RunId);
        Assert.Empty(result.Failures);
        Assert.Equal(opsBefore, _fs.MutatingOps);
        Assert.Equal(bytesBefore, _fs.ReadAllBytes(first.JournalPath!));
    }

    [Fact]
    public void Resume_StopsBeforeFirstOp_WhenCancelled()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 25);
        _fs.AddDirectory(@"D:\Archive\2026");
        var journal = WriteJournal([Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)]);
        var opsBefore = _fs.MutatingOps;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = NewExecutor().Resume(journal, cts.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.Equal(opsBefore, _fs.MutatingOps);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
    }

    [Fact]
    public void Resume_Refuses_AfterUndoThatWroteOnlyUndoStarted()
    {
        // 첫 복사를 마친 뒤 취소된 실행. 원본 드라이브를 뺀 채 되돌리기를 하면 모든 사유가 일시적이라 작업 기록은 남지 않는다.
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 28);
        var b = AddFile(@"E:\DCIM\b.jpg", 100, 29);
        _fs.AddDirectory(@"D:\Archive\2026");
        using var cts = new CancellationTokenSource();
        _hooked.AfterMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
            }
        };
        var run = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)), cts.Token);
        _hooked.AfterMove = null;
        Assert.Equal(RunOutcome.Cancelled, run.Outcome);
        var countBefore = Entries(run.JournalPath!).Count;

        _fs.RemoveVolume(@"E:\");
        var undo = new Undoer(new ExecutionContext(_hooked, new XxHasher(_hooked), AppData, () => Now)).Undo(run.JournalPath!);
        _fs.RestoreVolume(@"E:\");

        Assert.Equal(0, undo.Undone);
        var entries = Entries(run.JournalPath!);
        Assert.Equal(countBefore + 1, entries.Count);
        Assert.Equal(JournalEvent.UndoStarted, entries[^1].Event);
        Assert.True(JournalReader.Load(_fs, run.JournalPath!).UndoStarted);
        var opsBefore = _fs.MutatingOps;

        var ex = Assert.Throws<InvalidOperationException>(() => NewExecutor().Resume(run.JournalPath!));

        Assert.Equal("되돌리기를 시작한 기록은 이어 할 수 없습니다", ex.Message);
        Assert.Equal(opsBefore, _fs.MutatingOps);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\b.jpg"));
    }

    [Fact]
    public void Resume_Refuses_RunThatWasBeingUndone()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 26);
        var ops = new[]
        {
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", 10),
        };
        var journal = WriteJournal(ops,
            ("op1", JournalEvent.Started, null), ("op1", JournalEvent.Completed, null), ("op1", JournalEvent.Undone, null));
        var opsBefore = _fs.MutatingOps;

        var ex = Assert.Throws<InvalidOperationException>(() => NewExecutor().Resume(journal));

        Assert.Equal("되돌리기를 시작한 기록은 이어 할 수 없습니다", ex.Message);
        Assert.Equal(opsBefore, _fs.MutatingOps);
    }

    [Fact]
    public void Resume_AppendsOnlyToChosenCopy_WhenCopiesAgree()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 27);
        _fs.AddDirectory(@"D:\Archive\2026");
        var journal = WriteJournal([Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)]);
        var journalsBefore = _fs.AllFiles().Count(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
        var appDataBefore = _fs.ReadAllBytes(AppDataJournal);

        var result = NewExecutor().Resume(AppDataJournal);

        Assert.Equal(ArchiveJournal, result.JournalPath); // 길이가 같으면 [아카이브, 앱 데이터] 순서의 첫 사본
        Assert.Empty(result.Warnings);
        Assert.Equal(journalsBefore, _fs.AllFiles().Count(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(appDataBefore, _fs.ReadAllBytes(AppDataJournal));
        Assert.Equal(journal, result.JournalPath);
        var entries = Entries(ArchiveJournal);
        Assert.All(entries, e => Assert.Equal(RunId, e.RunId));
        Assert.Single(entries, e => e.Event == JournalEvent.RunStarted);
        Assert.Equal(JournalEvent.RunFinished, entries[^1].Event);
    }

    [Fact]
    public void Resume_DoesNotStartHeadlessJournal_WhereNoneExisted()
    {
        // 처음 실행 때 아카이브 쪽 기록을 못 남겼다면, 이어 하기가 시작 정보 없는 기록 파일을 새로 만들면 안 된다.
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 28);
        _fs.AddDirectory(@"D:\Archive\2026");
        var op = Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length);
        var writer = new JournalWriter(_fs, [AppDataJournal]);
        writer.Write(new JournalEntry(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now));
        writer.Write(new JournalEntry(RunId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
            TimestampUtc: Now));

        var result = NewExecutor().Resume(AppDataJournal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(AppDataJournal, result.JournalPath);
        Assert.False(_fs.FileExists(ArchiveJournal));
        Assert.True(JournalReader.Load(_fs, AppDataJournal).Finished);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
    }

    // ------------------------------------------------------------------
    // 계획 기록 완결, 멱등성, 기록 사본 고르기
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, false)] // RunStarted만 디스크에 닿고 끊김 → 계획 0개
    [InlineData(1, false)] // 첫 Planned까지 닿고 끊김 → 계획 1개
    [InlineData(1, true)]  // 둘째 Planned를 쓰다 끊겨 마지막 줄이 잘림 → 계획 1개
    public void Resume_RefusesPlanIncomplete_WhenPlanBatchWasTorn(int plannedWritten, bool tornLastLine)
    {
        // 실제 디스크에서는 계획 묶음(한 번에 덧붙이고 한 번 flush)을 쓰다 끊기면 앞의 몇 줄만 남을 수 있다.
        // 가짜 디스크의 묶음 쓰기는 원자적이라, 그렇게 남은 기록을 직접 만든다.
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 29);
        var b = AddFile(@"E:\DCIM\b.jpg", 200, 30);
        var c = AddFile(@"E:\DCIM\c.jpg", 300, 31);
        _fs.AddDirectory(@"D:\Archive\2026");
        PlannedOperation[] ops =
        [
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", c.Length),
        ];
        var writer = new JournalWriter(_fs, [ArchiveJournal, AppDataJournal]);
        writer.WriteMany(ops.Take(plannedWritten)
            .Select(op => new JournalEntry(RunId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
                TimestampUtc: Now))
            .Prepend(new JournalEntry(RunId, JournalEvent.RunStarted, Length: ops.Length, Destination: Archive, TimestampUtc: Now)));
        if (tornLastLine)
        {
            foreach (var path in new[] { ArchiveJournal, AppDataJournal })
            {
                _fs.AddFile(path, [.. _fs.ReadAllBytes(path), .. Encoding.UTF8.GetBytes("{\"runId\":\"" + RunId + "\",\"event\":\"Plan")]);
            }
        }

        var journals = _fs.AllFiles().Where(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, journals.Count);
        var journal = journals.Single(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        var before = JournalReader.Load(_fs, journal);
        Assert.Equal(3, before.ExpectedOperations);
        Assert.Equal(plannedWritten, before.Operations.Count);
        var bytesBefore = journals.Select(_fs.ReadAllBytes).ToList();
        var opsBefore = _fs.MutatingOps;

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.PlanIncomplete, result.Outcome);
        Assert.Equal(before.RunId, result.RunId);
        Assert.Equal(0, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "계획 기록이 끝나지 않아 이어 할 수 없습니다. 다시 계획해 주세요" }, result.Warnings);
        Assert.Equal(opsBefore, _fs.MutatingOps);
        Assert.Equal(bytesBefore, journals.Select(_fs.ReadAllBytes).ToList());
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void Resume_IsIdempotent_WhenFirstResumeHitMissingDrive_ForCopy()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 500, 32);
        _fs.AddFile(@"D:\Archive\2026\a.jpg", a); // 복사는 끝났고 Completed만 못 썼다
        var journal = WriteJournal([Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)],
            ("op1", JournalEvent.Started, null));
        _fs.RemoveVolume(@"E:\");

        var first = NewExecutor().Resume(journal);
        Assert.Equal(RunOutcome.Interrupted, first.Outcome);
        Assert.Equal(OpStatus.Failed, StateOf(JournalReader.Load(_fs, journal), "op1").Status);

        _fs.RestoreVolume(@"E:\");
        var second = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, second.Outcome);
        Assert.Empty(second.Failures);
        Assert.Empty(_hooked.Created);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        var state = JournalReader.Load(_fs, journal);
        Assert.True(state.Finished);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(Hash(@"E:\DCIM\a.jpg"), StateOf(state, "op1").Hash);
    }

    [Fact]
    public void Resume_IsIdempotent_WhenFirstResumeHitMissingDrive_ForMove()
    {
        var x = AddFile(@"D:\Archive\2026\x.jpg", 300, 33); // 이동은 끝났고 Completed만 못 썼다
        _fs.AddDirectory(@"D:\Archive\2025");
        var op = Move("op1", @"D:\Archive\2025\x.jpg", @"D:\Archive\2026\x.jpg", x.Length);
        WriteJournal([op], ("op1", JournalEvent.Started, null));
        _fs.RemoveVolume(@"D:\");

        var first = NewExecutor().Resume(AppDataJournal);
        Assert.Equal(RunOutcome.Interrupted, first.Outcome);
        Assert.Equal("보관 드라이브가 연결되어 있지 않습니다", Assert.Single(first.Failures).Message);
        Assert.Equal(OpStatus.Failed, StateOf(JournalReader.Load(_fs, AppDataJournal), "op1").Status);

        _fs.RestoreVolume(@"D:\");
        var second = NewExecutor().Resume(AppDataJournal);

        Assert.Equal(RunOutcome.Completed, second.Outcome);
        Assert.Empty(second.Failures);
        Assert.Equal(x, _fs.ReadAllBytes(@"D:\Archive\2026\x.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2025\x.jpg"));
        var state = JournalReader.Load(_fs, AppDataJournal);
        Assert.True(state.Finished);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
    }

    [Fact]
    public void Resume_UsesLongerJournalCopy()
    {
        var a = AddFile(@"E:\DCIM\a.jpg", 100, 34);
        var b = AddFile(@"E:\DCIM\b.jpg", 200, 35);
        var c = AddFile(@"E:\DCIM\c.jpg", 300, 36);
        _fs.AddDirectory(@"D:\Archive\2026");
        _hooked.AfterMove = (_, destination) =>
        {
            if (destination.EndsWith(@"\a.jpg", StringComparison.OrdinalIgnoreCase))
            {
                _fs.RemoveVolume(@"D:\"); // 아카이브 쪽 기록은 여기서 멈춘다
            }
        };

        var first = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", c.Length)));
        _hooked.AfterMove = null;
        _fs.RestoreVolume(@"D:\");

        var lagging = JournalPaths.InArchive(Archive, first.RunId);
        var longer = JournalPaths.InAppData(AppData, first.RunId);
        Assert.True(Entries(lagging).Count < Entries(longer).Count);
        var laggingBefore = _fs.ReadAllBytes(lagging);

        var result = NewExecutor().Resume(lagging);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(longer, result.JournalPath);
        Assert.Contains($"기록 사본이 서로 달라 더 긴 기록을 사용했습니다: {longer}", result.Warnings);
        Assert.Equal(laggingBefore, _fs.ReadAllBytes(lagging)); // 뒤처진 사본에는 이어 쓰지 않는다
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        Assert.Equal(c, _fs.ReadAllBytes(@"D:\Archive\2026\c.jpg"));

        // 고른 사본에는 빈 곳이 없다: 작업마다 Started 뒤에 결과가 있고, 끝은 RunFinished.
        var entries = Entries(longer);
        Assert.Equal(JournalEvent.RunFinished, entries[^1].Event);
        foreach (var opId in new[] { "op1", "op2", "op3" })
        {
            var events = entries.Where(e => e.OpId == opId && e.Event != JournalEvent.Planned).Select(e => e.Event).ToList();
            Assert.Equal(JournalEvent.Started, events[0]);
            Assert.Equal(JournalEvent.Completed, events[^1]);
        }

        var state = JournalReader.Load(_fs, longer);
        Assert.True(state.Finished);
        Assert.All(state.Operations, o => Assert.Equal(OpStatus.Completed, o.Status));
        Assert.Equal(Hash(@"E:\DCIM\a.jpg"), StateOf(state, "op1").Hash);
    }
}
