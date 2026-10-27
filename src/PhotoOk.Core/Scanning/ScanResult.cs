namespace PhotoOk.Core.Scanning;

/// <summary>스캔 중 읽지 못한 경로와 사유.</summary>
public sealed record ScanError(string Path, string Message);

/// <summary>스캔 결과. <see cref="Groups"/>는 대표 경로의 서수(대소문자 무시) 순서다.</summary>
public sealed record ScanResult(
    IReadOnlyList<FileGroup> Groups,
    IReadOnlyList<string> Ignored,
    IReadOnlyList<ScanError> Errors);
