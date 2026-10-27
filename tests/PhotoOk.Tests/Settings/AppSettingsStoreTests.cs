using System.IO;
using System.Text;
using PhotoOk.Core.Planning;
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
}
