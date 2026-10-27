using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Tests.Planning;

public class ReviewDetectorTests
{
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);

    private static FileGroup Group(string path, MediaKind kind, DateTime? capture, string? model = null) =>
        new(new PhotoInfo(path, 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), kind, capture, null, model, null), []);

    private static IReadOnlyList<ReviewReason> Detect(FileGroup group, Preset preset) =>
        ReviewDetector.Detect(group, preset, new DeviceAliasResolver(preset.DeviceAliases));

    [Fact]
    public void Detect_UnknownDevice_OnlyWhenLayoutUsesDevice()
    {
        var group = Group(@"E:\DCIM\a.JPG", MediaKind.Image, Oct14);

        Assert.Empty(Detect(group, BuiltInPresets.DateOnly));
        Assert.Equal([ReviewReason.UnknownDevice], Detect(group, BuiltInPresets.DateDeviceFormat));
    }

    [Fact]
    public void Detect_NoUnknownDevice_WhenDeviceResolves()
    {
        var group = Group(@"E:\DCIM\a.JPG", MediaKind.Image, Oct14, model: "X100VI");

        Assert.Empty(Detect(group, BuiltInPresets.DateDeviceFormat));
    }

    [Fact]
    public void Detect_UsesOnlyDefaultBranch_ForDeviceTokens()
    {
        // 판단 필요 분기에만 {기기}가 있으면 기기 모름이 아니다.
        var preset = new Preset("t", TimeOnly.MinValue,
            [
                new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new LevelTemplate("{기기}")]),
                new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}"), new LevelTemplate("{모델}")]),
            ],
            [], BuiltInPresets.DateOnly.FormatGroups);
        var withoutModelToken = preset with
        {
            Branches = [preset.Branches[0], new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}")])],
        };
        var group = Group(@"E:\DCIM\a.JPG", MediaKind.Image, Oct14);

        Assert.Equal([ReviewReason.UnknownDevice], Detect(group, preset));
        Assert.Empty(Detect(group, withoutModelToken));
    }

    [Fact]
    public void Detect_NoCaptureDate_AndOrdersReasonsByEnumValue()
    {
        var group = Group(@"E:\DCIM\a.MP4", MediaKind.Video, null);

        Assert.Equal([ReviewReason.NoCaptureDate, ReviewReason.UnknownDevice],
            Detect(group, BuiltInPresets.DateDeviceFormat));
    }

    [Fact]
    public void Detect_OrphanSidecar_WithoutNoCaptureDateOrDevice()
    {
        var group = Group(@"E:\DCIM\IMG.RW2.xmp", MediaKind.Sidecar, null);

        Assert.Equal([ReviewReason.OrphanSidecar], Detect(group, BuiltInPresets.DateDeviceFormat));
    }

    [Fact]
    public void Detect_OtherKind_OnlyNoCaptureDate()
    {
        var group = Group(@"E:\DCIM\notes.txt", MediaKind.Other, null);

        Assert.Equal([ReviewReason.NoCaptureDate], Detect(group, BuiltInPresets.DateDeviceFormat));
    }
}
