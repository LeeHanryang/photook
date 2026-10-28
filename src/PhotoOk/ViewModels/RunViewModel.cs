using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.Execution;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 계획을 실행하고(또는 끝나지 않은 실행을 이어 하고) 진행률과 결과를 보여 주는 화면.
/// 파일은 <see cref="Executor"/>만 건드린다. 이 화면은 실행기를 백그라운드에서 부르고 결과를 한국어로 풀어 줄 뿐이다.
/// "검증 완료 — 원본을 직접 정리해도 안전합니다"는 실행이 실패 없이 끝까지 마쳤고(<see cref="RunOutcome.Completed"/>, 실패 0)
/// 스캔 때 읽지 못한 파일도, 가져오지 않은 파일(사진·동영상이 아닌 파일)도, 가져오지 않은 하위 폴더도 없다고 알 때만 보인다.
/// 그것을 모르는 경우(기록에서 이어 하기)에는 보이지 않는다.
/// 상태는 화면(UI) 스레드에서만 바꾼다.
/// </summary>
public sealed partial class RunViewModel : ObservableObject, IBusyViewModel
{
    private const string SafeMessage = "검증 완료 — 원본을 직접 정리해도 안전합니다";
    private const string SubfoldersNotImportedMessage = "하위 폴더의 파일은 가져오지 않았습니다. 원본을 정리하기 전에 확인해 주세요";
    private const string JournalUnavailableTitle = "기록을 남길 수 없어 실행을 멈췄습니다. 작업 기록에서 이어 하거나 되돌릴 수 있습니다";
    private const string UnexpectedErrorTitle = "예기치 못한 오류로 실행을 멈췄습니다. 작업 기록에서 이어 하거나 되돌릴 수 있습니다";
    private const int LineLimit = 50;
    private const string FailureDetailSeparator = " — ";

    private readonly ShellViewModel _shell;
    private readonly Executor _executor;
    private readonly IFileSystem _fileSystem;
    private readonly FileLog _log;
    private readonly Func<ObservableObject> _createHome;
    private readonly Func<ObservableObject> _createHistory;
    private readonly Plan? _plan;
    private readonly string? _journalPath;

    /// <summary>스캔 때 읽지 못한 파일 수, 가져오지 않은 파일 수, 가져오지 않은 하위 폴더가 있는지. 기록에서 이어 하는 화면은 모른다(null).</summary>
    private readonly ScanFacts? _scan;

    private CancellationTokenSource? _cts;
    private bool _started;
    private ExecutionResult? _result;
    private string? _faultTitle;
    private IReadOnlyList<string> _failureLines = [];
    private IReadOnlyList<string> _warningLines = [];

    /// <summary>지난 결과의 알림 전체(50줄로 자르기 전). 화면 안에서 이어 하면 앞부분의 알림을 잃지 않도록 새 알림 앞에 붙인다.</summary>
    private IReadOnlyList<string> _allWarnings = [];

