using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 가져올 폴더를 읽는 화면. 끝나면 미리보기 화면으로, 취소하면 가져오기 화면으로 간다.
/// 다음·이전 화면은 직접 만들지 않고 생성자로 받은 팩토리로 만든다. 디스크는 읽기만 한다.
/// </summary>
public sealed partial class ScanViewModel : ObservableObject, IBusyViewModel
{
    private const string ErrorTitle = "스캔하지 못했습니다";
    private const string ErrorMessage = "예기치 못한 오류로 스캔을 멈췄습니다. 자세한 내용은 로그 폴더에 남겼습니다.";

    private readonly ShellViewModel _shell;
    private readonly ImportWorkflow _workflow;
    private readonly ImportSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly FileLog _log;
    private readonly Func<ScanOutcome, ObservableObject> _createPreview;
    private readonly Func<ObservableObject> _createSource;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesReadText))]
    private int filesRead;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool isCancelling;

    public ScanViewModel(ShellViewModel shell, ImportWorkflow workflow, ImportSettings settings, IDialogService dialogs,
        FileLog log, Func<ScanOutcome, ObservableObject> createPreview, Func<ObservableObject> createSource)
    {
        _shell = shell;
        _workflow = workflow;
        _settings = settings;
        _dialogs = dialogs;
        _log = log;
        _createPreview = createPreview;
        _createSource = createSource;
        SourcesText = string.Join(", ", settings.Sources);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy && !IsCancelling);
    }

    public event EventHandler? BusyEnded;

    public IRelayCommand CancelCommand { get; }

    /// <summary>읽는 폴더들.</summary>
    public string SourcesText { get; }

    /// <summary>진행률은 미디어 파일을 읽은 수다(목록을 먼저 다 모은 뒤 읽는다).</summary>
    public string FilesReadText => string.Create(CultureInfo.InvariantCulture, $"읽은 파일 {FilesRead}개");

    /// <summary>
    /// 스캔한다. 끝나면 미리보기 화면으로, 취소되면 가져오기 화면으로 간다. 예기치 못한 오류는 로그에 남기고
    /// 한국어 안내를 보인 뒤 가져오기 화면으로 돌아간다. 예외를 밖으로 던지지 않는다.
    /// </summary>
    public async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            // 이 화면으로 바꾸는 호출이 끝난 뒤에 다음 화면으로 가도록 한 번 양보한다.
            await Task.Yield();

            var progress = new Progress<int>(n => FilesRead = n);
            var scan = await _workflow.ScanAsync(_settings, progress, _cts.Token);
            _shell.Navigate(_createPreview(scan));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _shell.Navigate(_createSource());
        }
        catch (Exception ex)
        {
            _log.Error("스캔하지 못했습니다", ex);
            TryNavigate(_createSource);
            _dialogs.ShowError(ErrorTitle, ErrorMessage);
        }
        finally
        {
            IsBusy = false;
            BusyEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>지금 읽는 파일까지 마치고 멈추도록 요청한다. 예외를 던지지 않는다.</summary>
    public void Cancel()
    {
        if (!IsBusy)
        {
            return;
        }

        IsCancelling = true;
        try
        {
            _cts.Cancel();
        }
        catch (AggregateException ex)
        {
            _log.Error("스캔 취소 중 오류", ex);
        }
    }

    /// <summary>
    /// 오류 처리 도중이라 화면 팩토리가 또 실패해도 안내 창은 띄울 수 있게 한다.
    /// </summary>
    private void TryNavigate(Func<ObservableObject> create)
    {
        try
        {
            _shell.Navigate(create());
        }
        catch (Exception ex)
        {
            _log.Error("가져오기 화면으로 돌아가지 못했습니다", ex);
        }
    }
}
