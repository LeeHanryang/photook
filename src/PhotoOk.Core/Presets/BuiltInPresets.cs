namespace PhotoOk.Core.Presets;

public static class BuiltInPresets
{
    public static Preset DateOnly { get; } = Create("날짜만", [Level("{연도}"), Level("{날짜}")]);

    public static Preset DateDeviceFormat { get; } =
        Create("날짜·기기·포맷", [Level("{연도}"), Level("{날짜}"), Level("{기기}"), Level("{포맷}")]);

    public static IReadOnlyList<Preset> All { get; } = [DateOnly, DateDeviceFormat];

    private static LevelTemplate Level(string template) => new(template);

    private static Preset Create(string name, IReadOnlyList<LevelTemplate> defaultLevels) =>
        new(name, TimeOnly.MinValue,
            [
                new LayoutBranch("판단 필요", BranchCondition.NeedsReview,
                    [Level("판단 필요"), Level("{사유}"), Level("{원래폴더}")]),
                new LayoutBranch("기본", BranchCondition.Always, defaultLevels),
            ],
            [],
            DefaultFormatGroups());

    private static Dictionary<string, IReadOnlyList<string>> DefaultFormatGroups() => new()
    {
        ["JPG"] = [".jpg", ".jpeg"],
        ["RAW"] = [".rw2", ".raf", ".nef", ".nrw", ".cr2", ".cr3", ".arw", ".dng", ".orf", ".pef", ".srw"],
        ["HEIF"] = [".heic", ".heif"],
        ["동영상"] = [".mp4", ".mov", ".m4v", ".avi", ".mts", ".m2ts"],
        ["기타 이미지"] = [".png", ".tif", ".tiff", ".webp"],
    };
}
