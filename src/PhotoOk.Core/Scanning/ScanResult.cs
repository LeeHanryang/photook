namespace PhotoOk.Core.Scanning;

/// <summary>스캔 중 읽지 못한 경로와 사유.</summary>
public sealed record ScanError(string Path, string Message);

/// <summary>
/// 스캔 결과. <see cref="Groups"/>는 대표 경로의 서수(대소문자 무시) 순서다.
/// <see cref="SubfoldersNotScanned"/>는 하위 폴더를 포함하지 않고 스캔했는데 소스 폴더에 하위 폴더가 있었을 때(확인하지 못했을 때 포함) true다.
/// 그 폴더의 파일은 세지도, 무시한 파일로 알리지도 않았다.
/// </summary>
public sealed record ScanResult(
    IReadOnlyList<FileGroup> Groups,
    IReadOnlyList<string> Ignored,
    IReadOnlyList<ScanError> Errors,
    bool SubfoldersNotScanned);
