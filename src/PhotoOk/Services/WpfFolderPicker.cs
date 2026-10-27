using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace PhotoOk.Services;

/// <summary>Windows 기본 폴더 선택 창(<see cref="OpenFolderDialog"/>).</summary>
public sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        var owner = DialogOwner.Find();
        var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return picked == true ? dialog.FolderName : null;
    }
}

/// <summary>창을 띄울 때 쓸 주인 창. 메인 창이 아직 보이지 않으면 null.</summary>
internal static class DialogOwner
{
    public static Window? Find()
    {
        var window = Application.Current?.MainWindow;
        return window is { IsLoaded: true } ? window : null;
    }
}
