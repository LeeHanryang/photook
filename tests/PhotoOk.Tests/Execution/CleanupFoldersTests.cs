using System.IO;
using System.Text;
using System.Text.Json;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.Execution;

/// <summary>이동으로 비게 된 폴더 치우기. 고른 폴더는 남기고, 옮긴 파일이 있던 폴더와 그 위만 비어 있을 때 치운다.</summary>
public class CleanupFoldersTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string Picked = @"D:\Archive\촬영";
    private const string Target = @"D:\Archive\2026";
    private const string RunId = "20261027-103000-ab12";
    private const string TimestampMessage = @"원본 날짜를 유지하지 못했습니다: D:\Archive\2026\a.jpg";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly HookedFileSystem _hooked;

    public CleanupFoldersTests()
    {
        _hooked = new HookedFileSystem(_fs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
        _fs.AddDirectory(Target);
        _fs.AddDirectory(Picked);
    }

    // ------------------------------------------------------------------
    // 도우미
    // ------------------------------------------------------------------

    private ExecutionContext Context => new(_hooked, new XxHasher(_hooked), AppData, () => Now);

    private Executor NewExecutor() => new(Context);

    private Undoer NewUndoer() => new(Context);

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private byte[] Add(string path, int seed, int length = 100)
    {
        var data = Bytes(length, seed);
        _fs.AddFile(path, data);
        return data;
    }

    /// <summary>파일을 <see cref="Target"/>으로 옮기는 작업. 이름은 작업 ID를 붙여 겹치지 않게 한다.</summary>
    private PlannedOperation MoveOut(string id, string source) =>
        new(id, OperationKind.Move, source, Path.Combine(Target, $"{id}-{Path.GetFileName(source)}"), _fs.GetFile(source).Length, []);

    private static Plan PlanOf(IReadOnlyList<string>? roots, params PlannedOperation[] ops) => new(Archive, ops, [], [], roots);

    private List<JournalEntry> Entries(string path) =>
        Encoding.UTF8.GetString(_fs.ReadAllBytes(path))
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<JournalEntry>(line, JournalWriter.JsonOptions)!)
            .ToList();

    private List<string> RemovedFolders(string journal) =>
        Entries(journal).Where(e => e.Event == JournalEvent.FolderRemoved).Select(e => e.Destination!).ToList();

    private bool IsFinished(string journal) => JournalReader.Load(_fs, journal).Finished;

    private static string Journal(ExecutionResult result) => result.JournalPath!;

    // ------------------------------------------------------------------
    // 무엇을 치우는가
    // ------------------------------------------------------------------

    [Fact]
    public void Reorganize_RemovesEmptiedFolders()
    {
        var a = Add($@"{Picked}\old\sub\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.False(_fs.DirectoryExists($@"{Picked}\old\sub"));
        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.True(_fs.DirectoryExists(Picked));
        Assert.Equal(a, _fs.ReadAllBytes($@"{Target}\op1-a.jpg"));
        Assert.Equal([$@"{Picked}\old\sub", $@"{Picked}\old"], RemovedFolders(Journal(result)));

        // 치운 뒤에 끝맺는다. 그래야 끊겨도 이어 하기가 다시 치운다.
        var entries = Entries(Journal(result));
        Assert.Equal(JournalEvent.RunFinished, entries[^1].Event);
    }

    [Fact]
    public void KeepsFolderWithOtherFiles()
    {
        Add($@"{Picked}\old\sub\a.jpg", 1);
        var memo = Add($@"{Picked}\old\sub\memo.txt", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.Equal(memo, _fs.ReadAllBytes($@"{Picked}\old\sub\memo.txt"));
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void KeepsParent_WhenItHoldsOtherFiles()
    {
        Add($@"{Picked}\old\sub\a.jpg", 1);
        var memo = Add($@"{Picked}\old\memo.txt", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.False(_fs.DirectoryExists($@"{Picked}\old\sub"));
        Assert.Equal(memo, _fs.ReadAllBytes($@"{Picked}\old\memo.txt"));
        Assert.Equal([$@"{Picked}\old\sub"], RemovedFolders(Journal(result)));
    }

    [Fact]
    public void RemovesSharedParent_AfterAllBranchesEmptied()
    {
        Add($@"{Picked}\old\x\a.jpg", 1);
        Add($@"{Picked}\old\y\deep\b.jpg", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\x\a.jpg"), MoveOut("op2", $@"{Picked}\old\y\deep\b.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.True(_fs.DirectoryExists(Picked));
        Assert.Equal(4, RemovedFolders(Journal(result)).Count);
    }

    [Fact]
    public void DoesNotClimbAboveRoot()
    {
        const string root = @"D:\Archive\모음\고른";
        Add($@"{root}\sub\a.jpg", 1);
        var plan = PlanOf([root], MoveOut("op1", $@"{root}\sub\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.False(_fs.DirectoryExists($@"{root}\sub"));
        Assert.True(_fs.DirectoryExists(root)); // 비었어도 고른 폴더는 남긴다
        Assert.True(_fs.DirectoryExists(@"D:\Archive\모음"));
        Assert.Equal([$@"{root}\sub"], RemovedFolders(Journal(result)));
    }

    [Fact]
    public void RootItself_StaysWhenEmptied()
    {
        const string root = @"D:\Archive\고른";
        Add($@"{root}\a.jpg", 1);
        var plan = PlanOf([root], MoveOut("op1", $@"{root}\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.True(_fs.DirectoryExists(root));
        Assert.True(_fs.IsDirectoryEmpty(root));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void NestedRoots_NeverRemovesEitherRoot()
    {
        const string outer = @"D:\Archive\큰";
        const string inner = @"D:\Archive\큰\작은";
        Add($@"{inner}\sub\a.jpg", 1);
        var plan = PlanOf([outer, inner], MoveOut("op1", $@"{inner}\sub\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.False(_fs.DirectoryExists($@"{inner}\sub"));
        Assert.True(_fs.DirectoryExists(inner)); // 다른 고른 폴더 아래에 있어도 고른 폴더는 치우지 않는다
        Assert.True(_fs.DirectoryExists(outer));
        Assert.Equal([$@"{inner}\sub"], RemovedFolders(Journal(result)));
    }

    [Fact]
    public void FolderWithSimilarName_IsNotUnderRoot()
    {
        // D:\Archive\abcd는 D:\Archive\abc 아래가 아니다.
        const string root = @"D:\Archive\abc";
        _fs.AddDirectory(root);
        Add(@"D:\Archive\abcd\sub\a.jpg", 1);
        var plan = PlanOf([root], MoveOut("op1", @"D:\Archive\abcd\sub\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.True(_fs.DirectoryExists(@"D:\Archive\abcd\sub"));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Theory]
    [InlineData(@"D:\ARCHIVE\촬영")]
    [InlineData(@"d:\archive\촬영\")]
    [InlineData(@"D:\Archive\촬영/")]
    public void RootComparison_IgnoresCaseAndTrailingSeparator(string root)
    {
        Add($@"{Picked}\old\a.jpg", 1);
        var plan = PlanOf([root], MoveOut("op1", $@"{Picked}\old\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.True(_fs.DirectoryExists(Picked));
        Assert.Single(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void DriveRootAsRoot_RemovesFoldersBelowIt()
    {
        Add(@"D:\Inbox\a.jpg", 1);
        var plan = PlanOf([@"D:\"], MoveOut("op1", @"D:\Inbox\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.Equal([@"D:\Inbox"], RemovedFolders(Journal(result)));
    }

    [Fact]
    public void BlankRoot_IsIgnored()
    {
        Add($@"{Picked}\old\a.jpg", 1);
        var plan = PlanOf([" "], MoveOut("op1", $@"{Picked}\old\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
    }

    [Fact]
    public void DoesNotRemoveUnrelatedEmptyFolder()
    {
        _fs.AddDirectory($@"{Picked}\빈폴더");
        _fs.AddDirectory($@"{Picked}\old\빈폴더");
        Add($@"{Picked}\old\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.True(_fs.DirectoryExists($@"{Picked}\빈폴더"));
        Assert.True(_fs.DirectoryExists($@"{Picked}\old\빈폴더"));
        Assert.Empty(result.Warnings);
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void NoCleanupRoots_RemovesNothing()
    {
        Add(@"D:\Inbox\old\a.jpg", 1);
        var plan = PlanOf(null, MoveOut("op1", @"D:\Inbox\old\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.True(_fs.DirectoryExists(@"D:\Inbox\old"));
        Assert.DoesNotContain(Entries(Journal(result)), e => e.Event is JournalEvent.CleanupRoot or JournalEvent.FolderRemoved);
    }

    [Fact]
    public void CopyOperations_LeaveSourceFolders()
    {
        var a = Add(@"E:\Inbox\sub\a.jpg", 1);
        var copy = new PlannedOperation("op1", OperationKind.Copy, @"E:\Inbox\sub\a.jpg", $@"{Target}\a.jpg", a.Length, []);

        var result = NewExecutor().Execute(PlanOf([@"E:\Inbox"], copy));

        Assert.Equal(a, _fs.ReadAllBytes(@"E:\Inbox\sub\a.jpg"));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void AbsorbRootOutsideArchive_IsCleaned()
    {
        const string root = @"D:\Inbox";
        Add($@"{root}\trip\a.jpg", 1);
        var plan = PlanOf([root], MoveOut("op1", $@"{root}\trip\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.False(_fs.DirectoryExists($@"{root}\trip"));
        Assert.True(_fs.DirectoryExists(root));
        Assert.Equal([$@"{root}\trip"], RemovedFolders(Journal(result)));
    }

    [Fact]
    public void TempNameBridge_RemovesFolderOnce()
    {
        var a = Add($@"{Picked}\old\a.jpg", 1);
        var temp = $@"{Picked}\old\a.jpg.photook-tmp-op1";
        var plan = PlanOf([Picked],
            new PlannedOperation("op1", OperationKind.Move, $@"{Picked}\old\a.jpg", temp, a.Length, []),
            new PlannedOperation("op2", OperationKind.Move, temp, $@"{Target}\a.jpg", a.Length, []));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal(a, _fs.ReadAllBytes($@"{Target}\a.jpg"));
        Assert.Equal([$@"{Picked}\old"], RemovedFolders(Journal(result)));
    }

    // ------------------------------------------------------------------
    // 그대로 두어야 하는 폴더
    // ------------------------------------------------------------------

    [Fact]
    public void FolderThatTurnedNonEmpty_Stays()
    {
        Add($@"{Picked}\old\a.jpg", 1);
        var late = Bytes(10, 2);
        _hooked.BeforeRemoveDirectory = path => _fs.AddFile(Path.Combine(path, "late.jpg"), late);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.Equal(late, _fs.ReadAllBytes($@"{Picked}\old\late.jpg"));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void LinkedFolder_Stays()
    {
        _fs.AddLinkedDirectory($@"{Picked}\old\link");
        Add($@"{Picked}\old\link\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\link\a.jpg"));

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.True(_fs.DirectoryExists($@"{Picked}\old\link"));
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    // ------------------------------------------------------------------
    // 취소·실패·이어 하기
    // ------------------------------------------------------------------

    [Fact]
    public void Cancelled_NoCleanup()
    {
        Add($@"{Picked}\one\a.jpg", 1);
        Add($@"{Picked}\two\b.jpg", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\one\a.jpg"), MoveOut("op2", $@"{Picked}\two\b.jpg"));
        using var cts = new CancellationTokenSource();
        _hooked.AfterMove = (_, _) => cts.Cancel();

        var result = NewExecutor().Execute(plan, cts.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.True(_fs.DirectoryExists($@"{Picked}\one"));
        Assert.Empty(RemovedFolders(Journal(result)));
        Assert.False(IsFinished(Journal(result)));
    }

    [Fact]
    public void WithFailures_NoCleanup()
    {
        Add($@"{Picked}\one\a.jpg", 1);
        Add($@"{Picked}\two\b.jpg", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\one\a.jpg"), MoveOut("op2", $@"{Picked}\two\b.jpg"));
        _hooked.BeforeMove = (source, _) =>
        {
            if (source.EndsWith("b.jpg", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("모의 이동 오류");
            }
        };

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.True(_fs.DirectoryExists($@"{Picked}\one"));
        Assert.True(_fs.FileExists($@"{Picked}\two\b.jpg"));
        Assert.Empty(RemovedFolders(Journal(result)));
    }

    [Fact]
    public void Resume_DoesCleanupAtEnd()
    {
        Add($@"{Picked}\one\a.jpg", 1);
        Add($@"{Picked}\two\b.jpg", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\one\a.jpg"), MoveOut("op2", $@"{Picked}\two\b.jpg"));
        using var cts = new CancellationTokenSource();
        _hooked.AfterMove = (_, _) => cts.Cancel();
        var journal = Journal(NewExecutor().Execute(plan, cts.Token));
        _hooked.AfterMove = null;
        Assert.Equal([Picked], JournalReader.Load(_fs, journal).CleanupRoots);

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.False(_fs.DirectoryExists($@"{Picked}\one"));
        Assert.False(_fs.DirectoryExists($@"{Picked}\two"));
        Assert.True(_fs.DirectoryExists(Picked));
        Assert.Equal(2, RemovedFolders(journal).Count);
        Assert.True(IsFinished(journal));
    }

    [Fact]
    public void Resume_WithFailures_NoCleanup()
    {
        Add($@"{Picked}\one\a.jpg", 1);
        Add($@"{Picked}\two\b.jpg", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\one\a.jpg"), MoveOut("op2", $@"{Picked}\two\b.jpg"));
        using var cts = new CancellationTokenSource();
        _hooked.AfterMove = (_, _) => cts.Cancel();
        var journal = Journal(NewExecutor().Execute(plan, cts.Token));
        _hooked.AfterMove = null;
        _hooked.BeforeMove = (_, _) => throw new IOException("모의 이동 오류");

        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.CompletedWithFailures, result.Outcome);
        Assert.True(_fs.DirectoryExists($@"{Picked}\one"));
        Assert.Empty(RemovedFolders(journal));
    }

    [Fact]
    public void CancelledDuringCleanup_FinishesCurrentFolder_AndResumeCompletes()
    {
        Add($@"{Picked}\old\sub\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));
        using var cts = new CancellationTokenSource();
        _hooked.BeforeRemoveDirectory = _ => cts.Cancel();

        var result = NewExecutor().Execute(plan, cts.Token);
        var journal = Journal(result);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.False(_fs.DirectoryExists($@"{Picked}\old\sub")); // 지금 폴더는 마쳤다
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.Equal([$@"{Picked}\old\sub"], RemovedFolders(journal));
        Assert.False(IsFinished(journal)); // 끝맺지 않아 이어 할 수 있다

        _hooked.BeforeRemoveDirectory = null;
        var resumed = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, resumed.Outcome);
        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.True(_fs.DirectoryExists(Picked));
        Assert.Equal([$@"{Picked}\old\sub", $@"{Picked}\old"], RemovedFolders(journal));
        Assert.True(IsFinished(journal));
    }

    [Fact]
    public void CancelledBeforeCleanup_ResumeStillCleans()
    {
        // 마지막 작업 도중에 들어온 취소: 작업은 다 끝났지만 치우지 않고 멈춘다.
        Add($@"{Picked}\old\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\a.jpg"));
        using var cts = new CancellationTokenSource();
        _hooked.AfterMove = (_, _) => cts.Cancel();

        var result = NewExecutor().Execute(plan, cts.Token);

        Assert.Equal(RunOutcome.Cancelled, result.Outcome);
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.False(IsFinished(Journal(result)));

        _hooked.AfterMove = null;
        Assert.Equal(RunOutcome.Completed, NewExecutor().Resume(Journal(result)).Outcome);
        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
    }

    [Fact]
    public void Resume_AfterCrashBetweenRemovalAndRecord_StillClimbs()
    {
        // 아래 폴더를 치우고 기록하기 전에 끊기면, 이어 하기는 이미 없는 폴더에서 멈추지 말고 위로 올라가야 한다.
        Add($@"{Picked}\old\sub\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));
        var removed = false;
        _hooked.BeforeRemoveDirectory = _ => removed = true;
        _hooked.BeforeAppend = _ =>
        {
            if (removed)
            {
                throw new SimulatedCrashException();
            }
        };

        Assert.Throws<SimulatedCrashException>(() => NewExecutor().Execute(plan));
        Assert.False(_fs.DirectoryExists($@"{Picked}\old\sub"));
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));

        _hooked.BeforeRemoveDirectory = null;
        _hooked.BeforeAppend = null;
        var journal = _fs.AllFiles().First(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                                                && p.StartsWith(Archive, StringComparison.OrdinalIgnoreCase));
        var result = NewExecutor().Resume(journal);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.True(_fs.DirectoryExists(Picked));
    }

    [Fact]
    public void Resume_FinishedRun_DoesNotCleanAgain()
    {
        Add($@"{Picked}\old\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\a.jpg"));
        var journal = Journal(NewExecutor().Execute(plan));
        _fs.AddDirectory($@"{Picked}\old"); // 끝난 뒤 사용자가 다시 만든 폴더

        Assert.Equal(RunOutcome.Completed, NewExecutor().Resume(journal).Outcome);

        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void CleanupFailure_IsWarningOnly(Type failure)
    {
        var a = Add($@"{Picked}\old\sub\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));
        _hooked.BeforeRemoveDirectory = _ => throw (Exception)Activator.CreateInstance(failure, "모의 제거 오류")!;

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        Assert.Equal([$@"빈 폴더를 치우지 못함: {Picked}\old\sub"], result.Warnings);
        Assert.True(_fs.DirectoryExists($@"{Picked}\old\sub"));
        Assert.Equal(a, _fs.ReadAllBytes($@"{Target}\op1-a.jpg"));
        Assert.Empty(RemovedFolders(Journal(result)));
        Assert.True(IsFinished(Journal(result)));
    }

    [Fact]
    public void SourceVolumeGone_IsWarningOnly()
    {
        // 앱 데이터 사본은 다른 드라이브에 있어 기록은 이어진다.
        const string root = @"D:\Inbox";
        Add($@"{root}\trip\a.jpg", 1);
        var plan = PlanOf([root], MoveOut("op1", $@"{root}\trip\a.jpg"));
        var moved = false;
        _hooked.AfterMove = (_, _) => moved = true;
        _hooked.BeforeAppend = path =>
        {
            // 마지막 작업의 Completed를 아카이브 사본까지 쓴 뒤에 드라이브가 빠진다.
            if (moved && path.StartsWith(AppData, StringComparison.OrdinalIgnoreCase))
            {
                _fs.RemoveVolume(@"D:\");
            }
        };

        var result = NewExecutor().Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Contains($@"빈 폴더를 치우지 못함: {root}\trip", result.Warnings);
        _fs.RestoreVolume(@"D:\");
        Assert.True(_fs.DirectoryExists($@"{root}\trip"));
        Assert.True(_fs.FileExists($@"{Target}\op1-a.jpg"));
    }

    // ------------------------------------------------------------------
    // 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_RestoresFilesIntoRemovedFolders()
    {
        var a = Bytes(200, 1);
        var b = Bytes(300, 2);
        var aTime = new DateTime(2020, 5, 1, 3, 4, 5, DateTimeKind.Utc);
        var bTime = new DateTime(2021, 6, 2, 4, 5, 6, DateTimeKind.Utc);
        _fs.AddFile($@"{Picked}\old\sub\a.jpg", a, aTime);
        _fs.AddFile($@"{Picked}\old\b.jpg", b, bTime);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"), MoveOut("op2", $@"{Picked}\old\b.jpg"));
        var journal = Journal(NewExecutor().Execute(plan));
        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));

        var undo = NewUndoer().Undo(journal);

        Assert.Empty(undo.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\sub\a.jpg"));
        Assert.Equal(b, _fs.ReadAllBytes($@"{Picked}\old\b.jpg"));
        Assert.Equal(aTime, _fs.GetFile($@"{Picked}\old\sub\a.jpg").LastWriteTimeUtc);
        Assert.Equal(bTime, _fs.GetFile($@"{Picked}\old\b.jpg").LastWriteTimeUtc);
        Assert.False(_fs.FileExists($@"{Target}\op1-a.jpg"));
    }

    [Fact]
    public void Undo_RestoresFolderHoldingOnlySubfolders()
    {
        var a = Add($@"{Picked}\old\sub\deep\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\deep\a.jpg"));
        var journal = Journal(NewExecutor().Execute(plan));

        Assert.Empty(NewUndoer().Undo(journal).Skipped);

        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\sub\deep\a.jpg"));
    }

    [Fact]
    public void Undo_FileWhereRemovedFolderWas_OverwritesNothing_AndSecondUndoSucceeds()
    {
        var a = Add($@"{Picked}\old\sub\a.jpg", 1);
        var b = Add($@"{Picked}\other\b.jpg", 2);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"), MoveOut("op2", $@"{Picked}\other\b.jpg"));
        var journal = Journal(NewExecutor().Execute(plan));
        Assert.False(_fs.DirectoryExists($@"{Picked}\old"));
        var blocker = Bytes(30, 3);
        _fs.AddFile($@"{Picked}\old", blocker); // 치운 폴더 자리에 같은 이름의 파일이 생겼다

        var undo = NewUndoer().Undo(journal);

        var skip = Assert.Single(undo.Skipped);
        Assert.Equal($@"원래 폴더 자리에 다른 파일이 있어 되돌리지 못했습니다: {Picked}\old", skip.Reason);
        Assert.Equal(blocker, _fs.ReadAllBytes($@"{Picked}\old"));
        Assert.Equal(a, _fs.ReadAllBytes($@"{Target}\op1-a.jpg")); // 옮긴 파일은 대상 자리에 그대로다
        Assert.Equal(b, _fs.ReadAllBytes($@"{Picked}\other\b.jpg"));
        var state = JournalReader.Load(_fs, journal);
        Assert.Equal(OpStatus.Completed, state.Operations.Single(o => o.Operation.Id == "op1").Status); // 다음 되돌리기가 다시 본다

        _fs.Move($@"{Picked}\old", $@"{Picked}\old.bak"); // 사용자가 치운다
        var second = NewUndoer().Undo(journal);

        Assert.Empty(second.Skipped);
        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\sub\a.jpg"));
        Assert.False(_fs.FileExists($@"{Target}\op1-a.jpg"));
        Assert.Equal(blocker, _fs.ReadAllBytes($@"{Picked}\old.bak"));
    }

    [Fact]
    public void Undo_UnrecordedMove_FileWhereRemovedFolderWas_OverwritesNothing_AndSecondUndoSucceeds()
    {
        // 옮기고 Completed를 쓰기 전에 끊긴 작업도 같은 경로로 돌려놓는다.
        var a = Bytes(100, 1);
        _fs.AddFile($@"{Target}\a.jpg", a);
        var blocker = Bytes(30, 3);
        _fs.AddFile($@"{Picked}\old", blocker);
        var path = WriteJournal(JournalPaths.InArchive(Archive, RunId),
            new JournalEntry(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Planned, "op1", OperationKind.Move, $@"{Picked}\old\sub\a.jpg", $@"{Target}\a.jpg", a.Length, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Started, "op1", OperationKind.Move, TimestampUtc: Now));

        var undo = NewUndoer().Undo(path);

        Assert.Equal($@"원래 폴더 자리에 다른 파일이 있어 되돌리지 못했습니다: {Picked}\old", Assert.Single(undo.Skipped).Reason);
        Assert.Equal(blocker, _fs.ReadAllBytes($@"{Picked}\old"));
        Assert.Equal(a, _fs.ReadAllBytes($@"{Target}\a.jpg"));

        _fs.Move($@"{Picked}\old", $@"{Picked}\old.bak");

        Assert.Empty(NewUndoer().Undo(path).Skipped);
        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\sub\a.jpg"));
    }

    [Fact]
    public void Undo_AfterCancelledCleanup_RestoresFiles()
    {
        var a = Add($@"{Picked}\old\sub\a.jpg", 1);
        var plan = PlanOf([Picked], MoveOut("op1", $@"{Picked}\old\sub\a.jpg"));
        using var cts = new CancellationTokenSource();
        _hooked.BeforeRemoveDirectory = _ => cts.Cancel();
        var journal = Journal(NewExecutor().Execute(plan, cts.Token));
        _hooked.BeforeRemoveDirectory = null;

        Assert.Empty(NewUndoer().Undo(journal).Skipped);

        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\sub\a.jpg"));

        // 되돌리기를 시작한 기록은 이어 하지 않으므로 복원한 폴더를 다시 치우지 않는다.
        Assert.Equal(RunOutcome.UndoStarted, NewExecutor().Resume(journal).Outcome);
        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\sub\a.jpg"));
    }

    // ------------------------------------------------------------------
    // 기록 읽기
    // ------------------------------------------------------------------

    [Fact]
    public void Journal_RootsAreWrittenBeforePlannedOperations_NotCountedAsPlan()
    {
        Add($@"{Picked}\old\a.jpg", 1);
        var plan = PlanOf([Picked, @"D:\Archive\다른"], MoveOut("op1", $@"{Picked}\old\a.jpg"));

        var result = NewExecutor().Execute(plan);

        var entries = Entries(Journal(result));
        Assert.Equal(JournalEvent.RunStarted, entries[0].Event);
        Assert.Equal(1, entries[0].Length);
        Assert.Equal(JournalEvent.CleanupRoot, entries[1].Event);
        Assert.Equal(Picked, entries[1].Destination);
        Assert.Null(entries[1].OpId);
        Assert.Equal(JournalEvent.CleanupRoot, entries[2].Event);
        Assert.Equal(JournalEvent.Planned, entries[3].Event);

        var state = JournalReader.Load(_fs, Journal(result));
        Assert.Equal(1, state.ExpectedOperations);
        Assert.Single(state.Operations);
        Assert.Equal([Picked, @"D:\Archive\다른"], state.CleanupRoots);
    }

    private string WriteJournal(string path, params JournalEntry[] entries)
    {
        var writer = new JournalWriter(_fs, [path]);
        foreach (var entry in entries)
        {
            writer.Write(entry);
        }

        return path;
    }

    [Fact]
    public void OldJournalWithoutNewEvents_StillReads()
    {
        var a = Add($@"{Picked}\old\a.jpg", 1);
        var path = WriteJournal(JournalPaths.InArchive(Archive, RunId),
            new JournalEntry(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Planned, "op1", OperationKind.Move, $@"{Picked}\old\a.jpg", $@"{Target}\a.jpg", a.Length, TimestampUtc: Now));

        var state = JournalReader.Load(_fs, path);

        Assert.Empty(state.CleanupRoots);
        Assert.Empty(state.Warnings);
        Assert.Equal(OpStatus.Pending, state.Operations.Single().Status);
        Assert.Equal(2, state.EntryCount);

        // 이어 해도 치우지 않고, 되돌리기도 예전처럼 된다.
        Assert.Equal(RunOutcome.Completed, NewExecutor().Resume(path).Outcome);
        Assert.True(_fs.DirectoryExists($@"{Picked}\old"));
        Assert.Empty(NewUndoer().Undo(path).Skipped);
        Assert.Equal(a, _fs.ReadAllBytes($@"{Picked}\old\a.jpg"));
    }

    [Fact]
    public void NewEvents_DoNotChangeOperationStatus()
    {
        var path = WriteJournal(JournalPaths.InArchive(Archive, RunId),
            new JournalEntry(RunId, JournalEvent.RunStarted, Length: 2, Destination: Archive, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.CleanupRoot, Destination: Picked, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Planned, "op1", OperationKind.Move, $@"{Picked}\a.jpg", $@"{Target}\a.jpg", 1, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Planned, "op2", OperationKind.Move, $@"{Picked}\b.jpg", $@"{Target}\b.jpg", 1, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Started, "op1", OperationKind.Move, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Warning, Message: "경고 문구", TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Completed, "op1", OperationKind.Move, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.FolderRemoved, Destination: $@"{Picked}\old", TimestampUtc: Now));

        var state = JournalReader.Load(_fs, path);

        Assert.Equal(OpStatus.Completed, state.Operations[0].Status);
        Assert.Equal(OpStatus.Pending, state.Operations[1].Status);
        Assert.Equal(2, state.ExpectedOperations);
        Assert.Equal(2, state.Operations.Count);
        Assert.False(state.Finished);
        Assert.False(state.UndoStarted);
        Assert.Equal([Picked], state.CleanupRoots);
        Assert.Equal(["경고 문구"], state.Warnings);
        Assert.Equal(8, state.EntryCount); // 사본을 고를 때 새 사건도 항목으로 센다
    }

    [Fact]
    public void NewEventsWithoutValues_AreIgnored_NotCorruption()
    {
        var path = WriteJournal(JournalPaths.InArchive(Archive, RunId),
            new JournalEntry(RunId, JournalEvent.RunStarted, Length: 0, Destination: Archive, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.CleanupRoot, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Warning, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.FolderRemoved, TimestampUtc: Now));

        var state = JournalReader.Load(_fs, path);

        Assert.Empty(state.CleanupRoots);
        Assert.Empty(state.Warnings);
    }

    [Fact]
    public void JournalCopies_PickCopyWithMoreEntries_CountingNewEvents()
    {
        // 앱 데이터 사본에만 FolderRemoved가 남은 경우: 더 긴 사본을 사실로 삼고 그 사본에만 이어 쓴다.
        _fs.AddDirectory($@"{Picked}\old");
        JournalEntry[] common =
        [
            new(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now),
            new(RunId, JournalEvent.CleanupRoot, Destination: Picked, TimestampUtc: Now),
            new(RunId, JournalEvent.Planned, "op1", OperationKind.Move, $@"{Picked}\old\sub\a.jpg", $@"{Target}\a.jpg", 1, TimestampUtc: Now),
            new(RunId, JournalEvent.Completed, "op1", OperationKind.Move, TimestampUtc: Now),
        ];
        var archiveCopy = WriteJournal(JournalPaths.InArchive(Archive, RunId), common);
        var appDataCopy = WriteJournal(JournalPaths.InAppData(AppData, RunId),
            [.. common, new(RunId, JournalEvent.FolderRemoved, Destination: $@"{Picked}\old\sub", TimestampUtc: Now)]);

        var result = NewExecutor().Resume(archiveCopy);

        Assert.Equal(appDataCopy, result.JournalPath);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.False(_fs.DirectoryExists($@"{Picked}\old")); // 사본을 어느 쪽으로 골라도 이미 없는 sub에서 위로 올라간다
        Assert.True(IsFinished(appDataCopy));
        Assert.False(IsFinished(archiveCopy));
    }

    // ------------------------------------------------------------------
    // 수정 시각 경고
    // ------------------------------------------------------------------

    /// <summary>두 파일을 복사하는 계획. a.jpg는 시각을 넣지 못한다(파일 시스템·권한 문제라 이어 해도 같다).</summary>
    private static (FakeFileSystem Fs, Executor Executor, Plan Plan) TimestampScenario()
    {
        var fs = new FakeFileSystem { NowUtc = Now };
        fs.AddDirectory(Archive);
        fs.AddDirectory(AppData);
        fs.AddDirectory(Target);
        var a = Bytes(100, 1);
        var b = Bytes(120, 2);
        fs.AddFile(@"E:\DCIM\a.jpg", a);
        fs.AddFile(@"E:\DCIM\b.jpg", b);
        fs.FailSetTimeOf($@"{Target}\a.jpg.photook-tmp");
        var plan = PlanOf(null,
            new PlannedOperation("op1", OperationKind.Copy, @"E:\DCIM\a.jpg", $@"{Target}\a.jpg", a.Length, []),
            new PlannedOperation("op2", OperationKind.Copy, @"E:\DCIM\b.jpg", $@"{Target}\b.jpg", b.Length, []));
        return (fs, new Executor(new ExecutionContext(fs, new XxHasher(fs), AppData, () => Now)), plan);
    }

    [Fact]
    public void TimestampWarning_SurvivesResume()
    {
        var (fs, executor, plan) = TimestampScenario();
        using var cts = new CancellationTokenSource();
        var hooked = new HookedFileSystem(fs)
        {
            AfterMove = (_, _) => cts.Cancel(),
        };
        var first = new Executor(new ExecutionContext(hooked, new XxHasher(hooked), AppData, () => Now)).Execute(plan, cts.Token);
        Assert.Equal(RunOutcome.Cancelled, first.Outcome);
        Assert.Contains(TimestampMessage, first.Warnings);

        // 앱이 끝난 뒤 다시 켜서 이어 하는 경우: 경고는 기록에서 읽는다.
        var result = executor.Resume(first.JournalPath!);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Equal([TimestampMessage], result.Warnings);

        var state = JournalReader.Load(fs, result.JournalPath!);
        Assert.Equal([TimestampMessage], state.Warnings);
    }

    [Fact]
    public void TimestampWarning_FinishedRun_StillReported()
    {
        var (_, executor, plan) = TimestampScenario();
        var first = executor.Execute(plan);
        Assert.Equal(RunOutcome.Completed, first.Outcome);

        var again = executor.Resume(first.JournalPath!);

        Assert.Equal([TimestampMessage], again.Warnings);
    }

    [Fact]
    public void TimestampWarning_SurvivesCrash_AtEveryStep()
    {
        // 시각을 넣지 못한 파일이 대상에 있으면, 어디서 끊겼든 이어 하기 결과에 경고가 한 번 있어야 한다.
        // 이름을 붙인 뒤 경고를 기록하기 전에 끊겨도 마찬가지다.
        var (cleanFs, cleanExecutor, cleanPlan) = TimestampScenario();
        var before = cleanFs.MutatingOps;
        Assert.Equal(RunOutcome.Completed, cleanExecutor.Execute(cleanPlan).Outcome);
        var n = cleanFs.MutatingOps - before;
        Assert.True(n > 10, $"변경 작업 수가 너무 적습니다: {n}");

        var failures = new List<string>();
        for (var k = 0; k < n; k++)
        {
            var (fs, executor, plan) = TimestampScenario();
            fs.CrashAfter(k);
            Assert.Throws<SimulatedCrashException>(() => executor.Execute(plan));
            fs.ClearFault();
            fs.FailSetTimeOf($@"{Target}\a.jpg.photook-tmp");
            var journal = fs.AllFiles().FirstOrDefault(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase));
            if (journal is null)
            {
                continue;
            }

            var result = executor.Resume(journal);
            if (result.Outcome == RunOutcome.PlanIncomplete)
            {
                continue;
            }

            if (result.Warnings.Count(w => w == TimestampMessage) != 1)
            {
                failures.Add($"k={k}: {result.Outcome}, 경고 [{string.Join(" | ", result.Warnings)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>끊긴 복사의 대상이 이미 있는 기록을 만들고 이어 한다. 대상의 시각은 원본과 <paramref name="secondsApart"/>초 다르다.</summary>
    private ExecutionResult ResumeReconciledCopy(double secondsApart)
    {
        var a = Bytes(100, 1);
        var sourceTime = new DateTime(2025, 3, 1, 10, 0, 1, DateTimeKind.Utc);
        _fs.AddFile(@"E:\DCIM\a.jpg", a, sourceTime);
        _fs.AddFile($@"{Target}\a.jpg", a, sourceTime.AddSeconds(secondsApart));
        var path = WriteJournal(JournalPaths.InArchive(Archive, RunId),
            new JournalEntry(RunId, JournalEvent.RunStarted, Length: 1, Destination: Archive, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Planned, "op1", OperationKind.Copy, @"E:\DCIM\a.jpg", $@"{Target}\a.jpg", a.Length, TimestampUtc: Now),
            new JournalEntry(RunId, JournalEvent.Started, "op1", OperationKind.Copy, TimestampUtc: Now));

        var result = NewExecutor().Resume(path);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // FAT 계열은 짝수 초로 반올림한다
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(-2)]
    public void ReconciledCopy_RoundedDestinationTime_HasNoWarning(double secondsApart)
    {
        Assert.Empty(ResumeReconciledCopy(secondsApart).Warnings);
    }

    [Theory]
    [InlineData(2.5)]
    [InlineData(-3)]
    [InlineData(86400)]
    public void ReconciledCopy_RealTimeDifference_Warns(double secondsApart)
    {
        Assert.Equal([TimestampMessage], ResumeReconciledCopy(secondsApart).Warnings);
    }
}
