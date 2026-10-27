namespace PhotoOk.Services;

/// <summary>확인·안내·오류 메시지 창.</summary>
public interface IDialogService
{
    /// <summary>예/아니오로 묻는다. 예면 true.</summary>
    bool Confirm(string title, string message);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);
}
