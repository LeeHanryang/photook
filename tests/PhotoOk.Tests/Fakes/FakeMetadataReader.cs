using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;

namespace PhotoOk.Tests.Fakes;

/// <summary>경로별로 미리 정한 촬영 정보를 돌려주는 테스트용 리더. 정하지 않은 경로는 메타데이터 없음.</summary>
public sealed class FakeMetadataReader : IMetadataReader
{
    private readonly Dictionary<string, (DateTime? Capture, string? Make, string? Model, string? Serial, CaptureTimeSource? Source)> _values =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="source">촬영일의 출처. 정하지 않으면 사진은 Photo, 영상은 Video(촬영일이 없으면 None).</param>
    public void Set(string path, DateTime? capture, string? make = null, string? model = null, string? serial = null,
        CaptureTimeSource? source = null)
    {
        _values[path] = (capture, make, model, serial, source);
    }

    /// <summary>이 경로를 읽으면 지정한 예외를 던진다(진짜 리더에서 예외가 새는 경우를 흉내 낸다).</summary>
    public void FailOn(string path, Exception exception)
    {
        _failures[path] = exception;
    }

    public PhotoInfo Read(FileEntry file)
    {
        if (_failures.TryGetValue(file.Path, out var failure))
        {
            throw failure;
        }

        var kind = FileKinds.Default.Classify(file.Path);
        if (kind is MediaKind.Sidecar or MediaKind.Other || !_values.TryGetValue(file.Path, out var v))
        {
            return new PhotoInfo(file.Path, file.Length, file.LastWriteTimeUtc, kind, null, null, null, null);
        }

        var source = v.Capture is null
            ? CaptureTimeSource.None
            : v.Source ?? (kind == MediaKind.Video ? CaptureTimeSource.Video : CaptureTimeSource.Photo);
        return new PhotoInfo(file.Path, file.Length, file.LastWriteTimeUtc, kind, v.Capture, v.Make, v.Model, v.Serial,
            source);
    }
}
