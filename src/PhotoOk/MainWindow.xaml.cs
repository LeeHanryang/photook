using System.ComponentModel;
using System.Windows;
using PhotoOk.ViewModels;

namespace PhotoOk;

/// <summary>앱의 유일한 창. 지금 화면을 보여 주고, 작업 중에 닫으려 하면 셸에 확인을 맡긴다.</summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;
        Closing += OnClosing;
        shell.CloseRequested += OnCloseRequested;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_shell.ConfirmClose())
        {
            e.Cancel = true;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // 작업이 멈춘 뒤 다시 닫는다. Closing 처리 중에 Close를 부를 수 없으므로 한 번 미룬다.
        Dispatcher.BeginInvoke(new Action(Close));
    }
}
