using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoOk.Core.IO;

namespace PhotoOk.Core.Execution;

public sealed class JournalUnavailableException(string message) : Exception(message);

/// <summary>
/// 선기록(write-ahead) 로그. 항목마다 JSON 한 줄을 모든 경로에 덧붙인다.
/// 일부 경로가 실패해도 하나라도 성공하면 계속하고, 모두 실패하면 <see cref="JournalUnavailableException"/>.
/// </summary>
public sealed class JournalWriter(IFileSystem fileSystem, IReadOnlyList<string> paths)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public void Write(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        var succeeded = false;

        foreach (var path in paths)
        {
            try
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    fileSystem.CreateDirectory(parent);
                }

                fileSystem.AppendLine(path, line);
                succeeded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                var warning = $"기록을 남기지 못함: {path}";
                if (!_warnings.Contains(warning))
                {
                    _warnings.Add(warning);
                }
            }
        }

        if (!succeeded)
        {
            throw new JournalUnavailableException("작업 기록을 어느 곳에도 남기지 못했습니다.");
        }
    }
}
