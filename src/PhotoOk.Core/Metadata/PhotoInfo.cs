namespace PhotoOk.Core.Metadata;

/// <summary>촬영일을 어디서 얻었는가.</summary>
public enum CaptureTimeSource
{
    /// <summary>촬영일이 없다.</summary>
    None,

    /// <summary>사진의 EXIF 정보.</summary>
    Photo,

    /// <summary>동영상의 QuickTime 정보.</summary>
    Video,

    /// <summary>파일 이름에 든 날짜.</summary>
    FileName,
}

/// <summary>파일 하나에 대해 읽어 낸 정보. 메타데이터를 읽지 못한 항목은 null이다.</summary>
/// <param name="CaptureTime">현지 벽시계 시각(<see cref="DateTimeKind.Unspecified"/>).</param>
/// <param name="CaptureSource"><paramref name="CaptureTime"/>을 얻은 곳. 촬영일이 없으면 <see cref="CaptureTimeSource.None"/>.</param>
public sealed record PhotoInfo(string Path, long Length, DateTime LastWriteTimeUtc, MediaKind Kind,
    DateTime? CaptureTime,
    string? CameraMake, string? CameraModel, string? SerialNumber,
    CaptureTimeSource CaptureSource = CaptureTimeSource.None);
