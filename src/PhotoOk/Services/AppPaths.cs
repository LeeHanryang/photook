using System.IO;

namespace PhotoOk.Services;

/// <summary>앱이 쓰는 폴더와 파일 위치.</summary>
/// <param name="AppDataRoot">기록(journal) 등 이 컴퓨터에만 두는 자료. <c>%LocalAppData%\PhotoOk</c>.</param>
/// <param name="PresetFolder">사용자 프리셋. <c>%AppData%\PhotoOk\presets</c>.</param>
/// <param name="SettingsPath">마지막 선택을 덧붙여 저장하는 설정 파일. <c>%AppData%\PhotoOk\settings.jsonl</c>.</param>
/// <param name="LogFolder">오류 로그. <c>%LocalAppData%\PhotoOk\logs</c>.</param>
public sealed record AppPaths(string AppDataRoot, string PresetFolder, string SettingsPath, string LogFolder)
{
    private const string AppFolderName = "PhotoOk";

    public static AppPaths ForCurrentUser()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        return new AppPaths(
            AppDataRoot: local,
            PresetFolder: Path.Combine(roaming, "presets"),
            SettingsPath: Path.Combine(roaming, "settings.jsonl"),
            LogFolder: Path.Combine(local, "logs"));
    }
}
