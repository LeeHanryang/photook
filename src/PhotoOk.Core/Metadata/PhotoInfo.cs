namespace PhotoOk.Core.Metadata;

/// <summary>파일 하나에 대해 읽어 낸 정보. 메타데이터를 읽지 못한 항목은 null이다.</summary>
/// <param name="CaptureTime">현지 벽시계 시각(<see cref="DateTimeKind.Unspecified"/>).</param>
public sealed record PhotoInfo(string Path, long Length, DateTime LastWriteTimeUtc, MediaKind Kind,
    DateTime? CaptureTime,
    string? CameraMake, string? CameraModel, string? SerialNumber);