    /// <summary>이번 실행이 이어받을 앞부분의 알림. 처음 실행에는 없다.</summary>
    private IReadOnlyList<string> _carriedWarnings = [];
    private int? _succeededFiles;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFileName))]
    private string? currentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(HomeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenHistoryCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isCancelling;

    private RunViewModel(ShellViewModel shell, Executor executor, IFileSystem fileSystem, FileLog log, Func<ObservableObject> createHome,
        Func<ObservableObject> createHistory, Plan? plan, string? journalPath, ScanFacts? scan)
    {
        _shell = shell;
        _executor = executor;
        _fileSystem = fileSystem;
        _log = log;
        _createHome = createHome;
        _createHistory = createHistory;
        _plan = plan;
        _journalPath = journalPath;
        _scan = scan;

        StartCommand = new AsyncRelayCommand(StartAsync, () => !_started && !IsBusy);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => CanResume);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && !IsCancelling);
        HomeCommand = new RelayCommand(() => _shell.Navigate(_createHome()), () => !IsBusy);
        OpenHistoryCommand = new RelayCommand(() => _shell.Navigate(_createHistory()), () => !IsBusy);
    }

    /// <summary>
    /// 미리보기에서 만든 계획을 실행하는 화면. <paramref name="scan"/>은 그 계획을 세운 스캔의 사실(읽지 못한 파일 수,
    /// 사진·동영상이 아니어서 가져오지 않은 파일 수, 가져오지 않은 하위 폴더가 있는지).
    /// </summary>
    public static RunViewModel ForPlan(ShellViewModel shell, Executor executor, IFileSystem fileSystem, FileLog log, Func<ObservableObject> createHome,
        Func<ObservableObject> createHistory, Plan plan, ScanFacts scan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scan);
        return new RunViewModel(shell, executor, fileSystem, log, createHome, createHistory, plan, journalPath: null, scan);
    }

    /// <summary>끝나지 않은 실행을 작업 기록으로 이어 하는 화면. 스캔 때 읽지 못한 파일 수를 모르므로 "안전" 문구는 보이지 않는다.</summary>
    public static RunViewModel ForResume(ShellViewModel shell, Executor executor, IFileSystem fileSystem, FileLog log, Func<ObservableObject> createHome,
        Func<ObservableObject> createHistory, string journalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        return new RunViewModel(shell, executor, fileSystem, log, createHome, createHistory, plan: null, journalPath, scan: null);
    }

    public event EventHandler? BusyEnded;

    public IAsyncRelayCommand StartCommand { get; }

    public IAsyncRelayCommand ResumeCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand HomeCommand { get; }

    public IRelayCommand OpenHistoryCommand { get; }

    /// <summary>"끝난 수 / 전체 수".</summary>
    public string ProgressText => string.Create(CultureInfo.InvariantCulture, $"{Done} / {Total}");

    /// <summary>방금 끝낸 파일의 이름(폴더 제외).</summary>
    public string? CurrentFileName => string.IsNullOrEmpty(CurrentPath) ? null : Path.GetFileName(CurrentPath);

    /// <summary>끝나기 전에는 null.</summary>
    public string? ResultTitle => _faultTitle ?? (_result is null ? null : TitleOf(_result.Outcome));

    /// <summary>실패 없이 끝까지 마쳤고 스캔 오류도, 가져오지 않은 파일도, 가져오지 않은 하위 폴더도 없다고 알 때만 문구가 있고, 아니면 null.</summary>
    public string? SafeToCleanMessage =>
        CompletedWithoutFailures && _scan is { ScanErrors: 0, IgnoredFiles: 0, SubfoldersNotImported: false } ? SafeMessage : null;

    /// <summary>
    /// 실패 없이 끝났는데도 "안전" 문구를 못 보이는 까닭(스캔 때 읽지 못한 파일이나 가져오지 않은 파일·하위 폴더가 있거나, 있었는지 모른다).
    /// 까닭이 여럿이면 줄을 바꿔 모두 보인다. 그 밖에는 null.
    /// </summary>
    public string? CautionMessage
    {
        get
        {
            if (!CompletedWithoutFailures)
            {
                return null;
            }

            if (_scan is null)
            {
                return "이 기록은 스캔할 때 읽지 못한 파일이나 가져오지 않은 파일이 있었는지 알 수 없습니다. 원본을 직접 정리하기 전에 확인해 주세요";
            }

            var lines = new List<string>();
            if (_scan.ScanErrors is > 0 and var errors)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"스캔할 때 읽지 못한 파일이 {errors}개 있습니다. 원본을 직접 정리하기 전에 그 파일을 확인해 주세요"));
            }

            if (_scan.IgnoredFiles is > 0 and var ignored)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"사진·동영상이 아닌 파일 {ignored}개는 가져오지 않았습니다. 원본을 정리하기 전에 확인해 주세요"));
            }

            if (_scan.SubfoldersNotImported)
            {
                lines.Add(SubfoldersNotImportedMessage);
            }

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    private bool CompletedWithoutFailures => _result is { Outcome: RunOutcome.Completed, Failures.Count: 0 };

    /// <summary>"처리한 파일 N개". 폴더 만들기는 세지 않는다. 기록을 읽지 못했거나 기록이 없으면 null.</summary>
    public string? SucceededFilesText => _succeededFiles is { } count
        ? string.Create(CultureInfo.InvariantCulture, $"처리한 파일 {count}개")
        : null;

    /// <summary>
    /// "원본 → 대상: 까닭"(까닭은 요약만; 원문은 로그). 실행 전 점검에서 막힌 까닭(오류)도 여기에 담는다.
    /// 최대 50줄 뒤에 "외 N건".
    /// </summary>
    public IReadOnlyList<string> FailureLines => _failureLines;

    /// <summary>실행기의 알림과 실행 전 점검 경고. 최대 50줄 뒤에 "외 N건".</summary>
    public IReadOnlyList<string> WarningLines => _warningLines;

    /// <summary>취소되었거나 드라이브가 빠져 멈춘 실행을 기록으로 이어 할 수 있는지.</summary>
    public bool CanResume =>
        !IsBusy && _result is { Outcome: RunOutcome.Cancelled or RunOutcome.Interrupted, JournalPath: not null };

    /// <summary>
    /// 실행한다. 한 번만 시작하며, 이미 시작했다면(실행 중이든 끝났든) 아무것도 하지 않는다.
    /// 예외를 밖으로 던지지 않는다(로그에 남기고 한국어 안내를 결과 제목으로 보인다).
    /// </summary>
    public Task StartAsync()
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        StartCommand.NotifyCanExecuteChanged();

        if (_plan is { } plan)
        {
            return RunAsync((ct, progress) => _executor.Execute(plan, ct, progress));
        }

        var journalPath = _journalPath!;
        return RunAsync((ct, progress) => _executor.Resume(journalPath, ct, progress));
    }

    /// <summary>지금 파일까지 마치고 멈추도록 요청한다. 예외를 던지지 않는다.</summary>
    public void Cancel()
    {
        if (!IsBusy)
        {
            return;
        }

        IsCancelling = true;
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _log.Error("실행 취소 요청 중 오류", ex);
        }
    }

    private Task ResumeAsync()
    {
        if (!CanResume)
        {
            return Task.CompletedTask;
        }

        var journalPath = _result!.JournalPath!;
        _carriedWarnings = _allWarnings;
        return RunAsync((ct, progress) => _executor.Resume(journalPath, ct, progress));
    }

    private async Task RunAsync(Func<CancellationToken, IProgress<ExecutionProgress>, ExecutionResult> work)
    {
        ClearResult();
        IsCancelling = false;
        IsBusy = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var progress = new ProgressRelay(ApplyProgress);
        try
        {
            var (result, succeededFiles) = await Task.Run(() =>
            {
                var finished = work(cts.Token, progress);
                return (finished, CountSucceededFiles(finished));
            });

            // 늦게 도착하는 진행률 알림이 있어도 마지막 보고로 맞춰 둔다.
            if (progress.Latest is { } latest)
            {
                ApplyProgress(latest);
            }

            ShowResult(result, succeededFiles);
        }
        catch (JournalUnavailableException ex)
        {
            _log.Error(JournalUnavailableTitle, ex);
            Fault(JournalUnavailableTitle);
        }
        catch (Exception ex)
        {
            _log.Error("실행 중 예기치 못한 오류", ex);
            Fault(UnexpectedErrorTitle);
        }
        finally
        {
            _cts = null;
            IsCancelling = false;
            IsBusy = false;
            BusyEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>진행률은 한 방향으로만 늘어난다. 먼저 보낸 알림이 나중에 도착해도 화면이 뒤로 가지 않게 한다.</summary>
    private void ApplyProgress(ExecutionProgress progress)
    {
        if (progress.Done < Done)
        {
            return;
        }

        Total = progress.Total; // 진행 막대의 최댓값을 먼저 맞춘다.
        Done = progress.Done;
        CurrentPath = progress.CurrentPath;
    }

    private void ClearResult()
    {
        _result = null;
        _faultTitle = null;
        _failureLines = [];
        _warningLines = [];
        _allWarnings = [];
        _succeededFiles = null;
        NotifyResultChanged();
    }

    private void ShowResult(ExecutionResult result, int? succeededFiles)
    {
        _result = result;
        _succeededFiles = succeededFiles;

        // 화면에는 까닭의 요약(" — " 앞)만 보인다. 운영체제 예외 원문은 뒤에 붙어 있으므로 로그에만 남긴다.
        var failures = result.Failures.Select(f => (Full: FormatFailure(f, f.Message), Shown: FormatFailure(f, Summary(f.Message)))).ToList();
        if (failures.Count > 0)
        {
            _log.Warning("처리하지 못한 파일" + Environment.NewLine + string.Join(Environment.NewLine, failures.Select(f => f.Full)));
        }

        // 실행 전 점검은 막는 오류만 "처리하지 못한 항목"이다. 경고(긴 경로 등)는 실행을 막지 않으므로 알림으로 보인다.
        var blockers = result.PreflightIssues.Where(i => i.Severity == Severity.Error).Select(i => FormatIssue(i));
        var issueWarnings = result.PreflightIssues.Where(i => i.Severity == Severity.Warning).Select(i => FormatIssue(i));

        // 실행기 알림도 실패처럼 요약만 보이고 원문은 로그에 남긴다(앞으로 예외 원문이 붙는 알림이 생겨도 화면에 새지 않게).
        var executorWarnings = result.Warnings.Select(w => (Full: w, Shown: WarningSummary(w))).ToList();
        if (executorWarnings.Any(w => !ReferenceEquals(w.Full, w.Shown)))
        {
            _log.Warning("실행 알림" + Environment.NewLine + string.Join(Environment.NewLine, executorWarnings.Select(w => w.Full)));
        }

        _failureLines = Cap(failures.Select(f => f.Shown).Concat(blockers).ToList());
        _allWarnings = _carriedWarnings.Concat(issueWarnings).Concat(executorWarnings.Select(w => w.Shown))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _carriedWarnings = [];
        _warningLines = Cap(_allWarnings);
        NotifyResultChanged();
    }

    private static string FormatFailure(OperationFailure failure, string message) =>
        failure.Source is null ? $"{failure.Destination}: {message}" : $"{failure.Source} → {failure.Destination}: {message}";

    private static string FormatIssue(PreflightIssue issue) => issue.Path is null ? issue.Message : $"{issue.Path}: {issue.Message}";

    /// <summary>실행기의 실패 메시지는 "한국어 요약 — 운영체제 메시지"다. 요약만 돌려준다.</summary>
    private static string Summary(string message)
    {
        var cut = message.IndexOf(FailureDetailSeparator, StringComparison.Ordinal);
        return cut < 0 ? message : message[..cut];
    }

    /// <summary>
    /// 실행기 알림("한국어 요약: 경로" 또는 "한국어 요약 — 원문")의 요약. " — "가 첫 ": "보다 앞에 있을 때만 자른다
    /// (경로에 " — "가 들어 있어도 경로는 자르지 않는다). 자를 것이 없으면 같은 문자열을 그대로 돌려준다.
    /// </summary>
    internal static string WarningSummary(string warning)
    {
        var cut = warning.IndexOf(FailureDetailSeparator, StringComparison.Ordinal);
        var colon = warning.IndexOf(": ", StringComparison.Ordinal);
        return cut >= 0 && (colon < 0 || cut < colon) ? warning[..cut] : warning;
    }

    /// <summary>
    /// 기록에서 폴더 만들기를 뺀 작업 중 끝난(완료·이미 있어 건너뜀) 수. 기록을 읽지 못하면 null(그 줄은 보이지 않는다).
    /// 이어 하기는 이전 실행에서 끝낸 파일도 센다. 실행기가 일하는 스레드에서 부른다.
    /// </summary>
    private int? CountSucceededFiles(ExecutionResult result)
    {
        if (result.JournalPath is null)
        {
            return null;
        }

        try
        {
            return JournalReader.Load(_fileSystem, result.JournalPath).Operations
                .Count(o => o.Operation.Kind != OperationKind.CreateDirectory && o.Status is OpStatus.Completed or OpStatus.Skipped);
        }
        catch (Exception ex)
        {
            // 어떤 오류든 이 줄만 숨긴다. 이미 끝난 실행의 결과를 통째로 버리지 않는다.
            _log.Error("처리한 파일 수를 기록에서 읽지 못했습니다", ex);
            return null;
        }
    }

    private void Fault(string title)
    {
        // 이어 하다 멈췄어도 앞부분의 알림은 계속 보인다.
        _allWarnings = _carriedWarnings;
        _carriedWarnings = [];
        _warningLines = Cap(_allWarnings);
        _faultTitle = title;
        NotifyResultChanged();
    }

    private void NotifyResultChanged()
    {
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(SafeToCleanMessage));
        OnPropertyChanged(nameof(CautionMessage));
        OnPropertyChanged(nameof(SucceededFilesText));
        OnPropertyChanged(nameof(FailureLines));
        OnPropertyChanged(nameof(WarningLines));
        OnPropertyChanged(nameof(CanResume));
        ResumeCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<string> Cap(IReadOnlyList<string> lines) =>
        lines.Count <= LineLimit
            ? lines
            : [.. lines.Take(LineLimit), string.Create(CultureInfo.InvariantCulture, $"외 {lines.Count - LineLimit}건")];

    private static string TitleOf(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Completed => "정리를 마쳤습니다",
        RunOutcome.CompletedWithFailures => "일부 파일을 처리하지 못했습니다",
        RunOutcome.Cancelled => "취소했습니다. 이어 하거나 되돌릴 수 있습니다",
        RunOutcome.Interrupted => "드라이브가 연결되어 있지 않아 멈췄습니다. 다시 연결하고 이어 하세요",
        RunOutcome.BlockedByPreflight => "실행 전 점검에서 막혔습니다",
        RunOutcome.PlanIncomplete => "계획 기록이 끝나지 않았습니다. 다시 계획해 주세요",
        RunOutcome.UndoStarted => "이미 되돌리기를 시작한 실행이라 이어 할 수 없습니다",
        _ => UnexpectedErrorTitle,
    };

    /// <summary>
    /// 실행기가 일하는 스레드에서 오는 진행률을 화면(UI) 스레드로 넘긴다(<see cref="Progress{T}"/>가 만든 곳의 동기화 컨텍스트로 알린다).
    /// 마지막 보고는 따로 기억해, 알림이 늦게 도착해도 끝난 뒤 화면을 맞출 수 있게 한다.
    /// </summary>
    private sealed class ProgressRelay(Action<ExecutionProgress> apply) : IProgress<ExecutionProgress>
    {
        private readonly IProgress<ExecutionProgress> _toUi = new Progress<ExecutionProgress>(apply);
        private ExecutionProgress? _latest;

        public ExecutionProgress? Latest => Volatile.Read(ref _latest);

        public void Report(ExecutionProgress value)
        {
            Volatile.Write(ref _latest, value);
            _toUi.Report(value);
        }
    }
}
