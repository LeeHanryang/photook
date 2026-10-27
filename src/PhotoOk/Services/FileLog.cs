using System.Globalization;
using System.IO;
using PhotoOk.Core.IO;

namespace PhotoOk.Services;

/// <summary>
/// 오류 로그. 날짜마다 <c>yyyyMMdd.log</c> 파일에 덧붙이기만 한다(앱 파일도 덮어쓰지 않는다).
/// 오류 처리 도중에 불리므로 로그를 남기지 못해도 예외를 던지지 않는다.
/// </summary>
public sealed class FileLog(IFileSystem fileSystem, string logFolder)
{
    private readonly object _gate = new();

    public void Error(string message, Exception ex) => Write("오류", $"{message}{Environment.NewLine}{ex}");

    /// <summary>예외는 없지만 나중에 확인할 수 있게 남겨 둘 일(예: 화면에는 요약만 보이는 파일별 실패 원문).</summary>
    public void Warning(string message) => Write("경고", message);

    private void Write(string level, string text)
    {
        try
        {
            var now = DateTime.Now;
            var path = Path.Combine(logFolder, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {text}";
            lock (_gate)
            {
                fileSystem.CreateDirectory(logFolder);
                fileSystem.AppendLine(path, line);
            }
        }
        catch (Exception)
        {
            // 로그를 쓰지 못해도 원래 오류 처리(안내 창)는 계속해야 한다.
        }
    }
}
