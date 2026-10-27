namespace PhotoOk.Services;

/// <summary>열 파일 선택 창.</summary>
public interface IFileOpenPicker
{
    /// <summary>고른 파일의 전체 경로. 사용자가 취소하면 null. <paramref name="filter"/>는 <c>"설명|*.json"</c> 형식.</summary>
    string? PickFile(string title, string filter);
}
