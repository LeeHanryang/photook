using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoOk.ViewModels;

/// <summary>미리보기 오른쪽 썸네일 격자의 한 칸. 그림은 나중에 비동기로 채워진다.</summary>
public sealed partial class ThumbnailItemViewModel(string path) : ObservableObject
{
    [ObservableProperty]
    private ImageSource? image;

    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileName(path);
}
