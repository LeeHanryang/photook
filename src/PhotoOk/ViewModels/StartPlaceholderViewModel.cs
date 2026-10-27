using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoOk.ViewModels;

/// <summary>시작 화면이 들어올 자리. 시작 화면이 생기면 이 자리표시는 없어진다.</summary>
public sealed class StartPlaceholderViewModel : ObservableObject
{
    public string Title => "PhotoOK";
}
