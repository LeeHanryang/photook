using System.IO;
using System.Text.RegularExpressions;

namespace PhotoOk.Tests.Installer;

/// <summary>설치 스크립트의 약속: 앱을 강제로 끄지 않고, 프로그램 폴더 밖을 지우지 않으며, 버전은 build.ps1만 넘긴다.</summary>
public class InstallerScriptTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "PhotoOk.iss"));
    private static readonly string Build = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "build.ps1"));

    private static IEnumerable<string> Section(string name)
    {
        var match = Regex.Match(Script, @"^\[" + name + @"\]\s*$(.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(match.Success, $"[{name}] 절이 없습니다");
        return match.Groups[1].Value.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith(';'));
    }

    [Fact]
    public void AppMutex_IsTheOneTheAppHolds()
    {
        Assert.Contains("AppMutex=PhotoOkRunning", Section("Setup"));
    }

    [Fact]
    public void ProcessCheck_StaysForUpgradesFromVersionsWithoutTheMutex()
    {
        Assert.Contains("function IsAppRunning(): Boolean;", Script, StringComparison.Ordinal);
        Assert.Contains("tasklist.exe", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverForceKills()
    {
        Assert.Contains("CloseApplications=no", Section("Setup"));
        Assert.DoesNotContain("taskkill", Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TerminateProcess", Script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckFailed_AsksInsteadOfGoingOnSilently()
    {
        Assert.Contains("확인하지 못했습니다. PhotoOK가 꺼져 있는지 확인한 뒤 계속하세요", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_HasNoDefault_OnlyBuildScriptPassesIt()
    {
        Assert.Matches(@"#ifndef AppVersion\s+#error", Script);
        Assert.DoesNotContain("#define AppVersion", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScript_RefusesVersionsTheDowngradeCheckCannotRead()
    {
        Assert.Contains(@"^\d+\.\d+\.\d+$", Build, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallDelete_StaysInsideAppFolder_OnlyOverAPreviousInstall_AndKeepsTheUninstaller()
    {
        var entries = Section("InstallDelete").ToList();

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            var name = Regex.Match(entry, @"Name:\s*""([^""]+)""").Groups[1].Value;
            Assert.StartsWith(@"{app}\", name, StringComparison.Ordinal);
            Assert.DoesNotContain("..", name, StringComparison.Ordinal);
            Assert.NotEqual(@"{app}\*", name); // 언인스톨러와 그 기록까지 지운다
            Assert.Contains("Check: IsOverPreviousInstall", entry, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InstallDelete_NamesEachLanguageFolder_NoWildcardFolders()
    {
        var folders = Section("InstallDelete")
            .Where(entry => entry.StartsWith("Type: filesandordirs", StringComparison.Ordinal))
            .Select(entry => Regex.Match(entry, @"Name:\s*""\{app\}\\([^""]+)""").Groups[1].Value)
            .ToList();

        Assert.Equal(["cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "tr", "zh-Hans", "zh-Hant"], folders);
    }

    [Fact]
    public void InstallDelete_CoversThePublishedLanguageFolders_WhenPublishOutputExists()
    {
        var publish = Path.Combine(RepoRoot(), "installer", "publish");
        if (!Directory.Exists(publish))
        {
            return; // 게시 결과는 저장소에 없다. build.ps1을 돌린 뒤에만 맞춰 본다.
        }

        foreach (var folder in Directory.GetDirectories(publish).Select(path => Path.GetFileName(path)!))
        {
            Assert.Contains($@"Name: ""{{app}}\{folder}""", Script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Uninstall_DeletesNothingButAnEmptyAppFolder()
    {
        Assert.Equal(["Type: dirifempty; Name: \"{app}\""], Section("UninstallDelete"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoOk.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("레포 루트를 찾지 못했습니다.");
    }
}
