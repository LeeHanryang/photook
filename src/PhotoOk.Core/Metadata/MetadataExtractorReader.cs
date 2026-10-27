using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Metadata;

/// <summary>
/// MetadataExtractor로 EXIF·QuickTime 메타데이터를 읽는다.
/// 촬영일 우선순위: EXIF DateTimeOriginal, IFD0 DateTime, QuickTime 무비 헤더 Created(UTC, 현지 시각으로 변환).
/// </summary>
public sealed class MetadataExtractorReader(IFileSystem fileSystem, FileKinds kinds, TimeZoneInfo localZone) : IMetadataReader
{
    // 이 시각보다 이른 QuickTime 시각은 "설정되지 않음"(1904 기준 0)으로 보고 없음 처리한다.
    private static readonly DateTime QuickTimeEpochCutoff = new(1971, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public PhotoInfo Read(FileEntry file)
    {
        var kind = kinds.Classify(file.Path);
        if (kind is MediaKind.Sidecar or MediaKind.Other)
        {
            return Empty(file, kind);
        }

        try
        {
            using var stream = fileSystem.OpenRead(file.Path);
            var directories = ImageMetadataReader.ReadMetadata(stream);
            return Build(file, kind, directories);
        }
        catch (ImageProcessingException)
        {
            return Empty(file, kind);
        }
        catch (IOException)
        {
            return Empty(file, kind);
        }
    }

    private PhotoInfo Build(FileEntry file, MediaKind kind, IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var movie = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();

        DateTime? captureTime = null;
        if (subIfd is not null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
        {
            captureTime = Unspecified(original);
        }
        else if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified))
        {
            captureTime = Unspecified(modified);
        }
        else if (movie is not null && movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created))
        {
            captureTime = QuickTimeToLocal(created, localZone);
        }

        return new PhotoInfo(file.Path, file.Length, file.LastWriteTimeUtc, kind, captureTime,
            Trimmed(ifd0?.GetString(ExifDirectoryBase.TagMake)),
            Trimmed(ifd0?.GetString(ExifDirectoryBase.TagModel)),
            Trimmed(subIfd?.GetString(ExifDirectoryBase.TagBodySerialNumber)));
    }

    /// <summary>QuickTime의 UTC 시각을 현지 벽시계 시각으로 바꾼다. 1971년보다 이르면(미설정 값) null.</summary>
    internal static DateTime? QuickTimeToLocal(DateTime utc, TimeZoneInfo zone)
    {
        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (asUtc < QuickTimeEpochCutoff)
        {
            return null;
        }

        return Unspecified(TimeZoneInfo.ConvertTimeFromUtc(asUtc, zone));
    }

    private static DateTime Unspecified(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static PhotoInfo Empty(FileEntry file, MediaKind kind) =>
        new(file.Path, file.Length, file.LastWriteTimeUtc, kind, null, null, null, null);
}
