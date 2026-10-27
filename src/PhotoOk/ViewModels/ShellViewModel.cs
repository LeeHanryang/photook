using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 창 하나에 지금 화면(<see cref="Current"/>)을 바꿔 끼운다. 화면 ViewModel은 다음 화면을 직접 만들지 않고,
/// 생성자로 받은 팩토리 델리게이트로 만든 화면을 <see cref="Navigate"/>에 넘긴다(조립은 App).
/// </summary>
public sealed partial class ShellViewModel(IDialogService dialogs) : ObservableObject
{
    private const string BusyCloseTitle = "실행 중입니다";
    private const string BusyCloseMessage = "지금 파일까지 마치고 멈춘 뒤 닫을까요?";
    private const string StoppingCloseTitle = "멈추는 중입니다";
    private const string StoppingCloseMessage = "지금 파일을 마치는 중입니다. 그래도 바로 닫을까요? 기록이 남아 있어 나중에 이어 하거나 되돌릴 수 있습니다.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private ObservableObject? current;

    /// <summary>닫기를 승인받고 멈추기를 기다리는 화면.</summary>
    private IBusyViewModel? _closingAfter;

    /// <summary>지금 화면이 <see cref="IBusyViewModel"/>이고 작업 중이면 true.</summary>
    public bool IsBusy => Current is IBusyViewModel { IsBusy: true };

    /// <summary>작업 중에 닫기를 승인받은 화면이 멈췄을 때. 창은 이때 다시 닫기를 시도한다.</summary>
    public event EventHandler? CloseRequested;

    public void Navigate(ObservableObject screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        Current = screen;
    }

    /// <summary>
    /// 창을 닫아도 되면 true. 작업 중이면 사용자에게 묻고, 예면 지금 화면에 멈추기를 요청한 뒤 false를 돌려준다
    /// (멈추면 <see cref="CloseRequested"/>). 아니오면 작업을 그대로 두고 false.
    /// 멈추기를 기다리는 중에 다시 닫으려 하면 바로 닫을지 묻는다(예면 true). 멈추지 않는 화면이 창을 붙잡지 않게 하기 위해서다.
    /// 예외를 밖으로 던지지 않는다.
    /// </summary>
    public bool ConfirmClose()
    {
        if (Current is not IBusyViewModel { IsBusy: true } busy)
        {
            return true;
        }

        if (ReferenceEquals(_closingAfter, busy))
        {
            // 이미 멈추는 중이다. 기다릴지, 기록을 믿고 바로 닫을지 사용자가 정한다.
            return dialogs.Confirm(StoppingCloseTitle, StoppingCloseMessage);
        }

        if (!dialogs.Confirm(BusyCloseTitle, BusyCloseMessage))
        {
            return false;
        }

        // Cancel 안에서 바로 멈출 수도 있으므로 구독을 먼저 한다.
        _closingAfter = busy;
        busy.BusyEnded += OnClosingScreenStopped;
        try
        {
            busy.Cancel();
        }
        catch (Exception)
        {
            // 멈추기 요청이 실패했다. 멈추는 중으로 남겨 두면 창이 닫히지 않으므로 처음 상태로 돌린다.
            busy.BusyEnded -= OnClosingScreenStopped;
            _closingAfter = null;
        }

        return false;
    }

    private void OnClosingScreenStopped(object? sender, EventArgs e)
    {
        if (sender is IBusyViewModel busy)
        {
            busy.BusyEnded -= OnClosingScreenStopped;
        }

        _closingAfter = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCurrentChanged(ObservableObject? oldValue, ObservableObject? newValue)
    {
        if (oldValue is IBusyViewModel and INotifyPropertyChanged oldScreen)
        {
            oldScreen.PropertyChanged -= OnScreenPropertyChanged;
        }

        if (newValue is IBusyViewModel and INotifyPropertyChanged newScreen)
        {
            newScreen.PropertyChanged += OnScreenPropertyChanged;
        }
    }

    private void OnScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(IBusyViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(IsBusy));
        }
    }
}
