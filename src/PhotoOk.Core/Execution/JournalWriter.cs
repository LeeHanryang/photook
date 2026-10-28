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

    /// <summary>지금까지 한 번도 실패하지 않은 경로(빠진 줄 없이 모든 항목이 들어 있는 파일), 처음 받은 순서대로.</summary>
    public IReadOnlyList<string> HealthyPaths => paths.Where(p => !_failed.Contains(p)).ToList();

    /// <summary>항목 하나를 경로마다 한 줄씩 덧붙이고 flush한다(작업마다의 선기록).</summary>
    public void Write(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        AppendToAll(entry, path => fileSystem.AppendLine(path, line));
    }

    /// <summary>
    /// 여러 항목을 경로마다 한 번에 덧붙이고 한 번만 flush한다(실행 시작 때의 계획 기록). 경로별 실패 처리는 <see cref="Write"/>와 같다.
    /// 묶음 중간에 끊기면 그 파일에는 앞의 몇 줄만 남을 수 있다. 계획 기록이 모자라면 기록을 읽는 쪽이 RunStarted의 계획 수로 가려낸다.
    /// </summary>
    public void WriteMany(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        var lines = list.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)).ToList();
        if (lines.Count == 0)
        {
            return;
        }

        AppendToAll(list[0], path => fileSystem.AppendLines(path, lines));
    }

    /// <param name="next">이번에 쓸 (첫) 항목. 잘린 줄을 닫는 표시에 같은 실행ID와 시각을 쓴다.</param>
    private void AppendToAll(JournalEntry next, Action<string> append)
    {
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

                    CloseTornLine(path, next);
                    _prepared.Add(path);
                }

                append(path);
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
    /// 이전 실행이 강제 종료되어 마지막 줄이 줄바꿈 없이 끝났다면, 줄바꿈과 <see cref="JournalEvent.TornLineClosed"/> 표시를
    /// 한 번에 덧붙여 조각을 닫는다. 줄바꿈이 없으면 다음 항목이 조각에 붙어 읽을 수 없는 줄이 되고, 표시가 없으면 닫힌 조각이
    /// (우연히 중괄호로 끝날 때) 손상된 줄과 구별되지 않아 이후 읽기가 막힌다.
    /// </summary>
    private void CloseTornLine(string path, JournalEntry next)
    {
        if (!fileSystem.FileExists(path))
        {
            return;
        }

        var last = -1;
        using (var stream = fileSystem.OpenReadShared(path))
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
            var marker = new JournalEntry(next.RunId, JournalEvent.TornLineClosed, TimestampUtc: next.TimestampUtc);
            fileSystem.AppendLines(path, [string.Empty, JsonSerializer.Serialize(marker, JsonOptions)]);
        }
    }
}
