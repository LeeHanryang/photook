using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Metadata;

public class MetadataExtractorReaderTests
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");

    private static (FakeFileSystem Fs, MetadataExtractorReader Reader) Create()
    {
        var fs = new FakeFileSystem();
        return (fs, new MetadataExtractorReader(fs, FileKinds.Default, Kst));
    }

    [Fact]
    public void Read_ReturnsExifCaptureTimeAndCamera()
    {
        var (fs, reader) = Create();
        fs.AddFile(@"D:\in\a.jpg", TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0), "FUJIFILM", "X100VI"));

        var info = reader.Read(fs.GetFile(@"D:\in\a.jpg"));

        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), info.CaptureTime);
        Assert.Equal("FUJIFILM", info.CameraMake);
        Assert.Equal("X100VI", info.CameraModel);
        Assert.Equal(MediaKind.Image, info.Kind);
        Assert.Equal(@"D:\in\a.jpg", info.Path);
    }

    [Fact]
    public void Read_ReturnsNullCaptureTime_ForUnreadableFile()
    {
        var (fs, reader) = Create();
        fs.AddFile(@"D:\in\broken.raf", new byte[16]);

        var info = reader.Read(fs.GetFile(@"D:\in\broken.raf"));

        Assert.Null(info.CaptureTime);
        Assert.Null(info.CameraMake);
        Assert.Null(info.CameraModel);
        Assert.Null(info.SerialNumber);
        Assert.Equal(MediaKind.Raw, info.Kind);
    }

    [Fact]
    public void Read_ReturnsNullFields_ForJpegWithoutMetadata()
    {
        var (fs, reader) = Create();
        fs.AddFile(@"D:\in\plain.jpg", TestImages.Jpeg());

        var info = reader.Read(fs.GetFile(@"D:\in\plain.jpg"));

        Assert.Null(info.CaptureTime);
        Assert.Null(info.CameraMake);
    }

    [Fact]
    public void Read_DoesNotOpen_SidecarOrOther()
    {
        var (fs, reader) = Create();
        // 촬영 시각·카메라가 든 유효한 JPEG 바이트를 넣어 둔다. 잘못 열어서 읽으면 값이 채워지므로 테스트가 실패한다.
        var dated = TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0), "FUJIFILM", "X100VI");
        fs.AddFile(@"D:\in\a.xmp", dated);
        fs.AddFile(@"D:\in\Thumbs.db", dated);

        var xmp = reader.Read(fs.GetFile(@"D:\in\a.xmp"));
        var other = reader.Read(fs.GetFile(@"D:\in\Thumbs.db"));

        Assert.Null(xmp.CaptureTime);
        Assert.Null(xmp.CameraModel);
        Assert.Equal(MediaKind.Sidecar, xmp.Kind);
        Assert.Null(other.CaptureTime);
        Assert.Null(other.CameraModel);
        Assert.Equal(MediaKind.Other, other.Kind);
    }

    [Fact]
    public void Read_ReturnsNullFields_WhenOpenFailsWithIOException()
    {
        var (fs, reader) = Create();
        fs.AddFile(@"D:\in\a.jpg", TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0)));
        fs.FailReadsOf(@"D:\in\a.jpg");

        var info = reader.Read(fs.GetFile(@"D:\in\a.jpg"));

        Assert.Null(info.CaptureTime);
        Assert.Equal(MediaKind.Image, info.Kind);
    }

    private static (FakeFileSystem Fs, MetadataExtractorReader Reader) CreateToday()
    {
        var fs = new FakeFileSystem();
        return (fs, new MetadataExtractorReader(fs, FileKinds.Default, Kst, () => new DateTime(2026, 10, 28)));
    }

    [Fact]
    public void Read_UsesFileNameDate_WhenNoExif()
    {
        var (fs, reader) = CreateToday();
        fs.AddFile(@"D:\in\photo_2026-10-17_00-32-09.jpg", [1, 2, 3, 4]);

        var info = reader.Read(fs.GetFile(@"D:\in\photo_2026-10-17_00-32-09.jpg"));

        Assert.Equal(new DateTime(2026, 10, 17, 0, 32, 9), info.CaptureTime);
        Assert.Equal(CaptureTimeSource.FileName, info.CaptureSource);
    }

    [Fact]
    public void Read_UsesFileNameDate_WhenFileCannotBeRead()
    {
        var (fs, reader) = CreateToday();
        fs.AddFile(@"D:\in\20231214_161919.jpg", TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0)));
        fs.FailReadsOf(@"D:\in\20231214_161919.jpg");

        var info = reader.Read(fs.GetFile(@"D:\in\20231214_161919.jpg"));

        Assert.Equal(new DateTime(2023, 12, 14, 16, 19, 19), info.CaptureTime);
        Assert.Equal(CaptureTimeSource.FileName, info.CaptureSource);
    }

    [Fact]
    public void Read_PrefersExif_OverFileName()
    {
        var (fs, reader) = CreateToday();
        fs.AddFile(@"D:\in\20200101_000000.jpg", TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0)));

        var info = reader.Read(fs.GetFile(@"D:\in\20200101_000000.jpg"));

        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), info.CaptureTime);
        Assert.Equal(CaptureTimeSource.Photo, info.CaptureSource);
    }

    [Fact]
    public void Read_HasNoSource_WhenNoDateAnywhere()
    {
        var (fs, reader) = CreateToday();
        fs.AddFile(@"D:\in\plain.jpg", TestImages.Jpeg());

        var info = reader.Read(fs.GetFile(@"D:\in\plain.jpg"));

        Assert.Null(info.CaptureTime);
        Assert.Equal(CaptureTimeSource.None, info.CaptureSource);
    }

    [Fact]
    public void Read_DoesNotUseFileNameDate_ForSidecarOrOther()
    {
        var (fs, reader) = CreateToday();
        fs.AddFile(@"D:\in\20231214_161919.xmp", [1]);
        fs.AddFile(@"D:\in\20231214_161919.txt", [1]);

        var xmp = reader.Read(fs.GetFile(@"D:\in\20231214_161919.xmp"));
        var other = reader.Read(fs.GetFile(@"D:\in\20231214_161919.txt"));

        Assert.Null(xmp.CaptureTime);
        Assert.Equal(CaptureTimeSource.None, xmp.CaptureSource);
        Assert.Null(other.CaptureTime);
        Assert.Equal(CaptureTimeSource.None, other.CaptureSource);
    }

    [Fact]
    public void Read_ConvertsQuickTimeUtc_ToLocal()
    {
        Assert.Equal(new DateTime(2026, 10, 14, 0, 30, 0),
            MetadataExtractorReader.QuickTimeToLocal(new DateTime(2026, 10, 13, 15, 30, 0, DateTimeKind.Utc), Kst));
        Assert.Null(MetadataExtractorReader.QuickTimeToLocal(new DateTime(1904, 1, 1), Kst));
    }
}
