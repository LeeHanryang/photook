using System.IO;
using System.Text;
using System.Text.Json;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.Execution;

public class ExecutorTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly HookedFileSystem _hooked;

    public ExecutorTests()
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

    private byte[] AddSource(string path, int length, int seed)
    {
        var data = Bytes(length, seed);
        _fs.AddFile(path, data);
        return data;
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

    // ------------------------------------------------------------------
    // 테스트
    // ------------------------------------------------------------------

    [Fact]
    public void Execute_CopiesFiles_AndLeavesSourcesUntouched()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 3 * 1024 * 1024 + 17, 1); // 버퍼보다 큰 파일
        var b = AddSource(@"E:\DCIM\b.jpg", 1000, 2);
        var sourcesBefore = _fs.AllFiles().Where(p => p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase)).ToList();

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op3", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"E:\DCIM\b.jpg"));
        Assert.Equal(sourcesBefore,
            _fs.AllFiles().Where(p => p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase)).ToList());
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        Assert.Empty(TempFiles());
        Assert.False(_fs.DirectoryExists(@"D:\Archive\.photook\되돌림"));
    }

    [Fact]
    public void Execute_JournalsPlannedFirst_ThenStartedCompleted_WithSourceHash_AndRunFinished()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 5000, 3);

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        var entries = Entries(result.JournalPath!);
        Assert.Equal(
            new[]
            {
                JournalEvent.RunStarted, JournalEvent.Planned, JournalEvent.Planned,
                JournalEvent.Started, JournalEvent.Completed, JournalEvent.Started, JournalEvent.Completed,
                JournalEvent.RunFinished,
            },
            entries.Select(e => e.Event));
        Assert.All(entries, e => Assert.Equal(result.RunId, e.RunId));
        Assert.All(entries, e => Assert.Equal(Now, e.TimestampUtc));
        Assert.Equal(Archive, entries[0].Destination);

        var planned = entries[2];
        Assert.Equal("op2", planned.OpId);
        Assert.Equal(OperationKind.Copy, planned.Kind);
        Assert.Equal(@"E:\DCIM\a.jpg", planned.Source);
        Assert.Equal(@"D:\Archive\2026\a.jpg", planned.Destination);
        Assert.Equal(a.Length, planned.Length);

        var copyCompleted = entries[6];
        Assert.Equal("op2", copyCompleted.OpId);
        Assert.Equal(new XxHasher(_fs).Hash(@"E:\DCIM\a.jpg"), copyCompleted.Hash);
        Assert.Equal(2, entries[0].Length); // RunStarted에 계획한 작업 수

        var state = JournalReader.Load(_fs, result.JournalPath!);
        Assert.Equal(2, state.ExpectedOperations);
        Assert.Equal(entries.Count, state.EntryCount);
        Assert.True(state.Finished);
        Assert.All(state.Operations, o => Assert.Equal(OpStatus.Completed, o.Status));
    }

    [Fact]
    public void Execute_FailsAndQuarantinesTemp_WhenVerificationFails()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 2000, 4);
        _fs.AddDirectory(@"D:\Archive\2026");
        _fs.CorruptNextWrite();

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(0, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("op1", failure.OpId);
        Assert.Equal("복사 검증 실패", failure.Message);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
        Assert.Empty(TempFiles());
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));

        var quarantined = new SafeDelete(_fs, Archive, result.RunId).QuarantineRoot + @"\2026\a.jpg.photook-tmp";
        Assert.True(_fs.FileExists(quarantined));
        Assert.NotEqual(a, _fs.ReadAllBytes(quarantined));

        var failed = Entries(result.JournalPath!).Single(e => e.Event == JournalEvent.Failed);
        Assert.Equal("op1", failed.OpId);
        Assert.Equal("복사 검증 실패", failed.Message);
    }

    [Fact]
    public void Execute_ContinuesAfterSingleFailure()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 5);
        var b = AddSource(@"E:\DCIM\b.jpg", 200, 6);
        _fs.AddDirectory(@"D:\Archive\2026");
        _fs.FailReadsOf(@"E:\DCIM\a.jpg");

        var result = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(1, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("op1", failure.OpId);
        Assert.Equal(@"E:\DCIM\a.jpg", failure.Source);
        Assert.Equal(@"D:\Archive\2026\a.jpg", failure.Destination);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"D:\Archive\2026\b.jpg"));
        Assert.Empty(TempFiles());

        var state = JournalReader.Load(_fs, result.JournalPath!);
        Assert.Equal(OpStatus.Failed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op2").Status);
        Assert.True(state.Finished);
    }

    [Fact]
    public void Execute_Interrupts_WhenSourceVolumeRemoved()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 7);
        var b = AddSource(@"E:\DCIM\b.jpg", 200, 8);
        var c = AddSource(@"E:\DCIM\c.jpg", 300, 9);
        _fs.AddDirectory(@"D:\Archive\2026");
        _hooked.AfterMove = (_, destination) =>
        {
            if (destination.EndsWith(@"\a.jpg", StringComparison.OrdinalIgnoreCase))
            {
                _fs.RemoveVolume(@"E:\");
            }
        };

        var result = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", c.Length)));

        Assert.Equal(RunOutcome.Interrupted, result.Outcome);
        Assert.Equal(1, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("op2", failure.OpId);
        Assert.Equal("원본 드라이브가 연결되어 있지 않습니다", failure.Message);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\b.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\c.jpg"));
        Assert.Empty(TempFiles());
        Assert.Equal(b, _fs.ReadAllBytes(@"E:\DCIM\b.jpg"));

        var state = JournalReader.Load(_fs, result.JournalPath!);
        Assert.False(state.Finished);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Failed, StateOf(state, "op2").Status);
        Assert.Equal(OpStatus.Pending, StateOf(state, "op3").Status);
        Assert.Equal("원본 드라이브가 연결되어 있지 않습니다",
            Entries(result.JournalPath!).Single(e => e.Event == JournalEvent.Failed).Message);
    }

    [Fact]
    public void Execute_Cancels_BetweenOperations()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 10);
        var b = AddSource(@"E:\DCIM\b.jpg", 200, 11);
        _fs.AddDirectory(@"D:\Archive\2026");
        using var cts = new CancellationTokenSource();
        _hooked.AfterMove = (_, _) => cts.Cancel(); // 첫 작업의 마지막 단계(임시 이름 → 진짜 이름) 직후

        var result = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)), cts.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, result.Succeeded);
        Assert.Empty(result.Failures);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\b.jpg"));

        var state = JournalReader.Load(_fs, result.JournalPath!);
        Assert.False(state.Finished); // 이어 하기 대상으로 남는다
        Assert.Equal(OpStatus.Completed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Pending, StateOf(state, "op2").Status);
    }

    [Fact]
    public void Execute_IsBlocked_ByPreflightErrors()
    {
        AddSource(@"E:\DCIM\a.jpg", 100, 12);
        _fs.SetFreeSpace(@"D:\", 0);
        var filesBefore = _fs.AllFiles();

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", 100)));

        Assert.Equal(RunOutcome.BlockedByPreflight, result.Outcome);
        Assert.Null(result.JournalPath);
        Assert.Equal(0, result.Succeeded);
        Assert.Contains(result.PreflightIssues, i => i.Severity == Severity.Error);
        Assert.Equal(filesBefore, _fs.AllFiles());
        Assert.Equal(0, _fs.MutatingOps);
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
    }

    [Fact]
    public void Execute_WritesRunStartedAndAllPlanned_InOneBatchPerJournalCopy()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 21);
        var b = AddSource(@"E:\DCIM\b.jpg", 200, 22);

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op3", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length)));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        var archiveJournal = JournalPaths.InArchive(Archive, result.RunId);
        var appDataJournal = JournalPaths.InAppData(AppData, result.RunId);
        // 계획 단계: 사본마다 한 번에 4줄(RunStarted + Planned 3개). 그 뒤의 작업 기록은 한 줄씩(선기록 계약).
        Assert.Equal([(archiveJournal, 4), (appDataJournal, 4)], _hooked.Appends.Take(2));
        Assert.All(_hooked.Appends.Skip(2), x => Assert.Equal(1, x.Lines));
        Assert.Equal(2 + (3 * 2 * 2) + 2, _hooked.Appends.Count);
        var entries = Entries(archiveJournal);
        Assert.Equal(JournalEvent.RunStarted, entries[0].Event);
        Assert.Equal(3, entries[0].Length);
        Assert.Equal(["op1", "op2", "op3"], entries.Skip(1).Take(3).Select(e => e.OpId));
        Assert.All(entries.Skip(1).Take(3), e => Assert.Equal(JournalEvent.Planned, e.Event));
    }

    [Fact]
    public void Execute_Copy_KeepsSourceLastWriteTime_SetOnTempAfterVerifyBeforeRename()
    {
        var taken = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var a = Bytes(3000, 23);
        _fs.AddFile(@"E:\DCIM\a.jpg", a, taken);
        _fs.NowUtc = Now; // 새로 쓰는 파일의 시각은 원본과 다르다
        var timeAtRename = (DateTime?)null;
        _hooked.BeforeMove = (source, _) => timeAtRename = _fs.GetFile(source).LastWriteTimeUtc;

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(taken, _fs.GetFile(@"D:\Archive\a.jpg").LastWriteTimeUtc);
        Assert.Equal(taken, _fs.GetFile(@"E:\DCIM\a.jpg").LastWriteTimeUtc);
        Assert.Equal(taken, timeAtRename); // 이름을 붙이기 전에 임시 파일에 시각을 넣었다
        Assert.Equal([@"D:\Archive\a.jpg.photook-tmp"], _hooked.TimesSet);
    }

    [Fact]
    public void Execute_Copy_DoesNotSetTime_WhenVerificationFails()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 3000, 24);
        _fs.CorruptNextWrite();

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Empty(_hooked.TimesSet);
    }

    [Fact]
    public void Execute_DoesNotAllocateACopyBufferPerOperation()
    {
        // 1 MiB 버퍼를 작업마다 새로 만들면 작업 수만큼 큰 객체 힙에 쌓인다. 빌려 쓰고 돌려줘야 한다.
        const int files = 30;
        var warmup = AddSource(@"E:\DCIM\warmup.jpg", 100, 25);
        NewExecutor().Execute(PlanOf(Copy("w1", @"E:\DCIM\warmup.jpg", @"D:\Archive\warmup.jpg", warmup.Length)));
        var ops = new List<PlannedOperation>();
        for (var i = 0; i < files; i++)
        {
            var data = AddSource($@"E:\DCIM\{i}.jpg", 100, 100 + i);
            ops.Add(Copy($"op{i}", $@"E:\DCIM\{i}.jpg", $@"D:\Archive\{i}.jpg", data.Length));
        }

        var executor = NewExecutor();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = executor.Execute(PlanOf([.. ops]));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.True(allocated < files * 1024L * 1024 / 2, $"복사 {files}개에 {allocated:N0} 바이트를 할당했습니다");
    }

    [Fact]
    public void Execute_RecordsFailure_ForNotSupportedAndArgumentExceptions_AndFinishesRun()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 26);
        var b = AddSource(@"E:\DCIM\b.jpg", 200, 27);
        var c = AddSource(@"E:\DCIM\c.jpg", 300, 28);
        _hooked.BeforeMove = (_, destination) =>
        {
            // 검증을 마친 임시 파일에 진짜 이름을 붙이는 이동에서만 던진다(격리 이동은 그대로 된다).
            if (string.Equals(destination, @"D:\Archive\a.jpg", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("지원하지 않는 경로 형식");
            }
        };

        var result = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"relative\b.jpg", b.Length), // 잘못된 경로(예: 사용자 프리셋에서 온 것)
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\c.jpg", c.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal(["op1", "op2"], result.Failures.Select(f => f.OpId));
        Assert.All(result.Failures, f => Assert.StartsWith("파일 작업 중 오류가 발생했습니다", f.Message));
        Assert.Equal(1, result.Succeeded);
        Assert.False(_fs.FileExists(@"D:\Archive\a.jpg"));
        Assert.Empty(TempFiles()); // 이름을 붙이지 못한 임시 파일은 격리했다
        Assert.Equal(c, _fs.ReadAllBytes(@"D:\Archive\c.jpg"));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes(@"E:\DCIM\b.jpg"));
        var state = JournalReader.Load(_fs, result.JournalPath!);
        Assert.True(state.Finished);
        Assert.Equal(OpStatus.Failed, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Failed, StateOf(state, "op2").Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op3").Status);
    }

    [Fact]
    public void Execute_WritesJournal_ToBothLocations()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 13);

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        var inArchive = JournalPaths.InArchive(Archive, result.RunId);
        var inAppData = JournalPaths.InAppData(AppData, result.RunId);
        Assert.Equal(inArchive, result.JournalPath);
        Assert.Equal(_fs.ReadAllBytes(inArchive), _fs.ReadAllBytes(inAppData));
        Assert.Empty(result.Warnings);
        Assert.Matches(@"^\d{8}-\d{6}-[0-9a-f]{4}$", result.RunId);
    }

    [Fact]
    public void Execute_ReportsAppDataJournal_WhenArchiveJournalFails()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 14);
        _fs.AddFile(@"D:\Archive\.photook", new byte[] { 1 }); // 폴더를 만들 수 없게 막는다

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(JournalPaths.InAppData(AppData, result.RunId), result.JournalPath);
        Assert.Contains(result.Warnings, w => w.Contains(JournalPaths.InArchive(Archive, result.RunId)));
        Assert.True(JournalReader.Load(_fs, result.JournalPath!).Finished);
    }

    [Fact]
    public void Execute_Throws_WhenNoJournalCanBeWritten()
    {
        AddSource(@"E:\DCIM\a.jpg", 100, 15);
        _fs.AddFile(@"D:\Archive\.photook", new byte[] { 1 });
        _fs.RemoveVolume(@"C:\");

        Assert.Throws<JournalUnavailableException>(() => NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", 100))));
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026"));
    }

    [Fact]
    public void Execute_SkipsExistingDirectories()
    {
        _fs.AddDirectory(@"D:\Archive\2026");

        var result = NewExecutor().Execute(PlanOf(
            Mkdir("op1", @"D:\Archive\2026"),
            Mkdir("op2", @"D:\Archive\2026\10")));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.True(_fs.DirectoryExists(@"D:\Archive\2026\10"));

        var entries = Entries(result.JournalPath!);
        var skipped = Assert.Single(entries, e => e.Event == JournalEvent.Skipped);
        Assert.Equal("op1", skipped.OpId);
        Assert.False(string.IsNullOrEmpty(skipped.Message));
        Assert.Contains(entries, e => e.Event == JournalEvent.Completed && e.OpId == "op2");

        var state = JournalReader.Load(_fs, result.JournalPath!);
        Assert.Equal(OpStatus.Skipped, StateOf(state, "op1").Status);
        Assert.Equal(OpStatus.Completed, StateOf(state, "op2").Status);
    }

    [Fact]
    public void Execute_MovesWithinVolume()
    {
        var x = Bytes(300, 16);
        _fs.AddFile(@"D:\Archive\2025\x.jpg", x);
        _fs.AddDirectory(@"D:\Archive\2026");

        var result = NewExecutor().Execute(PlanOf(Move("op1", @"D:\Archive\2025\x.jpg", @"D:\Archive\2026\x.jpg", x.Length)));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.False(_fs.FileExists(@"D:\Archive\2025\x.jpg"));
        Assert.Equal(x, _fs.ReadAllBytes(@"D:\Archive\2026\x.jpg"));
    }

    [Fact]
    public void Execute_FailsMove_WhenSizeDiffersFromPlan_AndLeavesFileAlone()
    {
        var x = Bytes(300, 17);
        _fs.AddFile(@"D:\Archive\2025\x.jpg", x);
        _fs.AddDirectory(@"D:\Archive\2026");

        var result = NewExecutor().Execute(PlanOf(Move("op1", @"D:\Archive\2025\x.jpg", @"D:\Archive\2026\x.jpg", 999)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal("op1", Assert.Single(result.Failures).OpId);
        Assert.Equal(x, _fs.ReadAllBytes(@"D:\Archive\2025\x.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\x.jpg"));
    }

    [Fact]
    public void Execute_RefusesCrossVolumeMove()
    {
        var x = AddSource(@"E:\DCIM\x.jpg", 300, 25);

        var result = NewExecutor().Execute(PlanOf(Move("op1", @"E:\DCIM\x.jpg", @"D:\Archive\x.jpg", x.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal("다른 드라이브로는 옮기지 않습니다", Assert.Single(result.Failures).Message);
        Assert.Equal(x, _fs.ReadAllBytes(@"E:\DCIM\x.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\x.jpg"));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void Execute_UsesKoreanMessage_ForIoFailure()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 26);
        _fs.AddDirectory(@"D:\Archive\2026");
        _fs.FailReadsOf(@"E:\DCIM\a.jpg");

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        var message = Assert.Single(result.Failures).Message;
        Assert.StartsWith("파일 작업 중 오류가 발생했습니다 — ", message);
        Assert.Equal(message, Entries(result.JournalPath!).Single(e => e.Event == JournalEvent.Failed).Message);
    }

    [Fact]
    public void Execute_UsesKoreanMessage_ForMissingParentFolder()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 27);

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        Assert.StartsWith("폴더를 찾을 수 없습니다", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public void Execute_Interrupts_WhenArchiveVolumeRemoved()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 28);
        var b = AddSource(@"E:\DCIM\b.jpg", 200, 29);
        var c = AddSource(@"E:\DCIM\c.jpg", 300, 30);
        _fs.AddDirectory(@"D:\Archive\2026");
        _hooked.AfterMove = (_, destination) =>
        {
            if (destination.EndsWith(@"\a.jpg", StringComparison.OrdinalIgnoreCase))
            {
                _fs.RemoveVolume(@"D:\");
            }
        };

        var result = NewExecutor().Execute(PlanOf(
            Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length),
            Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", b.Length),
            Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", c.Length)));

        Assert.Equal(RunOutcome.Interrupted, result.Outcome);
        Assert.Equal(1, result.Succeeded);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("op2", failure.OpId);
        Assert.Equal("보관 드라이브가 연결되어 있지 않습니다", failure.Message);

        var appDataJournal = JournalPaths.InAppData(AppData, result.RunId);
        Assert.Equal(appDataJournal, result.JournalPath);
        var entries = Entries(appDataJournal);
        Assert.DoesNotContain(entries, e => e.Event == JournalEvent.RunFinished);
        Assert.Equal("보관 드라이브가 연결되어 있지 않습니다", entries.Single(e => e.Event == JournalEvent.Failed).Message);
        var state = JournalReader.Load(_fs, appDataJournal);
        Assert.False(state.Finished);
        Assert.Equal(OpStatus.Pending, StateOf(state, "op3").Status);

        _fs.RestoreVolume(@"D:\");
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\b.jpg"));
        Assert.Empty(TempFiles());
        Assert.Equal(b, _fs.ReadAllBytes(@"E:\DCIM\b.jpg"));
    }

    [Fact]
    public void Execute_FailsCopy_WhenDestinationAlreadyExists_AndLeavesItAlone()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 18);
        var existing = Bytes(50, 19);
        _fs.AddFile(@"D:\Archive\2026\a.jpg", existing);

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal("대상에 같은 이름의 파일이 생겼습니다", Assert.Single(result.Failures).Message);
        Assert.Equal(existing, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public void Execute_FailsCopy_WhenDestinationAppearsBeforeRename_AndQuarantinesTemp()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 20);
        _fs.AddDirectory(@"D:\Archive\2026");
        var intruder = Bytes(50, 21);
        _hooked.BeforeMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                _fs.AddFile(@"D:\Archive\2026\a.jpg", intruder);
            }
        };

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.Equal("대상에 같은 이름의 파일이 생겼습니다", Assert.Single(result.Failures).Message);
        Assert.Equal(intruder, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Empty(TempFiles());
        var quarantined = new SafeDelete(_fs, Archive, result.RunId).QuarantineRoot + @"\2026\a.jpg.photook-tmp";
        Assert.Equal(a, _fs.ReadAllBytes(quarantined));
    }

    [Fact]
    public void Execute_QuarantinesStaleTemp_BeforeCopying()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 22);
        var stale = Bytes(40, 23);
        _fs.AddFile(@"D:\Archive\2026\a.jpg.photook-tmp", stale);

        var result = NewExecutor().Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length)));

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(a, _fs.ReadAllBytes(@"D:\Archive\2026\a.jpg"));
        Assert.Empty(TempFiles());
        var quarantined = new SafeDelete(_fs, Archive, result.RunId).QuarantineRoot + @"\2026\a.jpg.photook-tmp";
        Assert.Equal(stale, _fs.ReadAllBytes(quarantined));
    }

    [Fact]
    public void Execute_PropagatesSimulatedCrash_LeavingResumableJournal()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 24);
        _fs.AddDirectory(@"D:\Archive\2026");
        var executor = NewExecutor();
        // 기록 2곳 × (RunStarted, Planned, Started) + 폴더 준비 2번 = 8번까지 성공하고, 복사를 시작하면 죽는다.
        _fs.CrashAfter(8);

        Assert.Throws<SimulatedCrashException>(() =>
            executor.Execute(PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", a.Length))));
        _fs.ClearFault();

        var journal = _fs.AllFiles().Single(p => p.Contains(@"\.photook\journal\", StringComparison.OrdinalIgnoreCase));
        var state = JournalReader.Load(_fs, journal);
        Assert.False(state.Finished);
        Assert.Equal(OpStatus.Started, StateOf(state, "op1").Status);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(a, _fs.ReadAllBytes(@"E:\DCIM\a.jpg"));
    }
}
