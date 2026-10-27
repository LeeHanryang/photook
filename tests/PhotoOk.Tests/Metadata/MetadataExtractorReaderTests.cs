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
        // 읽으면 실패하도록 해 두고, 열지 않으면 예외도 없다는 것을 확인한다.
        fs.AddFile(@"D:\in\a.xmp", TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0)));
        fs.AddFile(@"D:\in\Thumbs.db", TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0)));
        fs.FailReadsOf(@"D:\in\a.xmp");
        fs.FailReadsOf(@"D:\in\Thumbs.db");

        var xmp = reader.Read(fs.GetFile(@"D:\in\a.xmp"));
        var other = reader.Read(fs.GetFile(@"D:\in\Thumbs.db"));

        Assert.Null(xmp.CaptureTime);
        Assert.Equal(MediaKind.Sidecar, xmp.Kind);
        Assert.Null(other.CaptureTime);
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

    [Fact]
    public void Read_ConvertsQuickTimeUtc_ToLocal()
    {
        Assert.Equal(new DateTime(2026, 10, 14, 0, 30, 0),
            MetadataExtractorReader.QuickTimeToLocal(new DateTime(2026, 10, 13, 15, 30, 0, DateTimeKind.Utc), Kst));
        Assert.Null(MetadataExtractorReader.QuickTimeToLocal(new DateTime(1904, 1, 1), Kst));
    }
}
