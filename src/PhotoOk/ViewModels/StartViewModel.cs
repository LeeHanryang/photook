using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.History;
using PhotoOk.Core.IO;

namespace PhotoOk.ViewModels;

/// <summary>
/// 시작 화면. 연결된 이동식 드라이브와 최근 작업을 보여 주고, 가져오기 화면으로 보낸다.
/// 가져오기 화면은 직접 만들지 않고 생성자로 받은 팩토리로 만든다(인자는 미리 채울 드라이브 루트, 없으면 null).
/// </summary>
public sealed partial class StartViewModel : ObservableObject
{
    private const int RecentRunLimit = 5;

    private readonly ShellViewModel _shell;
    private readonly IRemovableDrives _drives;
    private readonly RunHistory _history;
    private readonly Func<string?, ObservableObject> _createSource;

    [ObservableProperty]
    private RunSummary? unfinishedRun;

    public StartViewModel(ShellViewModel shell, IRemovableDrives drives, RunHistory history,
        Func<string?, ObservableObject> createSource)
    {
        _shell = shell;
        _drives = drives;
        _history = history;
        _createSource = createSource;
        Refresh();
    }

    public ObservableCollection<DriveChoice> Drives { get; } = [];

    /// <summary>최근 작업 최대 5개(최근 것이 먼저).</summary>
    public ObservableCollection<RunSummary> RecentRuns { get; } = [];

    /// <summary>드라이브 목록과 작업 기록을 다시 읽는다. 읽을 수 없는 기록은 목록에서 빠진다.</summary>
    public void Refresh()
    {
        Drives.Clear();
        foreach (var drive in _drives.List())
        {
            Drives.Add(drive);
        }

        var (runs, _) = _history.List();
        RecentRuns.Clear();
        foreach (var run in runs.Take(RecentRunLimit))
        {
            RecentRuns.Add(run);
        }

        // 목록은 최근 것이 먼저이므로 처음 만나는 것이 가장 최근이다.
        UnfinishedRun = runs.FirstOrDefault(r => r.Status is RunStatus.Unfinished or RunStatus.UndoInProgress);
    }

    [RelayCommand]
    private void StartImport() => _shell.Navigate(_createSource(null));

    [RelayCommand]
    private void ImportFromDrive(DriveChoice drive) => _shell.Navigate(_createSource(drive.Root));
}
