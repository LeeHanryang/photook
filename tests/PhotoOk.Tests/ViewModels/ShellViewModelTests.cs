using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class ShellViewModelTests
{
    private const string BusyTitle = "실행 중입니다";
    private const string BusyMessage = "지금 파일까지 마치고 멈춘 뒤 닫을까요?";

    private readonly FakeDialogService _dialogs = new();

    private sealed class IdleScreen : ObservableObject;

    private sealed class FakeBusyScreen : ObservableObject, IBusyViewModel
    {
        private bool _isBusy = true;

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public int CancelCount { get; private set; }

        public event EventHandler? BusyEnded;

        public void Cancel() => CancelCount++;

        /// <summary>작업이 멈춘 상황을 흉내 낸다.</summary>
        public void EndBusy()
        {
            IsBusy = false;
            BusyEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    [Fact]
    public void Navigate_SetsCurrent()
    {
        var shell = new ShellViewModel(_dialogs);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var screen = new IdleScreen();

        shell.Navigate(screen);

        Assert.Same(screen, shell.Current);
        Assert.Contains(nameof(ShellViewModel.Current), changed);

        var next = new IdleScreen();
        shell.Navigate(next);
        Assert.Same(next, shell.Current);
    }

    [Fact]
    public void Closing_WhenIdle_IsAllowed()
    {
        var shell = new ShellViewModel(_dialogs);
        Assert.True(shell.ConfirmClose());

        shell.Navigate(new IdleScreen());
        Assert.False(shell.IsBusy);
        Assert.True(shell.ConfirmClose());

        // 작업 화면이라도 지금 바쁘지 않으면 묻지 않고 닫는다.
        shell.Navigate(new FakeBusyScreen { IsBusy = false });
        Assert.False(shell.IsBusy);
        Assert.True(shell.ConfirmClose());

        Assert.Empty(_dialogs.Confirms);
    }

    [Fact]
    public void Closing_WhileBusy_AsksAndCancels()
    {
        var shell = new ShellViewModel(_dialogs);
        var screen = new FakeBusyScreen();
        shell.Navigate(screen);
        var closeRequests = 0;
        shell.CloseRequested += (_, _) => closeRequests++;
        _dialogs.ConfirmAnswers.Enqueue(true);

        Assert.True(shell.IsBusy);
        Assert.False(shell.ConfirmClose());

        Assert.Equal([(BusyTitle, BusyMessage)], _dialogs.Confirms);
        Assert.Equal(1, screen.CancelCount);
        Assert.Equal(0, closeRequests);

        screen.EndBusy();

        Assert.Equal(1, closeRequests);
        Assert.False(shell.IsBusy);
        Assert.True(shell.ConfirmClose());
        Assert.Single(_dialogs.Confirms);
    }

    [Fact]
    public void Closing_WhileBusy_DeclineKeepsRunning()
    {
        var shell = new ShellViewModel(_dialogs);
        var screen = new FakeBusyScreen();
        shell.Navigate(screen);
        var closeRequests = 0;
        shell.CloseRequested += (_, _) => closeRequests++;
        _dialogs.ConfirmAnswers.Enqueue(false);

        Assert.False(shell.ConfirmClose());

        Assert.Single(_dialogs.Confirms);
        Assert.Equal(0, screen.CancelCount);

        // 작업이 나중에 스스로 끝나도 사용자가 닫기를 거절했으므로 창을 닫지 않는다.
        screen.EndBusy();
        Assert.Equal(0, closeRequests);
    }

    [Fact]
    public void Closing_AgainWhileStopping_DoesNotAskTwice()
    {
        var shell = new ShellViewModel(_dialogs);
        var screen = new FakeBusyScreen();
        shell.Navigate(screen);
        var closeRequests = 0;
        shell.CloseRequested += (_, _) => closeRequests++;
        _dialogs.ConfirmAnswers.Enqueue(true);

        Assert.False(shell.ConfirmClose());
        Assert.False(shell.ConfirmClose());

        Assert.Single(_dialogs.Confirms);
        Assert.Equal(1, screen.CancelCount);

        screen.EndBusy();
        Assert.Equal(1, closeRequests);
    }

    [Fact]
    public void IsBusy_FollowsCurrentScreen()
    {
        var shell = new ShellViewModel(_dialogs);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var screen = new FakeBusyScreen();

        shell.Navigate(screen);
        Assert.True(shell.IsBusy);
        Assert.Contains(nameof(ShellViewModel.IsBusy), changed);

        changed.Clear();
        screen.IsBusy = false;
        Assert.False(shell.IsBusy);
        Assert.Contains(nameof(ShellViewModel.IsBusy), changed);

        // 떠난 화면의 변화는 더 이상 알리지 않는다.
        shell.Navigate(new IdleScreen());
        changed.Clear();
        screen.IsBusy = true;
        Assert.DoesNotContain(nameof(ShellViewModel.IsBusy), changed);
    }
}
