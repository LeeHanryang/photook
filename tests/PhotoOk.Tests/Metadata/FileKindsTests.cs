using PhotoOk.Core.Metadata;

namespace PhotoOk.Tests.Metadata;

public class FileKindsTests
{
    [Theory]
    [InlineData("a.JPG", MediaKind.Image)]
    [InlineData("b.rw2", MediaKind.Raw)]
    [InlineData("c.MOV", MediaKind.Video)]
    [InlineData("d.xmp", MediaKind.Sidecar)]
    [InlineData("Thumbs.db", MediaKind.Other)]
    [InlineData(@"D:\사진\x.HeIc", MediaKind.Image)]
    [InlineData("noextension", MediaKind.Other)]
    public void Classify_ByExtension(string name, MediaKind expected)
    {
        Assert.Equal(expected, FileKinds.Default.Classify(name));
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".RAF", true)]
    [InlineData(".mp4", true)]
    [InlineData(".xmp", false)]
    [InlineData(".db", false)]
    public void IsMediaExtension_TrueForImageRawVideo(string ext, bool expected)
    {
        Assert.Equal(expected, FileKinds.Default.IsMediaExtension(ext));
    }

    [Theory]
    [InlineData(".jpg .jpeg .heic .heif .png .tif .tiff .webp", MediaKind.Image)]
    [InlineData(".rw2 .raf .nef .nrw .cr2 .cr3 .arw .dng .orf .pef .srw", MediaKind.Raw)]
    [InlineData(".mp4 .mov .m4v .avi .mts .m2ts", MediaKind.Video)]
    [InlineData(".xmp .pp3 .dop .aae", MediaKind.Sidecar)]
    public void Default_CoversAllSpecifiedExtensions(string extensions, MediaKind expected)
    {
        foreach (var ext in extensions.Split(' '))
        {
            Assert.Equal(expected, FileKinds.Default.Classify("f" + ext));
        }
    }
}
