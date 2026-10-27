using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PhotoOk.Core.Presets;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Presets;

public class PresetLibraryTests
{
    private const string Folder = @"C:\Users\me\AppData\Roaming\PhotoOk\presets";

    private static void AddPreset(FakeFileSystem fs, string name, Preset preset) =>
        fs.AddFile(Path.Combine(Folder, name), Encoding.UTF8.GetBytes(PresetSerializer.Serialize(preset)));

    [Fact]
    public void LoadAll_ReturnsBuiltInsFirst_ThenUserFilesByName()
    {
        var fs = new FakeFileSystem();
        AddPreset(fs, "b.json", BuiltInPresets.Monthly with { Name = "비" });
        AddPreset(fs, "a.json", BuiltInPresets.DateOnly with { Name = "가" });
        fs.AddFile(Path.Combine(Folder, "memo.txt"), [1, 2, 3]);

        var (presets, errors) = new PresetLibrary(fs, Folder).LoadAll();

        Assert.Empty(errors);
        Assert.Equal(["날짜 + 이벤트", "날짜만", "날짜·기기·포맷", "월 단위", "가", "비"], presets.Select(p => p.Name));
        Assert.All(presets.Take(4), p => Assert.Null(p.FilePath));
        Assert.Equal("날짜순으로 정리하고, 이름을 적은 날만 폴더 이름에 붙습니다.", presets[0].Description);
        Assert.Equal("완전 자동입니다. 폴더 이름만으로는 내용을 알 수 없습니다.", presets[1].Description);
        Assert.Equal("카메라와 RAW/JPG별로 나뉩니다. 폴더가 깊어집니다.", presets[2].Description);
        Assert.Equal("폴더 수가 적습니다. 한 폴더에 사진이 많아집니다.", presets[3].Description);
        Assert.Equal(Path.Combine(Folder, "a.json"), presets[4].FilePath);
        Assert.Equal("사용자 프리셋: a.json", presets[4].Description);
        Assert.Equal("사용자 프리셋: b.json", presets[5].Description);
    }

    [Fact]
    public void LoadAll_CollectsErrors_ForBrokenFiles()
    {
        var fs = new FakeFileSystem();
        AddPreset(fs, "good.json", BuiltInPresets.Monthly with { Name = "좋음" });
        fs.AddFile(Path.Combine(Folder, "broken.json"), Encoding.UTF8.GetBytes("{ 깨진"));

        var (presets, errors) = new PresetLibrary(fs, Folder).LoadAll();

        Assert.Equal(5, presets.Count);
        var error = Assert.Single(errors);
        Assert.Equal(Path.Combine(Folder, "broken.json"), error.FilePath);
        Assert.StartsWith("프리셋을 읽을 수 없습니다: ", error.Message);
    }

    [Fact]
    public void LoadAll_WorksWithoutUserFolder()
    {
        var (presets, errors) = new PresetLibrary(new FakeFileSystem(), Folder).LoadAll();

        Assert.Equal(4, presets.Count);
        Assert.Empty(errors);
    }

    [Fact]
    public void LoadAll_ReportsError_ForInaccessibleFolder()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(Folder);
        fs.DenyDirectory(Folder);

        var (presets, errors) = new PresetLibrary(fs, Folder).LoadAll();

        Assert.Equal(4, presets.Count);
        var error = Assert.Single(errors);
        Assert.Equal(Folder, error.FilePath);
        Assert.Equal("프리셋 폴더를 열 수 없습니다", error.Message);
    }

    [Fact]
    public void LoadFile_ToleratesUtf8Bom()
    {
        var fs = new FakeFileSystem();
        var json = PresetSerializer.Serialize(BuiltInPresets.Monthly with { Name = "봄" });
        var path = Path.Combine(Folder, "bom.json");
        fs.AddFile(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)]);

        var entry = new PresetLibrary(fs, Folder).LoadFile(path);

        Assert.Equal("봄", entry.Name);
        Assert.Equal(path, entry.FilePath);
    }

    [Fact]
    public void LoadFile_Throws_PresetException_ForUnknownToken()
    {
        var fs = new FakeFileSystem();
        var preset = BuiltInPresets.Monthly with
        {
            Branches = [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{분류}")])],
        };
        var path = Path.Combine(Folder, "bad.json");
        fs.AddFile(path, Encoding.UTF8.GetBytes(PresetSerializer.Serialize(preset)));

        Assert.Throws<PresetException>(() => new PresetLibrary(fs, Folder).LoadFile(path));
    }

    [Fact]
    public void LoadFile_WrapsIoErrors_AsPresetException()
    {
        var fs = new FakeFileSystem();
        var path = Path.Combine(Folder, "missing.json");

        var ex = Assert.Throws<PresetException>(() => new PresetLibrary(fs, Folder).LoadFile(path));

        Assert.Equal($"프리셋 파일을 읽을 수 없습니다: {path}", ex.Message);
    }

    private static byte[] NamelessPresetJson()
    {
        var node = JsonNode.Parse(PresetSerializer.Serialize(BuiltInPresets.Monthly))!.AsObject();
        node.Remove("name");
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    [Fact]
    public void LoadFile_UsesFileName_WhenPresetHasNoName()
    {
        var fs = new FakeFileSystem();
        var path = Path.Combine(Folder, "내 정리법.json");
        fs.AddFile(path, NamelessPresetJson());

        var entry = new PresetLibrary(fs, Folder).LoadFile(path);

        Assert.Equal("내 정리법", entry.Name);
        Assert.Equal("내 정리법", entry.Preset.Name);
    }

    [Fact]
    public void LoadAll_UsesFileName_WhenPresetHasNoName()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path.Combine(Folder, "이름없음.json"), NamelessPresetJson());

        var (presets, errors) = new PresetLibrary(fs, Folder).LoadAll();

        Assert.Empty(errors);
        Assert.Equal("이름없음", presets[^1].Name);
        Assert.Equal("이름없음", presets[^1].Preset.Name);
    }
}
