using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.History;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 시작 화면. 연결된 이동식 드라이브와 최근 작업을 보여 주고, 가져오기·흡수·재정리의 소스 화면으로 보낸다.
/// 다음 화면은 직접 만들지 않고 생성자로 받은 팩토리로 만든다. 소스 화면의 인자는 작업 종류와 미리 채울 드라이브 루트(없으면 null),
/// 작업 기록 화면의 인자는 미리 고를 실행 ID(없으면 null), 이어 하기 화면의 인자는 기록 경로다.
/// 작업 기록은 백그라운드에서 읽는다(기록이 많거나 보관 폴더가 꺼진 네트워크 경로여도 화면이 멈추지 않게).
/// 드라이브나 기록을 읽지 못해도 예외를 밖으로 던지지 않는다(로그에 남기고 빈 목록을 보인다). 상태는 화면(UI) 스레드에서만 바꾼다.
/// </summary>
public sealed partial class StartViewModel : ObservableObject, ILeavableScreen
{
    private const int RecentRunLimit = 5;
    private const string LoadingNotice = "작업 기록을 읽는 중…";
    private const string EmptyNotice = "아직 작업 기록이 없습니다.";

    private readonly ShellViewModel _shell;
    private readonly IRemovableDrives _drives;
    private readonly IRunHistory _history;
    private readonly FileLog _log;
    private readonly Func<JobKind, string?, ObservableObject> _createSource;
    private readonly Func<string?, ObservableObject> _createHistory;
    private readonly Func<string, ObservableObject> _createResume;

    /// <summary>화면을 떠나면 취소한다(남은 기록 읽기가 이어 하기·되돌리기의 기록 덧붙이기와 겹치지 않게).</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>가장 나중 요청만 반영한다(늦게 끝난 앞선 읽기가 최신 목록을 덮지 않게).</summary>
    private int _loadGeneration;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeUnfinishedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoUnfinishedCommand))]
    private RunSummary? unfinishedRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HistoryNotice))]
    private bool isLoadingHistory;

    public StartViewModel(ShellViewModel shell, IRemovableDrives drives, IRunHistory history, FileLog log,
        Func<JobKind, string?, ObservableObject> createSource, Func<string?, ObservableObject> createHistory,
        Func<string, ObservableObject> createResume)
    {
        _shell = shell;
        _drives = drives;
        _history = history;
        _log = log;
        _createSource = createSource;
        _createHistory = createHistory;
        _createResume = createResume;
        WhenLoaded = RefreshAsync();
    }

    public ObservableCollection<DriveChoice> Drives { get; } = [];

    /// <summary>최근 작업 최대 5개(최근 것이 먼저).</summary>
    public ObservableCollection<RunSummary> RecentRuns { get; } = [];

    /// <summary>"작업 기록을 읽는 중…", 기록이 없으면 "아직 작업 기록이 없습니다.", 목록이 있으면 null.</summary>
    public string? HistoryNotice => IsLoadingHistory ? LoadingNotice : RecentRuns.Count == 0 ? EmptyNotice : null;

    /// <summary>가장 나중에 요청한 기록 읽기가 끝나면(반영·실패) 완료된다. 예외로 끝나지 않는다.</summary>
    public Task WhenLoaded { get; private set; }

    /// <summary>
    /// 드라이브 목록과 작업 기록을 다시 읽는다. 드라이브는 바로, 기록은 백그라운드에서 읽는다. 읽을 수 없는 기록은 목록에서 빠진다.
    /// 예외를 던지지 않는다.
    /// </summary>
    public Task RefreshAsync()
    {
        RefreshDrives();
        var generation = ++_loadGeneration;
        IsLoadingHistory = true;
        WhenLoaded = LoadHistoryAsync(generation);
        return WhenLoaded;
    }

    private void RefreshDrives()
    {
        IReadOnlyList<DriveChoice> drives;
        try
        {
            drives = _drives.List();
        }
        catch (Exception ex)
        {
            _log.Error("이동식 드라이브 목록을 읽지 못했습니다", ex);
            drives = [];
        }

        Drives.Clear();
        foreach (var drive in drives)
        {
            Drives.Add(drive);
        }
    }

    /// <summary>화면을 떠날 때 남은 기록 읽기를 멈춘다. 예외를 던지지 않는다.</summary>
    public void OnLeft()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (Exception ex)
        {
            _log.Error("시작 화면 정리 중 오류", ex);
        }
    }

    private async Task LoadHistoryAsync(int generation)
    {
        IReadOnlyList<RunSummary> runs;
        var ct = _lifetime.Token;
        try
        {
            var history = _history;
            (runs, _) = await Task.Run(() => history.List(ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // 화면을 떠났다
        }
        catch (Exception ex)
        {
            _log.Error("작업 기록을 읽지 못했습니다", ex);
            runs = [];
        }

        if (generation != _loadGeneration)
        {
            return;
        }

        RecentRuns.Clear();
        foreach (var run in runs.Take(RecentRunLimit))
        {
            RecentRuns.Add(run);
        }

        // 목록은 최근 것이 먼저이므로 처음 만나는 것이 가장 최근이다.
        UnfinishedRun = runs.FirstOrDefault(r => r.Status is RunStatus.Unfinished or RunStatus.UndoInProgress);
        IsLoadingHistory = false;
        OnPropertyChanged(nameof(HistoryNotice));
    }

    [RelayCommand]
    private void StartImport() => _shell.Navigate(_createSource(JobKind.Import, null));

    [RelayCommand]
    private void ImportFromDrive(DriveChoice drive) => _shell.Navigate(_createSource(JobKind.Import, drive.Root));

    /// <summary>흩어진 폴더를 아카이브로 합친다.</summary>
    [RelayCommand]
    private void StartAbsorb() => _shell.Navigate(_createSource(JobKind.Absorb, null));

    /// <summary>아카이브 안의 폴더를 프리셋에 맞게 다시 정리한다.</summary>
    [RelayCommand]
    private void StartReorganize() => _shell.Navigate(_createSource(JobKind.Reorganize, null));

    [RelayCommand]
    private void OpenHistory() => _shell.Navigate(_createHistory(null));

    /// <summary>끝나지 않은 작업 안내의 "이어 하기". 이어 할 수 있는 상태(끝나지 않음)일 때만 쓸 수 있다.</summary>
    [RelayCommand(CanExecute = nameof(CanResumeUnfinished))]
    private void ResumeUnfinished()
    {
        if (UnfinishedRun is { CanResume: true } run)
        {
            _shell.Navigate(_createResume(run.JournalPath));
        }
    }

    /// <summary>끝나지 않은 작업 안내의 "되돌리기". 작업 기록 화면에서 그 실행을 고른 채로 열어 확인을 거치게 한다.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoUnfinished))]
    private void UndoUnfinished()
    {
        if (UnfinishedRun is { CanUndo: true } run)
        {
            _shell.Navigate(_createHistory(run.RunId));
        }
    }

    private bool CanResumeUnfinished() => UnfinishedRun is { CanResume: true };

    private bool CanUndoUnfinished() => UnfinishedRun is { CanUndo: true };
}
