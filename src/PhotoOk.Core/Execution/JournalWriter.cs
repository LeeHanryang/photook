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
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _prepared = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Warnings => _warnings;

    public void Write(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        var succeeded = false;

        foreach (var path in paths)
        {
            // 한 번 실패한 경로는 이번 실행 동안 더 쓰지 않는다. 그래야 각 파일이 빠진 줄 없는 앞부분이거나 전체가 된다.
            if (_failed.Contains(path))
            {
                continue;
            }

            try
            {
                if (!_prepared.Contains(path))
                {
                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        fileSystem.CreateDirectory(parent);
                    }

                    CloseTornLine(path);
                    _prepared.Add(path);
                }

                fileSystem.AppendLine(path, line);
                succeeded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                _failed.Add(path);
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

    /// <summary>
    /// 이전 실행이 강제 종료되어 마지막 줄이 줄바꿈 없이 끝났다면, 줄바꿈만 먼저 덧붙여 조각을 닫는다.
    /// 그렇지 않으면 다음 항목이 조각에 붙어 읽을 수 없는 줄이 된다.
    /// </summary>
    private void CloseTornLine(string path)
    {
        if (!fileSystem.FileExists(path))
        {
            return;
        }

        var last = -1;
        using (var stream = fileSystem.OpenRead(path))
        {
            if (stream.CanSeek)
            {
                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    last = stream.ReadByte();
                }
            }
            else
            {
                int b;
                while ((b = stream.ReadByte()) >= 0)
                {
                    last = b;
                }
            }
        }

        if (last >= 0 && last != '\n')
        {
            fileSystem.AppendLine(path, string.Empty);
        }
    }
}
