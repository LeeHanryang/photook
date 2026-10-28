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
    private const string StoppingTitle = "멈추는 중입니다";
    private const string StoppingMessage = "지금 파일을 마치는 중입니다. 그래도 바로 닫을까요? 기록이 남아 있어 나중에 이어 하거나 되돌릴 수 있습니다.";

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

        public bool ThrowOnCancel { get; set; }

        public void Cancel()
        {
            CancelCount++;
            if (ThrowOnCancel)
            {
                throw new InvalidOperationException("모의 취소 실패");
            }
        }

        /// <summary>작업이 멈춘 상황을 흉내 낸다.</summary>
        public void EndBusy()
        {
            IsBusy = false;
            BusyEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class LeavableScreen : ObservableObject, ILeavableScreen
    {
        public int LeftCount { get; private set; }

        public bool ThrowOnLeft { get; set; }

        public void OnLeft()
        {
            LeftCount++;
            if (ThrowOnLeft)
            {
                throw new InvalidOperationException("모의 정리 실패");
            }
        }
    }

    [Fact]
    public void Navigate_TellsLeavingScreen_Once()
    {
        var shell = new ShellViewModel(_dialogs);
        var first = new LeavableScreen();
        var second = new LeavableScreen();
        shell.Navigate(first);

        shell.Navigate(first); // 같은 화면이면 떠난 것이 아니다
        Assert.Equal(0, first.LeftCount);

        shell.Navigate(second);

        Assert.Equal(1, first.LeftCount);
        Assert.Equal(0, second.LeftCount);
    }

    [Fact]
    public void Navigate_StillChangesScreen_WhenLeavingScreenThrows()
    {
        var shell = new ShellViewModel(_dialogs);
        var first = new LeavableScreen { ThrowOnLeft = true };
        var home = new IdleScreen();
        shell.Navigate(first);

        Assert.True(shell.ReturnHomeAfterError(() => home));

        Assert.Same(home, shell.Current);
        Assert.Equal(1, first.LeftCount);
    }

    [Fact]
    public void ReturnHomeAfterError_FromIdleScreen_GoesHome()
    {
        var shell = new ShellViewModel(_dialogs);
        var idle = new FakeBusyScreen { IsBusy = false };
        shell.Navigate(idle);
        var home = new IdleScreen();

        Assert.True(shell.ReturnHomeAfterError(() => home));

        Assert.Same(home, shell.Current);
        Assert.Equal(1, idle.CancelCount); // 남은 백그라운드 일을 멈춘다
    }

    [Fact]
    public void ReturnHomeAfterError_LeavesBusyScreen()
    {
        var shell = new ShellViewModel(_dialogs);
        var busy = new FakeBusyScreen();
        shell.Navigate(busy);

        Assert.False(shell.ReturnHomeAfterError(() => throw new InvalidOperationException("부르면 안 됨")));

        Assert.Same(busy, shell.Current);
        Assert.Equal(0, busy.CancelCount);
    }

    [Fact]
    public void ReturnHomeAfterError_DoesNothing_OnStartScreenOrWithoutScreen()
    {
        var shell = new ShellViewModel(_dialogs);
        Assert.False(shell.ReturnHomeAfterError(() => throw new InvalidOperationException("부르면 안 됨")));

        var start = new StartViewModel(shell, new FakeDrives(), new FakeRunHistory(),
            new PhotoOk.Services.FileLog(new FakeFileSystem(), @"L:\logs"), _ => new IdleScreen(), _ => new IdleScreen(),
            _ => new IdleScreen());
        shell.Navigate(start);

        Assert.False(shell.ReturnHomeAfterError(() => throw new InvalidOperationException("부르면 안 됨")));
        Assert.Same(start, shell.Current);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Closing_AgainWhileStopping_AsksToCloseNow(bool closeNow)
    {
        var shell = new ShellViewModel(_dialogs);
        var screen = new FakeBusyScreen();
        shell.Navigate(screen);
        var closeRequests = 0;
        shell.CloseRequested += (_, _) => closeRequests++;
        _dialogs.ConfirmAnswers.Enqueue(true);
        Assert.False(shell.ConfirmClose());

        // 멈추기를 기다리는 동안 다시 닫으려 하면 바로 닫을지 묻는다(멈추지 않는 화면이 창을 붙잡지 않게).
        _dialogs.ConfirmAnswers.Enqueue(closeNow);
        Assert.Equal(closeNow, shell.ConfirmClose());

        Assert.Equal([(BusyTitle, BusyMessage), (StoppingTitle, StoppingMessage)], _dialogs.Confirms);
        Assert.Equal(1, screen.CancelCount);
        Assert.Equal(0, closeRequests);

        if (!closeNow)
        {
            // 기다리기로 했으면 멈춘 뒤 그대로 닫힌다.
            screen.EndBusy();
            Assert.Equal(1, closeRequests);
        }
    }

    [Fact]
    public void Closing_WhenCancelThrows_DoesNotThrow_AndResetsState()
    {
        var shell = new ShellViewModel(_dialogs);
        var screen = new FakeBusyScreen { ThrowOnCancel = true };
        shell.Navigate(screen);
        var closeRequests = 0;
        shell.CloseRequested += (_, _) => closeRequests++;
        _dialogs.ConfirmAnswers.Enqueue(true);

        Assert.False(shell.ConfirmClose());
        Assert.Equal(1, screen.CancelCount);

        // 멈추는 중으로 남지 않았으므로 다음 닫기 시도는 처음 질문을 다시 한다.
        _dialogs.ConfirmAnswers.Enqueue(false);
        Assert.False(shell.ConfirmClose());
        Assert.Equal([(BusyTitle, BusyMessage), (BusyTitle, BusyMessage)], _dialogs.Confirms);

        // 구독도 풀렸으므로 나중에 작업이 끝나도 창을 닫지 않는다.
        screen.EndBusy();
        Assert.Equal(0, closeRequests);
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
