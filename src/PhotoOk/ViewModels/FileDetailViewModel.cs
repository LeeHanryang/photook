namespace PhotoOk.ViewModels;

/// <summary>미리보기 오른쪽의 선택 파일 정보와 "왜 이 경로인가".</summary>
public sealed class FileDetailViewModel(
    string fileName,
    string device,
    string captureTime,
    string destination,
    string branchName,
    string template,
    IReadOnlyList<string> reasons,
    string? skipNote)
{
    public string FileName { get; } = fileName;

    /// <summary>기기 이름. 모르면 "알 수 없음".</summary>
    public string Device { get; } = device;

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>. 촬영 시각이 없으면 그렇다고 적는다.</summary>
    public string CaptureTime { get; } = captureTime;

    /// <summary>놓일 전체 경로. 건너뛰는 파일이면 이미 같은 파일이 있는 경로.</summary>
    public string Destination { get; } = destination;

    /// <summary>프리셋에서 고른 분기 이름(예: "기본", "판단 필요").</summary>
    public string BranchName { get; } = branchName;

    /// <summary>그 분기의 폴더 템플릿을 <c>\</c>로 이은 것.</summary>
    public string Template { get; } = template;

    /// <summary>판단 필요 사유. 없으면 빈 목록.</summary>
    public IReadOnlyList<string> Reasons { get; } = reasons;

    public string ReasonsText => Reasons.Count == 0 ? "없음" : string.Join(", ", Reasons);

    /// <summary>이번 실행에서 옮기지 않는 이유. 옮기면 null.</summary>
    public string? SkipNote { get; } = skipNote;
}
