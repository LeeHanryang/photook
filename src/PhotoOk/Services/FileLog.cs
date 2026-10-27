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

    public void Error(string message, Exception ex)
    {
        try
        {
            var now = DateTime.Now;
            var path = Path.Combine(logFolder, now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
            var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [오류] {message}{Environment.NewLine}{ex}";
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
