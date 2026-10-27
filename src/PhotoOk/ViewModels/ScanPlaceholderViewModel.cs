using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoOk.ViewModels;

/// <summary>스캔 화면이 들어올 자리. 스캔 화면이 생기면 이 자리표시는 없어진다.</summary>
public sealed class ScanPlaceholderViewModel : ObservableObject
{
    public string Title => "스캔 화면 준비 중";
}
