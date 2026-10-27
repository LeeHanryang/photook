using Microsoft.Win32;

namespace PhotoOk.Services;

/// <summary>Windows 기본 파일 열기 창(<see cref="OpenFileDialog"/>). 읽을 파일을 고르기만 한다.</summary>
public sealed class WpfFileOpenPicker : IFileOpenPicker
{
    public string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = false,
            CheckFileExists = true,
        };

        var owner = DialogOwner.Find();
        var picked = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return picked == true ? dialog.FileName : null;
    }
}
