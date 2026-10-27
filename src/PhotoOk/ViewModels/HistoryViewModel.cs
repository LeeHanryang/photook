using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.Execution;
using PhotoOk.Core.History;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 작업 기록 화면. 지난 실행을 보여 주고, 끝나지 않은 실행을 이어 하거나 실행 하나를 되돌린다.
/// 파일은 <see cref="Undoer"/>(와 이어 하는 화면의 실행기)만 건드린다. 이 화면은 기록을 읽고 되돌리기를 백그라운드에서 부를 뿐이다.
/// 되돌리기는 항상 먼저 묻고, 아니라고 하면 아무것도 바꾸지 않는다. 복사본은 지우지 않고 보관 폴더로 옮겨진다.
/// 이어 하기 화면과 시작 화면은 직접 만들지 않고 생성자로 받은 팩토리로 만든다. 상태는 화면(UI) 스레드에서만 바꾼다.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject, IBusyViewModel
{
    private const string UndoTitle = "되돌리기";
    private const string UndoFaultMessage = "예기치 못한 오류로 되돌리기를 멈췄습니다. 자세한 내용은 로그 폴더에 남겼습니다. 기록이 남아 있어 다시 되돌릴 수 있습니다.";
    private const string JournalUnavailableMessage = "기록을 남길 수 없어 되돌리기를 멈췄습니다. 기록 폴더를 확인한 뒤 다시 시도해 주세요.";
    private const string LoadFaultMessage = "작업 기록을 읽지 못했습니다";
    private const string DetailSeparator = " — ";
    private const int LineLimit = 50;

    private readonly ShellViewModel _shell;
    private readonly RunHistory _history;
    private readonly Undoer _undoer;
    private readonly IDialogService _dialogs;
    private readonly FileLog _log;
    private readonly Func<string, ObservableObject> _createResume;
    private readonly Func<ObservableObject> _createHome;

    private CancellationTokenSource? _cts;
    private bool _refreshing;
    private string? _resultRunId;
    private IReadOnlyList<string> _undoResultLines = [];
    private IReadOnlyList<string> _loadErrors = [];
    private string? _undoSummary;
    private string? _undoFault;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private RunSummary? selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(HomeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isCancelling;

    /// <param name="selectRunId">처음부터 골라 둘 실행 ID. 없거나 목록에 없으면 고르지 않는다.</param>
    public HistoryViewModel(ShellViewModel shell, RunHistory history, Undoer undoer, IDialogService dialogs, FileLog log,
        Func<string, ObservableObject> createResume, Func<ObservableObject> createHome, string? selectRunId = null)
    {
        _shell = shell;
        _history = history;
        _undoer = undoer;
        _dialogs = dialogs;
        _log = log;
        _createResume = createResume;
        _createHome = createHome;

        ResumeCommand = new AsyncRelayCommand(ResumeAsync, () => !IsBusy && Selected is { CanResume: true });
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !IsBusy && Selected is { CanUndo: true });
        HomeCommand = new RelayCommand(() => _shell.Navigate(_createHome()), () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && !IsCancelling);

        Refresh(selectRunId);
    }

    public event EventHandler? BusyEnded;

    /// <summary>실행 ID 내림차순(최근 실행이 먼저).</summary>
    public ObservableCollection<RunSummary> Runs { get; } = [];

    /// <summary>"이어 하기": 고른 실행이 끝나지 않았을 때만. 실행 화면으로 가서 이어 한다.</summary>
    public IAsyncRelayCommand ResumeCommand { get; }

    /// <summary>"되돌리기": 되돌릴 수 있는 실행을 골랐을 때만. 먼저 묻는다.</summary>
    public IAsyncRelayCommand UndoCommand { get; }

    public IRelayCommand HomeCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>"경로: 사유"(사유는 요약만; 원문은 로그). 최대 50줄 뒤에 "외 N건".</summary>
    public IReadOnlyList<string> UndoResultLines => _undoResultLines;

    /// <summary>"되돌린 작업 N개, 남긴 항목 M개". 되돌리기를 마친 뒤에만 있고, 그 밖에는 null.</summary>
    public string? UndoSummary => _undoSummary;

    /// <summary>되돌리기를 끝까지 하지 못했을 때의 한국어 안내(예외 원문은 로그에만). 그 밖에는 null.</summary>
    public string? UndoFault => _undoFault;

    /// <summary>읽을 수 없어 목록에서 뺀 기록의 안내. 한국어 문구만 보인다.</summary>
    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public string StatusText(RunStatus s) => StatusOf(s);

    internal static string StatusOf(RunStatus s) => s switch
    {
        RunStatus.Completed => "완료",
        RunStatus.CompletedWithFailures => "일부 실패",
        RunStatus.Unfinished => "끝나지 않음",
        RunStatus.PlanIncomplete => "계획 미완료 — 다시 계획해 주세요",
        RunStatus.UndoInProgress => "되돌리는 중",
        RunStatus.Undone => "되돌림",
        _ => string.Empty,
    };

    /// <summary>
    /// 기록을 다시 읽는다. 읽던 실행(<paramref name="keepRunId"/>, 없으면 지금 고른 실행)이 아직 있으면 계속 고른 상태로 둔다.
    /// 읽지 못해도 예외를 던지지 않는다(로그에 남기고 한국어 안내만 보인다).
    /// </summary>
    public void Refresh(string? keepRunId = null)
    {
        var keep = keepRunId ?? Selected?.RunId;
        _refreshing = true;
        try
        {
            IReadOnlyList<RunSummary> runs;
            IReadOnlyList<string> errorLines;
            try
            {
                var (found, errors) = _history.List();
                runs = found;
                errorLines = errors.Select(e => $"{e.JournalPath}: {e.Message}").ToList();
            }
            catch (Exception ex)
            {
                _log.Error("작업 기록을 읽지 못했습니다", ex);
                runs = [];
                errorLines = [LoadFaultMessage];
            }

            SetLoadErrors(errorLines);

            Runs.Clear();
            foreach (var run in runs)
            {
                Runs.Add(run);
            }

            Selected = keep is null ? null : Runs.FirstOrDefault(r => r.RunId == keep);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>하고 있는 되돌리기를 지금 파일까지 마치고 멈추도록 요청한다. 예외를 던지지 않는다.</summary>
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
            _log.Error("되돌리기 취소 요청 중 오류", ex);
        }
    }

    partial void OnSelectedChanged(RunSummary? value)
    {
        // 다른 실행을 고르면 지난 되돌리기 결과는 더 이상 이 실행의 것이 아니다. 목록을 다시 읽는 동안의 변화는 무시한다.
        if (!_refreshing && value?.RunId != _resultRunId)
        {
            SetUndoResult(null, [], null, null);
        }
    }

    private Task ResumeAsync()
    {
        if (!IsBusy && Selected is { CanResume: true } run)
        {
            _shell.Navigate(_createResume(run.JournalPath));
        }

        return Task.CompletedTask;
    }

    private async Task UndoAsync()
    {
        if (IsBusy || Selected is not { CanUndo: true } run)
        {
            return;
        }

        var started = run.StartedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (!_dialogs.Confirm(UndoTitle, $@"{started} 실행을 되돌릴까요? 복사본은 보관 폴더의 .photook\되돌림으로 옮겨집니다."))
        {
            return;
        }

        SetUndoResult(null, [], null, null);
        IsCancelling = false;
        IsBusy = true;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var result = await Task.Run(() => _undoer.Undo(run.JournalPath, cts.Token));
            ShowUndoResult(run.RunId, result);
        }
        catch (JournalUnavailableException ex)
        {
            _log.Error("되돌리기 기록을 남길 수 없습니다", ex);
            SetUndoResult(run.RunId, [], null, JournalUnavailableMessage);
        }
        catch (Exception ex)
        {
            _log.Error("되돌리는 중 예기치 못한 오류", ex);
            SetUndoResult(run.RunId, [], null, UndoFaultMessage);
        }
        finally
        {
            try
            {
                Refresh(run.RunId);
            }
            catch (Exception ex)
            {
                _log.Error("되돌린 뒤 작업 기록을 다시 읽지 못했습니다", ex);
            }
            finally
            {
                _cts = null;
                IsCancelling = false;
                IsBusy = false;
                BusyEnded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ShowUndoResult(string runId, UndoResult result)
    {
        // 화면에는 사유의 요약(" — " 앞)만 보인다. 운영체제 예외 원문은 뒤에 붙어 있을 수 있으므로 원문은 로그에만 남긴다.
        var full = result.Skipped.Select(s => $"{s.Path}: {s.Reason}").ToList();
        if (full.Count > 0)
        {
            _log.Warning("되돌리지 않고 남긴 항목" + Environment.NewLine + string.Join(Environment.NewLine, full));
        }

        var shown = result.Skipped.Select(s => $"{s.Path}: {Summary(s.Reason)}").ToList();
        var summary = string.Create(CultureInfo.InvariantCulture, $"되돌린 작업 {result.Undone}개, 남긴 항목 {result.Skipped.Count}개");
        SetUndoResult(runId, Cap(shown), summary, null);
    }

    private void SetUndoResult(string? runId, IReadOnlyList<string> lines, string? summary, string? fault)
    {
        _resultRunId = runId;
        _undoResultLines = lines;
        _undoSummary = summary;
        _undoFault = fault;
        OnPropertyChanged(nameof(UndoResultLines));
        OnPropertyChanged(nameof(UndoSummary));
        OnPropertyChanged(nameof(UndoFault));
    }

    private void SetLoadErrors(IReadOnlyList<string> lines)
    {
        _loadErrors = Cap(lines);
        OnPropertyChanged(nameof(LoadErrors));
    }

    private static string Summary(string reason)
    {
        var cut = reason.IndexOf(DetailSeparator, StringComparison.Ordinal);
        return cut < 0 ? reason : reason[..cut];
    }

    private static IReadOnlyList<string> Cap(IReadOnlyList<string> lines) =>
        lines.Count <= LineLimit
            ? lines
            : [.. lines.Take(LineLimit), string.Create(CultureInfo.InvariantCulture, $"외 {lines.Count - LineLimit}건")];
}
