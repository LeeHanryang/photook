using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.History;
using PhotoOk.Core.Planning;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.ViewModels;

public class HistoryViewModelTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string LogFolder = @"L:\PhotoOk\logs"; // 기록을 남길 C:·D:가 사라져도 로그는 남도록 다른 드라이브
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly ShellViewModel _shell;
    private readonly ObservableObject _home = new ScreenStub();
    private readonly List<string> _resumeRequests = [];

    public HistoryViewModelTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
        _fs.AddDirectory(@"D:\Archive\2026");
    }

    private sealed class ScreenStub : ObservableObject;

    private ExecutionContext Context() => new(_fs, new XxHasher(_fs), AppData, () => Now);

    private FileLog Log => new(_fs, LogFolder);

    private HistoryViewModel Create(string? selectRunId = null) =>
        new(_shell, new RunHistory(_fs, AppData), new Undoer(Context()), _dialogs, Log,
            createResume: path =>
            {
                _resumeRequests.Add(path);
                return RunViewModel.ForResume(_shell, new Executor(Context()), _fs, Log, () => _home, () => _home, path);
            },
            createHome: () => _home,
            selectRunId);

    private static PlannedOperation Copy(string id, string source, string destination, long length) =>
        new(id, OperationKind.Copy, source, destination, length, Array.Empty<ReviewReason>());

    private Plan CopyPlan(int count)
    {
        var ops = new List<PlannedOperation>();
        for (var i = 1; i <= count; i++)
        {
            var data = new byte[100 + i];
            new Random(i).NextBytes(data);
            var source = $@"E:\DCIM\p{i}.jpg";
            _fs.AddFile(source, data);
            ops.Add(Copy($"op{i}", source, $@"D:\Archive\2026\p{i}.jpg", data.Length));
        }

        return new Plan(Archive, ops, Array.Empty<PlannedSkip>(), Array.Empty<string>());
    }

    /// <summary>실제 실행기로 계획을 실행해 기록을 남기고 기록 경로를 돌려준다.</summary>
    private string Run(Plan plan) => new Executor(Context()).Execute(plan, CancellationToken.None, null).JournalPath!;

    /// <summary>손으로 만든 기록: RunStarted(기대 작업 수), 계획된 작업, 덧붙일 사건들.</summary>
    private string WriteJournal(string runId, PlannedOperation[] planned, int? expected = null, params JournalEvent[] extra)
    {
        var writer = new JournalWriter(_fs, [JournalPaths.InArchive(Archive, runId), JournalPaths.InAppData(AppData, runId)]);
        writer.WriteMany(planned
            .Select(op => new JournalEntry(runId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
                TimestampUtc: Now))
            .Prepend(new JournalEntry(runId, JournalEvent.RunStarted, Length: expected ?? planned.Length, Destination: Archive,
                TimestampUtc: Now)));
        foreach (var ev in extra)
        {
            writer.Write(new JournalEntry(runId, ev, TimestampUtc: Now));
        }

        return JournalPaths.InArchive(Archive, runId);
    }

    private IEnumerable<string> ArchiveFilesOutsidePhotook() =>
        _fs.AllFiles().Where(p => p.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase)
            && !p.StartsWith(Archive + @"\.photook\", StringComparison.OrdinalIgnoreCase));

    private string LogText()
    {
        var logs = _fs.AllFiles().Where(p => p.StartsWith(LogFolder, StringComparison.OrdinalIgnoreCase)).ToList();
        return string.Concat(logs.Select(p => Encoding.UTF8.GetString(_fs.ReadAllBytes(p))));
    }

    [Fact]
    public void Lists_Runs_WithStatusText()
    {
        WriteJournal("20261026-100000-aaaa", [], expected: 2);                          // 계획이 끝나지 않음
        Run(CopyPlan(1));                                                               // 완료
        WriteJournal("20261025-100000-cccc", [], expected: 0, JournalEvent.UndoStarted); // 되돌림

        var vm = Create();

        Assert.Equal(3, vm.Runs.Count);
        Assert.Equal(RunStatus.PlanIncomplete, vm.Runs.Single(r => r.RunId == "20261026-100000-aaaa").Status);
        Assert.Contains(vm.Runs, r => r.Status == RunStatus.Completed);
        Assert.Contains(vm.Runs, r => r.Status == RunStatus.Undone);
        Assert.Equal("완료", vm.StatusText(RunStatus.Completed));
        Assert.Equal("일부 실패", vm.StatusText(RunStatus.CompletedWithFailures));
        Assert.Equal("끝나지 않음", vm.StatusText(RunStatus.Unfinished));
        Assert.Equal("계획 미완료 — 다시 계획해 주세요", vm.StatusText(RunStatus.PlanIncomplete));
        Assert.Equal("되돌리는 중", vm.StatusText(RunStatus.UndoInProgress));
        Assert.Equal("되돌림", vm.StatusText(RunStatus.Undone));
        Assert.Empty(vm.LoadErrors);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public void ShowsReadErrors_InKorean()
    {
        _fs.AddFile(Path.Combine(AppData, "journal", "broken.jsonl"), Encoding.UTF8.GetBytes("not json\n"));

        var vm = Create();

        Assert.Empty(vm.Runs);
        var line = Assert.Single(vm.LoadErrors);
        Assert.EndsWith("기록을 읽을 수 없습니다", line);
    }

    [Fact]
    public async Task Undo_AsksFirst_AndShowsResult()
    {
        var journal = Run(CopyPlan(2));
        var vm = Create();
        vm.Selected = vm.Runs.Single();
        var started = vm.Selected.StartedLocal;
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;
        _dialogs.ConfirmAnswers.Enqueue(true);

        Assert.True(vm.UndoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_dialogs.Confirms);
        Assert.Equal("되돌리기", confirm.Title);
        Assert.Equal(
            started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            + @" 실행을 되돌릴까요? 복사본은 보관 폴더의 .photook\되돌림으로 옮겨집니다.",
            confirm.Message);
        Assert.Empty(ArchiveFilesOutsidePhotook());
        Assert.True(_fs.FileExists(@"E:\DCIM\p1.jpg"));
        Assert.True(_fs.FileExists(@"E:\DCIM\p2.jpg"));
        Assert.Equal("되돌린 작업 2개, 남긴 항목 0개", vm.UndoSummary);
        Assert.Empty(vm.UndoResultLines);
        Assert.Null(vm.UndoFault);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);

        // 목록을 다시 읽고 같은 실행을 계속 선택한다.
        Assert.NotNull(vm.Selected);
        Assert.Equal(RunStatus.Undone, vm.Selected!.Status);
        Assert.Equal(journal, vm.Selected.JournalPath);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Undo_ShowsKeptItems_AndLogsThem()
    {
        Run(CopyPlan(1));
        _fs.RemoveVolume(@"E:\"); // 원본을 확인할 수 없으면 복사본을 남긴다
        var vm = Create();
        vm.Selected = vm.Runs.Single();
        _dialogs.ConfirmAnswers.Enqueue(true);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Equal("되돌린 작업 0개, 남긴 항목 1개", vm.UndoSummary);
        var line = Assert.Single(vm.UndoResultLines);
        Assert.StartsWith(@"D:\Archive\2026\p1.jpg: ", line);
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p1.jpg"));
        Assert.Contains(@"D:\Archive\2026\p1.jpg", LogText());
    }

    [Fact]
    public async Task Undo_Declined_ChangesNothing()
    {
        Run(CopyPlan(2));
        var vm = Create();
        vm.Selected = vm.Runs.Single();
        var before = _fs.AllFiles().ToList();
        var opsBefore = _fs.MutatingOps;
        _dialogs.ConfirmAnswers.Enqueue(false);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Confirms);
        Assert.Equal(opsBefore, _fs.MutatingOps);
        Assert.Equal(before, _fs.AllFiles().ToList());
        Assert.Null(vm.UndoSummary);
        Assert.Equal(RunStatus.Completed, vm.Selected!.Status);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Commands_FollowCanResumeAndCanUndo()
    {
        var planned = new[] { Copy("op1", @"E:\DCIM\p1.jpg", @"D:\Archive\2026\p1.jpg", 10) };
        WriteJournal("20261026-100000-aaaa", [], expected: 2);                           // 계획 미완료
        WriteJournal("20261025-100000-bbbb", [], expected: 0, JournalEvent.UndoStarted); // 되돌림
        WriteJournal("20261024-100000-cccc", planned);                                   // 끝나지 않음
        Run(CopyPlan(1));                                                                // 완료
        var vm = Create();

        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.Selected = vm.Runs.Single(r => r.Status == RunStatus.PlanIncomplete);
        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.Selected = vm.Runs.Single(r => r.Status == RunStatus.Undone);
        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null));

        vm.Selected = vm.Runs.Single(r => r.Status == RunStatus.Unfinished);
        Assert.True(vm.ResumeCommand.CanExecute(null));
        Assert.True(vm.UndoCommand.CanExecute(null));

        vm.Selected = vm.Runs.Single(r => r.Status == RunStatus.Completed);
        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task Resume_NavigatesToRunAndCompletes()
    {
        var plan = CopyPlan(2);
        var journal = WriteJournal("20261027-090000-bbbb", [.. plan.Operations]);
        var vm = Create();
        vm.Selected = vm.Runs.Single();
        Assert.Equal(RunStatus.Unfinished, vm.Selected.Status);

        await vm.ResumeCommand.ExecuteAsync(null);

        Assert.Equal([journal], _resumeRequests);
        var run = Assert.IsType<RunViewModel>(_shell.Current);
        await run.StartAsync();
        Assert.Equal("정리를 마쳤습니다", run.ResultTitle);
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p1.jpg"));
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p2.jpg"));
    }

    [Fact]
    public void SelectsRequestedRun_WhenOpenedFromStart()
    {
        WriteJournal("20261026-100000-aaaa", [], expected: 2);
        Run(CopyPlan(1));

        var vm = Create(selectRunId: "20261026-100000-aaaa");

        Assert.Equal("20261026-100000-aaaa", vm.Selected?.RunId);
    }

    [Fact]
    public async Task Undo_Fault_ShowsKoreanMessage_AndStillEndsBusy()
    {
        Run(CopyPlan(1));
        var vm = Create();
        vm.Selected = vm.Runs.Single();
        _dialogs.ConfirmAnswers.Enqueue(true);
        _fs.CrashAfter(0); // 되돌리기의 첫 변경에서 예외
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        await vm.UndoCommand.ExecuteAsync(null);

        _fs.ClearFault();
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.False(string.IsNullOrEmpty(vm.UndoFault));
        Assert.DoesNotContain("Exception", vm.UndoFault);
        Assert.Null(vm.UndoSummary);
    }

    [Fact]
    public void Cancel_WhenIdle_DoesNothing()
    {
        var vm = Create();

        vm.Cancel();

        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Home_NavigatesToHome()
    {
        var vm = Create();

        vm.HomeCommand.Execute(null);

        Assert.Same(_home, _shell.Current);
    }
}
