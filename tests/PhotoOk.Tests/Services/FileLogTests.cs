using System.IO;
using System.Text;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Services;

public class FileLogTests
{
    private const string LogFolder = @"C:\Users\test\AppData\Local\PhotoOk\logs";

    [Fact]
    public void Error_AppendsToDailyFile_AndKeepsEarlierLines()
    {
        var fs = new FakeFileSystem();
        var log = new FileLog(fs, LogFolder);

        log.Error("첫 번째", new InvalidOperationException("원인 1"));
        log.Error("두 번째", new IOException("원인 2"));

        var file = Path.Combine(LogFolder, DateTime.Now.ToString("yyyyMMdd") + ".log");
        Assert.Equal([file], fs.AllFiles());
        var text = Encoding.UTF8.GetString(fs.ReadAllBytes(file));
        Assert.Contains("첫 번째", text);
        Assert.Contains("원인 1", text);
        Assert.Contains("두 번째", text);
        Assert.Contains("System.IO.IOException", text);
        Assert.True(text.IndexOf("첫 번째", StringComparison.Ordinal) < text.IndexOf("두 번째", StringComparison.Ordinal));
    }

    [Fact]
    public void Error_NeverThrows_WhenDiskFails()
    {
        var fs = new FakeFileSystem();
        fs.CrashAfter(0);
        var log = new FileLog(fs, LogFolder);

        log.Error("기록 실패", new InvalidOperationException("원인"));

        Assert.Empty(fs.AllFiles());
    }

    [Fact]
    public void TryError_TellsWhetherTheLogWasWritten()
    {
        var fs = new FakeFileSystem();
        var log = new FileLog(fs, LogFolder);

        Assert.True(log.TryError("남김", new InvalidOperationException("원인")));

        fs.CrashAfter(0);
        Assert.False(log.TryError("못 남김", new InvalidOperationException("원인")));
    }

    [Fact]
    public void AppPaths_ForCurrentUser_UsesAppDataFolders()
    {
        var paths = AppPaths.ForCurrentUser();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(local, "PhotoOk"), paths.AppDataRoot);
        Assert.Equal(Path.Combine(roaming, "PhotoOk", "presets"), paths.PresetFolder);
        Assert.Equal(Path.Combine(roaming, "PhotoOk", "settings.jsonl"), paths.SettingsPath);
        Assert.Equal(Path.Combine(local, "PhotoOk", "logs"), paths.LogFolder);
    }
}
