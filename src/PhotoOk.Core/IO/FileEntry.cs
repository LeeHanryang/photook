namespace PhotoOk.Core.IO;

/// <summary>파일 하나의 경로, 크기, 마지막 쓰기 시각(UTC).</summary>
public sealed record FileEntry(string Path, long Length, DateTime LastWriteTimeUtc);
