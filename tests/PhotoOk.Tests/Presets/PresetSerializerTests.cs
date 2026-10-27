using PhotoOk.Core.Presets;

namespace PhotoOk.Tests.Presets;

public class PresetSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesPreset()
    {
        var original = BuiltInPresets.DateDeviceFormat;

        var json = PresetSerializer.Serialize(original);
        var restored = PresetSerializer.Deserialize(json);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.DayStart, restored.DayStart);
        Assert.Equal(original.Branches.Count, restored.Branches.Count);
        for (var i = 0; i < original.Branches.Count; i++)
        {
            Assert.Equal(original.Branches[i].Name, restored.Branches[i].Name);
            Assert.Equal(original.Branches[i].When, restored.Branches[i].When);
            Assert.Equal(original.Branches[i].Levels, restored.Branches[i].Levels);
        }
        Assert.Equal(original.DeviceAliases, restored.DeviceAliases);
        Assert.Equal(original.FormatGroups.Keys.OrderBy(k => k), restored.FormatGroups.Keys.OrderBy(k => k));
        foreach (var (key, extensions) in original.FormatGroups)
        {
            Assert.Equal(extensions, restored.FormatGroups[key]);
        }
        Assert.Equal(json, PresetSerializer.Serialize(restored));
    }

    [Fact]
    public void RoundTrip_PreservesAliasesAndReplacementLevels()
    {
        var preset = new Preset("내 규칙", new TimeOnly(4, 30),
            [
                new LayoutBranch("기본", BranchCondition.Always,
                [
                    new LevelTemplate("{연도}"),
                    new LevelTemplate("{기기}", EmptyValue.Replace, "기기 미상"),
                ]),
            ],
            [new DeviceAlias("Zf-1", "Z f", "111"), new DeviceAlias("S9", FileNamePrefix: "P1000")],
            new Dictionary<string, IReadOnlyList<string>> { ["JPG"] = [".jpg", ".jpeg"] });

        var json = PresetSerializer.Serialize(preset);
        var restored = PresetSerializer.Deserialize(json);

        Assert.Equal(new TimeOnly(4, 30), restored.DayStart);
        Assert.Equal(EmptyValue.Replace, restored.Branches[0].Levels[1].WhenEmpty);
        Assert.Equal("기기 미상", restored.Branches[0].Levels[1].Replacement);
        Assert.Equal(EmptyValue.Omit, restored.Branches[0].Levels[0].WhenEmpty);
        Assert.Equal(preset.DeviceAliases, restored.DeviceAliases);
        Assert.Contains("\"always\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"dayStart\"", json);
    }

    [Fact]
    public void Deserialize_Throws_WhenLastBranchIsConditional()
    {
        var preset = new Preset("잘못됨", TimeOnly.MinValue,
            [new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new LevelTemplate("판단 필요")])],
            [], new Dictionary<string, IReadOnlyList<string>>());
        var json = PresetSerializer.Serialize(preset);

        var ex = Assert.Throws<PresetException>(() => PresetSerializer.Deserialize(json));
        Assert.Equal("마지막 분기는 조건 없는 기본 분기여야 합니다", ex.Message);
    }

    [Fact]
    public void Deserialize_Throws_WhenNoBranches()
    {
        var preset = new Preset("비어 있음", TimeOnly.MinValue, [], [], new Dictionary<string, IReadOnlyList<string>>());
        var json = PresetSerializer.Serialize(preset);

        var ex = Assert.Throws<PresetException>(() => PresetSerializer.Deserialize(json));
        Assert.Equal("마지막 분기는 조건 없는 기본 분기여야 합니다", ex.Message);
    }

    [Fact]
    public void Deserialize_Throws_OnUnknownTokenInPreset()
    {
        var preset = new Preset("잘못된 토큰", TimeOnly.MinValue,
            [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}"), new LevelTemplate("{분류}")])],
            [], new Dictionary<string, IReadOnlyList<string>>());
        var json = PresetSerializer.Serialize(preset);

        var ex = Assert.Throws<PresetException>(() => PresetSerializer.Deserialize(json));
        Assert.Contains("분류", ex.Message);
    }

    [Fact]
    public void Deserialize_Throws_PresetException_OnBrokenJson()
    {
        Assert.Throws<PresetException>(() => PresetSerializer.Deserialize("{ not json"));
    }

    [Fact]
    public void BuiltIns_HaveExpectedShape()
    {
        var dateOnly = BuiltInPresets.DateOnly;
        Assert.Equal("날짜만", dateOnly.Name);
        Assert.Equal(TimeOnly.MinValue, dateOnly.DayStart);
        Assert.Equal(["판단 필요", "기본"], dateOnly.Branches.Select(b => b.Name));
        Assert.Equal(["판단 필요", "{사유}", "{원래폴더}"], dateOnly.Branches[0].Levels.Select(l => l.Template));
        Assert.Equal(["{연도}", "{날짜}"], dateOnly.Branches[1].Levels.Select(l => l.Template));

        var full = BuiltInPresets.DateDeviceFormat;
        Assert.Equal("날짜·기기·포맷", full.Name);
        Assert.Equal(["{연도}", "{날짜}", "{기기}", "{포맷}"], full.Branches[1].Levels.Select(l => l.Template));
        Assert.Equal(dateOnly.Branches[0].Levels, full.Branches[0].Levels);

        Assert.Equal([dateOnly, full], BuiltInPresets.All);
        Assert.Equal([".jpg", ".jpeg"], full.FormatGroups["JPG"]);
        Assert.Equal([".heic", ".heif"], full.FormatGroups["HEIF"]);
        Assert.Equal([".png", ".tif", ".tiff", ".webp"], full.FormatGroups["기타 이미지"]);
        Assert.Contains(".rw2", full.FormatGroups["RAW"]);
        Assert.Contains(".mp4", full.FormatGroups["동영상"]);
    }
}
