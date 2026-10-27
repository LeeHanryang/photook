namespace PhotoOk.Services;

/// <summary>폴더 선택 창. 화면 로직이 운영체제 창에 직접 묶이지 않게 한다.</summary>
public interface IFolderPicker
{
    /// <summary>고른 폴더의 전체 경로. 사용자가 취소하면 null.</summary>
    string? PickFolder(string title, string? initialFolder);
}
