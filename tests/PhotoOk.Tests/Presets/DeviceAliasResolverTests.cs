using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;

namespace PhotoOk.Tests.Presets;

public class DeviceAliasResolverTests
{
    private static PhotoInfo File(string path, string? model = null, string? serial = null) =>
        new(path, 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), MediaKind.Image, null, null, model, serial);

    [Fact]
    public void Resolve_PrefersModelAndSerial()
    {
        var resolver = new DeviceAliasResolver(
        [
            new DeviceAlias("Zf-1", "Z f", "111"),
            new DeviceAlias("Zf", "Z f"),
        ]);

        Assert.Equal("Zf-1", resolver.Resolve(File(@"D:\a\DSC0001.NEF", "Z f", "111")));
        Assert.Equal("Zf", resolver.Resolve(File(@"D:\a\DSC0002.NEF", "Z f", "222")));
        Assert.Equal("Zf-1", resolver.Resolve(File(@"D:\a\DSC0003.NEF", " z F ", " 111 ")));
    }

    [Fact]
    public void Resolve_ModelAndSerialAlias_WinsEvenWhenListedAfterModelOnly()
    {
        var resolver = new DeviceAliasResolver(
        [
            new DeviceAlias("Zf", "Z f"),
            new DeviceAlias("Zf-1", "Z f", "111"),
        ]);

        Assert.Equal("Zf-1", resolver.Resolve(File(@"D:\a\DSC0001.NEF", "Z f", "111")));
    }

    [Fact]
    public void Resolve_UsesFileNamePrefix_WhenNoExif()
    {
        var resolver = new DeviceAliasResolver([new DeviceAlias("S9", FileNamePrefix: "P1000")]);

        Assert.Equal("S9", resolver.Resolve(File(@"D:\a\P1000123.JPG")));
        Assert.Equal("S9", resolver.Resolve(File(@"D:\a\p1000999.jpg")));
        Assert.Null(resolver.Resolve(File(@"D:\P1000\IMG_1.JPG")));
    }

    [Fact]
    public void Resolve_FallsBackToModel_ThenNull()
    {
        var resolver = new DeviceAliasResolver([new DeviceAlias("Zf", "Z f")]);

        Assert.Equal("DMC-GX85", resolver.Resolve(File(@"D:\a\IMG_1.JPG", "  DMC-GX85 ")));
        Assert.Null(resolver.Resolve(File(@"D:\a\IMG_2.JPG")));
        Assert.Null(resolver.Resolve(File(@"D:\a\IMG_3.JPG", "   ")));
    }

    [Fact]
    public void Resolve_ModelOnlyAlias_RequiresNoSerialOnAlias()
    {
        var resolver = new DeviceAliasResolver([new DeviceAlias("Zf-1", "Z f", "111")]);

        // 시리얼이 다르면 이 별칭은 맞지 않으므로 모델 이름으로 돌아간다.
        Assert.Equal("Z f", resolver.Resolve(File(@"D:\a\DSC1.NEF", "Z f", "999")));
    }
}
