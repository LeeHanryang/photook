using System.IO;
using System.Text;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Workflow;
using PhotoOk.Core.Settings;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Settings;

public class AppSettingsStoreTests
{
    private const string SettingsPath = @"C:\Users\test\AppData\Roaming\PhotoOk\settings.jsonl";

    private readonly FakeFileSystem _fs = new();

    private static AppSettings Sample(string archive, TransferMode mode = TransferMode.Move) =>
        new([@"E:\DCIM", @"F:\사진"], false, archive, "날짜 + 이벤트", @"C:\p\내 프리셋.json", mode);

    private string Text() => Encoding.UTF8.GetString(_fs.ReadAllBytes(SettingsPath));

    [Fact]
    public void Settings_RoundTrip_AndLastLineWins()
    {
        var first = Sample(@"D:\A");
        var second = Sample(@"D:\B", TransferMode.Copy);

        AppSettingsStore.Save(_fs, SettingsPath, first);
        var loadedFirst = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal(first.Sources, loadedFirst.Sources);
        Assert.Equal(@"D:\A", loadedFirst.ArchiveRoot);
        Assert.Equal(TransferMode.Move, loadedFirst.Mode);

        AppSettingsStore.Save(_fs, SettingsPath, second);

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal(second.Sources, loaded.Sources);
        Assert.Equal(second.IncludeSubfolders, loaded.IncludeSubfolders);
        Assert.Equal(@"D:\B", loaded.ArchiveRoot);
        Assert.Equal(second.PresetName, loaded.PresetName);
        Assert.Equal(second.PresetFile, loaded.PresetFile);
        Assert.Equal(TransferMode.Copy, loaded.Mode);

        var lines = Text().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"mode\":\"Move\"", lines[0]);
        Assert.Contains("내 프리셋", lines[0]);
    }

    [Fact]
    public void Settings_IgnoresBrokenLastLine_UsesPreviousLine()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A"));
        _fs.AppendLine(SettingsPath, "{\"sources\":[\"E:\\\\x\"],\"includeSub");
        _fs.AppendLine(SettingsPath, "null");
        _fs.AppendLine(SettingsPath, "{\"includeSubfolders\":true}");

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);

        Assert.Equal(@"D:\A", loaded.ArchiveRoot);
        Assert.Equal(TransferMode.Move, loaded.Mode);
    }

    [Fact]
    public void Settings_DefaultsWhenMissing()
    {
        Assert.Same(AppSettings.Default, AppSettingsStore.Load(_fs, SettingsPath));
        Assert.Empty(AppSettings.Default.Sources);
        Assert.True(AppSettings.Default.IncludeSubfolders);
        Assert.Equal("날짜 + 이벤트", AppSettings.Default.PresetName);
        Assert.Equal(TransferMode.Copy, AppSettings.Default.Mode);
    }

    [Fact]
    public void Settings_DefaultsWhenNoLineIsReadable()
    {
        _fs.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        _fs.AppendLine(SettingsPath, "garbage");

        Assert.Same(AppSettings.Default, AppSettingsStore.Load(_fs, SettingsPath));
    }

    [Fact]
    public void Settings_DefaultsWhenFileIsUnreadable()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A"));
        _fs.DenyReadsOf(SettingsPath);

        Assert.Same(AppSettings.Default, AppSettingsStore.Load(_fs, SettingsPath));
    }

    [Fact]
    public void UseFolderNames_DefaultsOn_AndPersists()
    {
        Assert.True(AppSettings.Default.UseFolderNames);
        Assert.False(AppSettings.Default.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Keep, AppSettings.Default.Subfolders);
        Assert.True(AppSettings.Default.KeepReviewInPlace);

        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A") with
        {
            UseFolderNames = false,
            SplitUnderscores = true,
            Subfolders = SubfolderPolicy.Merge,
            KeepReviewInPlace = false,
        });

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.False(loaded.UseFolderNames);
        Assert.True(loaded.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Merge, loaded.Subfolders);
        Assert.False(loaded.KeepReviewInPlace);
    }

    [Fact]
    public void SaveChoices_Reorganize_KeepsImportValues_AndAppendsOneLine()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A", TransferMode.Copy));
        var preset = BuiltInPresets.Monthly with { Name = "내 방식" };

        AppSettingsStore.SaveChoices(_fs, SettingsPath, new ImportSettings([@"D:\B\모음"], true, @"D:\B", preset, TransferMode.Move,
            JobKind.Reorganize, UseFolderNames: false, SplitUnderscores: true, Subfolders: SubfolderPolicy.InPlace,
            KeepReviewInPlace: false, PresetFile: @"C:\p\내 방식.json"));

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal([@"E:\DCIM", @"F:\사진"], loaded.Sources);
        Assert.False(loaded.IncludeSubfolders);
        Assert.Equal(TransferMode.Copy, loaded.Mode);
        Assert.Equal(@"D:\B", loaded.ArchiveRoot);
        Assert.Equal("내 방식", loaded.PresetName);
        Assert.Equal(@"C:\p\내 방식.json", loaded.PresetFile);
        Assert.False(loaded.UseFolderNames);
        Assert.True(loaded.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.InPlace, loaded.Subfolders);
        Assert.False(loaded.KeepReviewInPlace);
        Assert.Equal(2, Text().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void SaveChoices_Import_SavesSourcesAndMode()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A", TransferMode.Copy));

        AppSettingsStore.SaveChoices(_fs, SettingsPath, new ImportSettings([@"G:\새 사진"], true, @"D:\A", BuiltInPresets.DateOnly,
            TransferMode.Move));

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal([@"G:\새 사진"], loaded.Sources);
        Assert.True(loaded.IncludeSubfolders);
        Assert.Equal(TransferMode.Move, loaded.Mode);
        Assert.Equal("날짜만", loaded.PresetName);
        Assert.Null(loaded.PresetFile);
    }

    [Fact]
    public void SaveChoices_NoFile_StartsFromDefaults()
    {
        AppSettingsStore.SaveChoices(_fs, SettingsPath, new ImportSettings([@"D:\A\모음"], true, @"D:\A", BuiltInPresets.DateEvent,
            TransferMode.Move, JobKind.Absorb, UseFolderNames: true));

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Empty(loaded.Sources);
        Assert.Equal(TransferMode.Copy, loaded.Mode);
        Assert.Equal(@"D:\A", loaded.ArchiveRoot);
    }

    [Fact]
    public void SaveChoices_UnreadableFile_Throws_AndWritesNothing()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A", TransferMode.Copy));
        var before = _fs.ReadAllBytes(SettingsPath);
        _fs.DenyReadsOf(SettingsPath);

        // 기본값으로 덮으면 가져오기의 폴더·복사/이동 선택을 잃는다. 쓰지 않고 부르는 쪽(로그)에 알린다.
        Assert.Throws<UnauthorizedAccessException>(() => AppSettingsStore.SaveChoices(_fs, SettingsPath,
            new ImportSettings([@"D:\A\모음"], true, @"D:\A", BuiltInPresets.DateOnly, TransferMode.Move, JobKind.Reorganize)));

        Assert.Equal(before, _fs.ReadAllBytes(SettingsPath));
    }

    private void AddBrokenLines()
    {
        _fs.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        _fs.AppendLine(SettingsPath, "다른 버전이 쓴 줄");
        _fs.AppendLine(SettingsPath, "{\"includeSubfolders\":true}");
    }

    [Theory]
    [InlineData(JobKind.Absorb)]
    [InlineData(JobKind.Reorganize)]
    public void SaveChoices_NoParseableLine_DoesNotMergeDefaults(JobKind kind)
    {
        AddBrokenLines();
        var before = _fs.ReadAllBytes(SettingsPath);

        // 읽지 못한 줄에 있을지 모를 가져오기의 선택을 기본값으로 가리지 않는다. 쓰지 않고 부르는 쪽(로그)에 알린다.
        Assert.Throws<IOException>(() => AppSettingsStore.SaveChoices(_fs, SettingsPath,
            new ImportSettings([@"D:\A\모음"], true, @"D:\A", BuiltInPresets.DateOnly, TransferMode.Move, kind)));

        Assert.Equal(before, _fs.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void SaveChoices_NoParseableLine_Import_WritesItsOwnValues()
    {
        AddBrokenLines();

        // 가져오기는 모든 값을 자기 값으로 쓰므로 기본값이 섞이지 않는다.
        AppSettingsStore.SaveChoices(_fs, SettingsPath, new ImportSettings([@"G:\새 사진"], false, @"D:\A", BuiltInPresets.DateOnly,
            TransferMode.Move));

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal([@"G:\새 사진"], loaded.Sources);
        Assert.False(loaded.IncludeSubfolders);
        Assert.Equal(TransferMode.Move, loaded.Mode);
    }

    [Fact]
    public void SaveChoices_EmptyFile_StartsFromDefaults()
    {
        _fs.AddFile(SettingsPath, []);

        AppSettingsStore.SaveChoices(_fs, SettingsPath, new ImportSettings([@"D:\A\모음"], true, @"D:\A", BuiltInPresets.DateEvent,
            TransferMode.Move, JobKind.Reorganize));

        Assert.Equal(@"D:\A", AppSettingsStore.Load(_fs, SettingsPath).ArchiveRoot);
    }

    [Fact]
    public void SaveChoices_BrokenLastLine_UsesEarlierLine()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Sample(@"D:\A", TransferMode.Copy));
        _fs.AppendLine(SettingsPath, "쓰다 만 줄");

        AppSettingsStore.SaveChoices(_fs, SettingsPath, new ImportSettings([@"D:\B\모음"], true, @"D:\B", BuiltInPresets.DateOnly,
            TransferMode.Move, JobKind.Reorganize));

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal([@"E:\DCIM", @"F:\사진"], loaded.Sources);
        Assert.Equal(TransferMode.Copy, loaded.Mode);
        Assert.Equal(@"D:\B", loaded.ArchiveRoot);
    }

    [Fact]
    public void OldSettingsLine_LoadsWithDefaults()
    {
        _fs.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        _fs.AppendLine(SettingsPath,
            "{\"sources\":[\"E:\\\\DCIM\"],\"includeSubfolders\":false,\"archiveRoot\":\"D:\\\\A\",\"presetName\":\"월 단위\",\"presetFile\":null,\"mode\":\"Move\"}");

        var loaded = AppSettingsStore.Load(_fs, SettingsPath);

        Assert.Equal([@"E:\DCIM"], loaded.Sources);
        Assert.Equal(TransferMode.Move, loaded.Mode);
        Assert.True(loaded.UseFolderNames);
        Assert.False(loaded.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Keep, loaded.Subfolders);
        Assert.True(loaded.KeepReviewInPlace);
    }
}
