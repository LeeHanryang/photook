using System.Windows;

namespace PhotoOk.Services;

/// <summary>WPF 메시지 창으로 묻고 알린다. 화면(UI) 스레드에서 부른다.</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        Show(title, message, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message) =>
        Show(title, message, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);

    public void ShowError(string title, string message) =>
        Show(title, message, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);

    private static MessageBoxResult Show(string title, string message, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult defaultResult)
    {
        var owner = DialogOwner.Find();
        return owner is null
            ? MessageBox.Show(message, title, buttons, image, defaultResult)
            : MessageBox.Show(owner, message, title, buttons, image, defaultResult);
    }
}
