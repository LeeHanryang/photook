using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.ViewModels;

public class RunViewModelTests
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string LogFolder = @"L:\PhotoOk\logs"; // 기록을 남길 C:·D:가 사라져도 로그는 남도록 다른 드라이브
    private const string RunId = "20261027-103000-ab12";
    private const string SafeMessage = "검증 완료 — 원본을 직접 정리해도 안전합니다";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly HookedFileSystem _hooked;
    private readonly ShellViewModel _shell = new(new FakeDialogService());
    private readonly ObservableObject _home = new ScreenStub();
    private readonly ObservableObject _history = new ScreenStub();

    public RunViewModelTests()
    {
        _hooked = new HookedFileSystem(_fs);
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(AppData);
        _fs.AddDirectory(@"D:\Archive\2026");
    }

    private sealed class ScreenStub : ObservableObject;

    // ------------------------------------------------------------------
    // 도우미
    // ------------------------------------------------------------------

    private Executor NewExecutor() => new(new ExecutionContext(_hooked, new XxHasher(_hooked), AppData, () => Now));

    private FileLog Log => new(_fs, LogFolder);

    private RunViewModel ForPlan(Plan plan, int scanErrors = 0, Executor? executor = null, int ignored = 0) =>
        RunViewModel.ForPlan(_shell, executor ?? NewExecutor(), _fs, Log, () => _home, () => _history, plan, scanErrors, ignored);

    private RunViewModel ForResume(string journalPath) =>
        RunViewModel.ForResume(_shell, NewExecutor(), _fs, Log, () => _home, () => _history, journalPath);

    private static PlannedOperation Copy(string id, string source, string destination, long length) =>
        new(id, OperationKind.Copy, source, destination, length, Array.Empty<ReviewReason>());

    private static PlannedOperation Mkdir(string id, string destination) =>
        new(id, OperationKind.CreateDirectory, null, destination, 0, Array.Empty<ReviewReason>());

    private static Plan PlanOf(params PlannedOperation[] ops) =>
        new(Archive, ops, Array.Empty<PlannedSkip>(), Array.Empty<string>());

    private byte[] AddSource(string path, int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        _fs.AddFile(path, data);
        return data;
    }

    /// <summary>E:\DCIM에 사진 <paramref name="count"/>장을 만들고 그 복사 계획을 돌려준다.</summary>
    private Plan CopyPlan(int count)
    {
        var ops = new List<PlannedOperation>();
        for (var i = 1; i <= count; i++)
        {
            var data = AddSource($@"E:\DCIM\p{i}.jpg", 100 + i, i);
            ops.Add(Copy($"op{i}", $@"E:\DCIM\p{i}.jpg", $@"D:\Archive\2026\p{i}.jpg", data.Length));
        }

        return PlanOf([.. ops]);
    }

    private IEnumerable<string> ArchiveJournals() =>
        _fs.AllFiles().Where(p => p.StartsWith(@"D:\Archive\.photook\journal\", StringComparison.OrdinalIgnoreCase));

    /// <summary>손으로 만든 기록: RunStarted, 주어진 Planned 몇 개, 그리고 덧붙일 사건들.</summary>
    private string WriteJournal(int expected, PlannedOperation[] planned, params JournalEvent[] extraEvents)
    {
        var path = JournalPaths.InArchive(Archive, RunId);
        var writer = new JournalWriter(_fs, [path, JournalPaths.InAppData(AppData, RunId)]);
        writer.WriteMany(planned
            .Select(op => new JournalEntry(RunId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
                TimestampUtc: Now))
            .Prepend(new JournalEntry(RunId, JournalEvent.RunStarted, Length: expected, Destination: Archive, TimestampUtc: Now)));
        foreach (var ev in extraEvents)
        {
            writer.Write(new JournalEntry(RunId, ev, TimestampUtc: Now));
        }

        return path;
    }

    private string LogText()
    {
        var logs = _fs.AllFiles().Where(p => p.StartsWith(LogFolder, StringComparison.OrdinalIgnoreCase)).ToList();
        return string.Concat(logs.Select(p => Encoding.UTF8.GetString(_fs.ReadAllBytes(p))));
    }

    // ------------------------------------------------------------------
    // 완료와 "안전" 문구
    // ------------------------------------------------------------------

    [Fact]
    public async Task Run_Completes_AndShowsSafeToClean()
    {
        var plan = CopyPlan(2);
        var vm = ForPlan(plan);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;
        var sourcesBefore = _fs.AllFiles().Where(p => p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase)).ToList();

        await vm.StartAsync();

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Equal(SafeMessage, vm.SafeToCleanMessage);
        Assert.Empty(vm.FailureLines);
        Assert.Empty(vm.WarningLines);
        Assert.False(vm.CanResume);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.Equal(2, vm.Total);
        Assert.Equal(2, vm.Done);
        Assert.Equal("2 / 2", vm.ProgressText);
        Assert.Equal(@"D:\Archive\2026\p2.jpg", vm.CurrentPath);
        Assert.Equal("p2.jpg", vm.CurrentFileName);
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p1.jpg"));
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p2.jpg"));
        Assert.Equal(sourcesBefore, _fs.AllFiles().Where(p => p.StartsWith(@"E:\", StringComparison.OrdinalIgnoreCase)).ToList());
    }

    [Theory]
    [InlineData(0, false, 0, true)]  // 스캔 오류·실패·가져오지 않은 파일 모두 없음
    [InlineData(1, false, 0, false)] // 스캔 중 읽지 못한 파일이 있었다
    [InlineData(0, true, 0, false)]  // 복사 중 실패한 파일이 있다
    [InlineData(0, false, 3, false)] // 사진·동영상이 아닌 파일을 가져오지 않았다
    [InlineData(2, false, 3, false)] // 둘 다
    public async Task SafeToClean_OnlyWhenNoScanErrors_NoFailures_AndNoIgnoredFiles(int scanErrors, bool failRead, int ignored,
        bool safe)
    {
        var plan = CopyPlan(2);
        if (failRead)
        {
            _fs.FailReadsOf(@"E:\DCIM\p1.jpg");
        }

        var vm = ForPlan(plan, scanErrors, ignored: ignored);

        await vm.StartAsync();

        Assert.Equal(safe ? SafeMessage : null, vm.SafeToCleanMessage);
        if (failRead)
        {
            Assert.Equal("일부 파일을 처리하지 못했습니다", vm.ResultTitle);
            var line = Assert.Single(vm.FailureLines);
            Assert.StartsWith(@"E:\DCIM\p1.jpg → D:\Archive\2026\p1.jpg: ", line);
            Assert.Null(vm.CautionMessage);
            return;
        }

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        if (safe)
        {
            Assert.Null(vm.CautionMessage);
            return;
        }

        var caution = vm.CautionMessage!;
        Assert.Equal(scanErrors > 0,
            caution.Contains($"스캔할 때 읽지 못한 파일이 {scanErrors}개", StringComparison.Ordinal));
        Assert.Equal(ignored > 0,
            caution.Contains("사진·동영상이 아닌 파일 3개는 가져오지 않았습니다. 원본을 정리하기 전에 확인해 주세요", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caution_IgnoredFilesOnly_IsExactText()
    {
        var vm = ForPlan(CopyPlan(1), ignored: 62);

        await vm.StartAsync();

        Assert.Null(vm.SafeToCleanMessage);
        Assert.Equal("사진·동영상이 아닌 파일 62개는 가져오지 않았습니다. 원본을 정리하기 전에 확인해 주세요", vm.CautionMessage);
    }

    [Fact]
    public async Task SafeToClean_Hidden_ForResumeFromHistory_BecauseScanErrorsAreUnknown()
    {
        var plan = CopyPlan(1);
        var first = ForPlan(plan);
        await first.StartAsync();
        var journal = ArchiveJournals().Single();

        var vm = ForResume(journal);
        await vm.StartAsync();

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Null(vm.SafeToCleanMessage);
        Assert.NotNull(vm.CautionMessage);
    }

    // ------------------------------------------------------------------
    // 결과 제목 일곱 가지
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(RunOutcome.Completed, "정리를 마쳤습니다")]
    [InlineData(RunOutcome.CompletedWithFailures, "일부 파일을 처리하지 못했습니다")]
    [InlineData(RunOutcome.Cancelled, "취소했습니다. 이어 하거나 되돌릴 수 있습니다")]
    [InlineData(RunOutcome.Interrupted, "드라이브가 연결되어 있지 않아 멈췄습니다. 다시 연결하고 이어 하세요")]
    [InlineData(RunOutcome.BlockedByPreflight, "실행 전 점검에서 막혔습니다")]
    [InlineData(RunOutcome.PlanIncomplete, "계획 기록이 끝나지 않았습니다. 다시 계획해 주세요")]
    [InlineData(RunOutcome.UndoStarted, "이미 되돌리기를 시작한 실행이라 이어 할 수 없습니다")]
    public async Task ResultTitle_PerOutcome(RunOutcome outcome, string expectedTitle)
    {
        RunViewModel vm;
        switch (outcome)
        {
            case RunOutcome.Completed:
                vm = ForPlan(CopyPlan(1));
                break;

            case RunOutcome.CompletedWithFailures:
                var failing = CopyPlan(2);
                _fs.FailReadsOf(@"E:\DCIM\p1.jpg");
                vm = ForPlan(failing);
                break;

            case RunOutcome.Cancelled:
                var cancelling = CopyPlan(3);
                var cancelled = ForPlan(cancelling);
                _hooked.AfterMove = (source, _) =>
                {
                    if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        cancelled.Cancel();
                    }
                };
                vm = cancelled;
                break;

            case RunOutcome.Interrupted:
                var interrupting = CopyPlan(3);
                _hooked.AfterMove = (source, _) =>
                {
                    if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        _fs.RemoveVolume(@"E:\");
                    }
                };
                vm = ForPlan(interrupting);
                break;

            case RunOutcome.BlockedByPreflight:
                vm = ForPlan(PlanOf(Copy("op1", @"E:\DCIM\missing.jpg", @"D:\Archive\2026\missing.jpg", 10)));
                break;

            case RunOutcome.PlanIncomplete:
                // 계획 3개 중 1개만 기록하고 끊긴 실행.
                var torn = PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", 10),
                    Copy("op2", @"E:\DCIM\b.jpg", @"D:\Archive\2026\b.jpg", 10),
                    Copy("op3", @"E:\DCIM\c.jpg", @"D:\Archive\2026\c.jpg", 10));
                vm = ForResume(WriteJournal(expected: 3, planned: [.. torn.Operations.Take(1)]));
                break;

            default:
                // 계획은 다 기록했고 되돌리기를 시작한 실행.
                var undone = PlanOf(Copy("op1", @"E:\DCIM\a.jpg", @"D:\Archive\2026\a.jpg", 10));
                vm = ForResume(WriteJournal(expected: 1, planned: [.. undone.Operations], JournalEvent.UndoStarted));
                break;
        }

        await vm.StartAsync();

        Assert.Equal(expectedTitle, vm.ResultTitle);
        Assert.Equal(outcome == RunOutcome.Completed ? SafeMessage : null, vm.SafeToCleanMessage); // 그 밖의 결과에는 절대 없다
        Assert.Equal(outcome is RunOutcome.Cancelled or RunOutcome.Interrupted, vm.CanResume);
        if (outcome == RunOutcome.BlockedByPreflight)
        {
            Assert.Contains(vm.FailureLines, l => l.Contains("원본 파일을 찾을 수 없습니다") && l.Contains("missing.jpg"));
        }
    }

    // ------------------------------------------------------------------
    // 한 번만 시작, 취소와 이어 하기
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_CannotStartTwice()
    {
        var plan = CopyPlan(2);
        var vm = ForPlan(plan);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        var first = vm.StartAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.StartCommand.CanExecute(null));
        var second = vm.StartAsync(); // 실행 중에 또 시작해도 아무것도 하지 않는다
        await Task.WhenAll(first, second);
        await vm.StartAsync();        // 끝난 뒤에 또 시작해도 마찬가지

        Assert.Single(ArchiveJournals());
        Assert.Equal(2, _hooked.Created.Count(p => p.StartsWith(@"D:\Archive\2026\", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, busyEnded);
        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_StopsAfterCurrentFile_AndCanResume()
    {
        var plan = CopyPlan(3);
        var vm = ForPlan(plan);
        var cancelCalls = 0;
        var cancellingShown = false;
        _hooked.AfterMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase) && cancelCalls++ == 0)
            {
                vm.Cancel(); // 첫 파일의 마지막 단계 직후
                cancellingShown = vm.IsCancelling && !vm.CancelCommand.CanExecute(null); // "취소하는 중…"
            }
        };

        await vm.StartAsync();

        Assert.True(cancellingShown);

        Assert.Equal("취소했습니다. 이어 하거나 되돌릴 수 있습니다", vm.ResultTitle);
        Assert.Null(vm.SafeToCleanMessage);
        Assert.True(vm.CanResume);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsCancelling);
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p1.jpg"));
        Assert.False(_fs.FileExists(@"D:\Archive\2026\p2.jpg"));
        Assert.True(vm.ResumeCommand.CanExecute(null));

        _hooked.AfterMove = null;
        await vm.ResumeCommand.ExecuteAsync(null);

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Equal(SafeMessage, vm.SafeToCleanMessage); // 스캔 오류 수는 처음 실행에서 이어받는다
        Assert.False(vm.CanResume);
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p2.jpg"));
        Assert.True(_fs.FileExists(@"D:\Archive\2026\p3.jpg"));
        Assert.Single(ArchiveJournals());
        Assert.Equal(3, vm.Done);
        Assert.Equal(3, vm.Total);
    }

    [Fact]
    public async Task Resume_KeepsScanErrorCount_SoSafeMessageStaysHidden()
    {
        var plan = CopyPlan(2);
        var vm = ForPlan(plan, scanErrors: 2);
        _hooked.AfterMove = (source, _) =>
        {
            if (source.EndsWith(".photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                vm.Cancel();
            }
        };
        await vm.StartAsync();
        _hooked.AfterMove = null;

        await vm.ResumeCommand.ExecuteAsync(null);

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Null(vm.SafeToCleanMessage);
        Assert.Contains("2개", vm.CautionMessage);
    }

    [Fact]
    public async Task Cancel_DoesNothing_BeforeStartAndAfterFinish()
    {
        var vm = ForPlan(CopyPlan(1));

        vm.Cancel();
        Assert.False(vm.IsCancelling);

        await vm.StartAsync();
        vm.Cancel();

        Assert.False(vm.IsCancelling);
        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
    }

    // ------------------------------------------------------------------
    // 오류
    // ------------------------------------------------------------------

    [Fact]
    public async Task JournalUnavailable_ShowsTitle_LogsIt_AndStillEndsBusy()
    {
        var plan = CopyPlan(2);
        var vm = ForPlan(plan);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;
        _hooked.BeforeMove = (_, _) =>
        {
            // 기록을 남길 두 곳(아카이브, 앱 데이터)이 모두 사라진다.
            _fs.RemoveVolume(@"D:\");
            _fs.RemoveVolume(@"C:\");
        };

        await vm.StartAsync();

        Assert.Equal("기록을 남길 수 없어 실행을 멈췄습니다", vm.ResultTitle);
        Assert.Null(vm.SafeToCleanMessage);
        Assert.False(vm.CanResume);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.Contains("기록을 남길 수 없어 실행을 멈췄습니다", LogText());
    }

    [Fact]
    public async Task UnexpectedException_ShowsGenericTitle_WithoutExceptionText()
    {
        var plan = CopyPlan(1);
        var vm = ForPlan(plan);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;
        _hooked.BeforeMove = (_, _) => throw new InvalidOperationException("SECRET-DETAIL-1234");

        await vm.StartAsync();

        Assert.Equal("예기치 못한 오류로 실행을 멈췄습니다. 작업 기록에서 이어 하거나 되돌릴 수 있습니다", vm.ResultTitle);
        Assert.Null(vm.SafeToCleanMessage);
        Assert.DoesNotContain("SECRET-DETAIL-1234", vm.ResultTitle);
        Assert.All(vm.FailureLines, l => Assert.DoesNotContain("SECRET-DETAIL-1234", l));
        Assert.All(vm.WarningLines, l => Assert.DoesNotContain("SECRET-DETAIL-1234", l));
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.Contains("SECRET-DETAIL-1234", LogText());
    }

    // ------------------------------------------------------------------
    // 목록 줄이기, 진행률, 화면 이동
    // ------------------------------------------------------------------

    [Fact]
    public async Task ManyWarnings_AreCappedAt50_WithRemainderCount()
    {
        var plan = CopyPlan(60);
        for (var i = 1; i <= 60; i++)
        {
            _fs.FailSetTimeOf($@"D:\Archive\2026\p{i}.jpg.photook-tmp"); // 원본 날짜를 유지하지 못한 파일마다 경고 한 줄
        }

        var vm = ForPlan(plan);

        await vm.StartAsync();

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Equal(51, vm.WarningLines.Count);
        Assert.All(vm.WarningLines.Take(50), l => Assert.Contains("원본 날짜를 유지하지 못했습니다", l));
        Assert.Equal("외 10건", vm.WarningLines[50]);
    }

    [Fact]
    public async Task Progress_ReportsDoneAndTotal_ThroughPropertyChanges()
    {
        var plan = CopyPlan(3);
        var vm = ForPlan(plan);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            lock (changed)
            {
                changed.Add(e.PropertyName);
            }
        };

        await vm.StartAsync();

        lock (changed)
        {
            Assert.Contains(nameof(RunViewModel.Done), changed);
            Assert.Contains(nameof(RunViewModel.Total), changed);
            Assert.Contains(nameof(RunViewModel.ProgressText), changed);
            Assert.Contains(nameof(RunViewModel.ResultTitle), changed);
        }

        Assert.Equal(3, vm.Done);
        Assert.Equal(3, vm.Total);
    }

    [Fact]
    public async Task HomeAndHistory_Navigate_OnlyWhenNotBusy()
    {
        var vm = ForPlan(CopyPlan(1));
        _shell.Navigate(vm);

        var running = vm.StartAsync();
        Assert.False(vm.HomeCommand.CanExecute(null));
        Assert.False(vm.OpenHistoryCommand.CanExecute(null));
        await running;

        Assert.True(vm.OpenHistoryCommand.CanExecute(null));
        vm.OpenHistoryCommand.Execute(null);
        Assert.Same(_history, _shell.Current);

        _shell.Navigate(vm);
        vm.HomeCommand.Execute(null);
        Assert.Same(_home, _shell.Current);
    }

    // ------------------------------------------------------------------
    // 실패 원문, 점검 경고, 처리한 파일 수
    // ------------------------------------------------------------------

    [Fact]
    public async Task FailureLine_ShowsSummaryOnly_AndLogsFullMessage()
    {
        var plan = CopyPlan(2);
        _hooked.BeforeMove = (source, _) =>
        {
            if (source.EndsWith("p1.jpg.photook-tmp", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("SECRET-OS-TEXT");
            }
        };
        var vm = ForPlan(plan);

        await vm.StartAsync();

        Assert.Equal("일부 파일을 처리하지 못했습니다", vm.ResultTitle);
        var line = Assert.Single(vm.FailureLines);
        Assert.Equal(@"E:\DCIM\p1.jpg → D:\Archive\2026\p1.jpg: 파일 작업 중 오류가 발생했습니다", line);
        Assert.DoesNotContain("SECRET", line);
        Assert.All(vm.WarningLines, l => Assert.DoesNotContain("SECRET", l));
        Assert.Contains("SECRET-OS-TEXT", LogText());
        Assert.Contains(@"E:\DCIM\p1.jpg → D:\Archive\2026\p1.jpg", LogText());
    }

    [Fact]
    public async Task PreflightWarning_GoesToWarnings_NotToFailures_AndSafeMessageStays()
    {
        var longName = new string('a', 250) + ".jpg";
        var destination = $@"D:\Archive\2026\{longName}";
        Assert.True(destination.Length > 259);
        var data = AddSource(@"E:\DCIM\long.jpg", 100, 77);
        var vm = ForPlan(PlanOf(Copy("op1", @"E:\DCIM\long.jpg", destination, data.Length)));

        await vm.StartAsync();

        Assert.Equal("정리를 마쳤습니다", vm.ResultTitle);
        Assert.Equal(SafeMessage, vm.SafeToCleanMessage);
        Assert.Empty(vm.FailureLines);
        var warning = Assert.Single(vm.WarningLines);
        Assert.Contains("경로가 길어 일부 프로그램에서 열리지 않을 수 있습니다", warning);
    }

    [Fact]
    public async Task BlockingPreflightError_StaysInFailureLines()
    {
        var vm = ForPlan(PlanOf(Copy("op1", @"E:\DCIM\missing.jpg", @"D:\Archive\2026\missing.jpg", 10)));

        await vm.StartAsync();

        Assert.Contains(vm.FailureLines, l => l.Contains("원본 파일을 찾을 수 없습니다"));
        Assert.Null(vm.SucceededFilesText); // 기록을 만들지 않은 실행
    }

    [Fact]
    public async Task SucceededFilesText_CountsFilesOnly_NotFolders()
    {
        var a = AddSource(@"E:\DCIM\a.jpg", 100, 1);
        var b = AddSource(@"E:\DCIM\b.jpg", 120, 2);
        var vm = ForPlan(PlanOf(
            Mkdir("op1", @"D:\Archive\2027"),
            Copy("op2", @"E:\DCIM\a.jpg", @"D:\Archive\2027\a.jpg", a.Length),
            Copy("op3", @"E:\DCIM\b.jpg", @"D:\Archive\2027\b.jpg", b.Length)));
        Assert.Null(vm.SucceededFilesText);

        await vm.StartAsync();

        Assert.Equal(3, vm.Done); // 진행률은 폴더 만들기도 센다
        Assert.Equal("처리한 파일 2개", vm.SucceededFilesText);
    }

    [Fact]
    public async Task SucceededFilesText_ExcludesFailedFiles_AndCountsResumedOnes()
    {
        var plan = CopyPlan(3);
        _fs.FailReadsOf(@"E:\DCIM\p2.jpg");
        var vm = ForPlan(plan);

        await vm.StartAsync();

        Assert.Equal("처리한 파일 2개", vm.SucceededFilesText);

        // 기록에서 이어 하는 화면도 같은 기록으로 센다.
        var resumed = ForResume(ArchiveJournals().Single());
        await resumed.StartAsync();
        Assert.NotNull(resumed.SucceededFilesText);
    }
}
