using System.Text;
using System.Text.Json;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

public static class JournalReader
{
    /// <summary>
    /// 기록 파일 하나를 읽어 실행 상태를 복원한다. 마지막 비어 있지 않은 줄이 잘려 읽을 수 없으면 그 줄만 무시하고,
    /// 중간 줄이 손상됐으면 <see cref="InvalidDataException"/>을 던진다.
    /// </summary>
    public static RunState Load(IFileSystem fileSystem, string path)
    {
        var lines = ReadLines(fileSystem, path);

        var lastNonEmpty = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                lastNonEmpty = i;
                break;
            }
        }

        string? runId = null;
        string? archiveRoot = null;
        var finished = false;
        var order = new List<string>();
        var operations = new Dictionary<string, OpState>(StringComparer.Ordinal);

        for (var i = 0; i <= lastNonEmpty; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            var lineNumber = i + 1;
            var entry = TryParse(lines[i]);
            if (entry is null)
            {
                if (i == lastNonEmpty)
                {
                    break;
                }

                throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄을 읽을 수 없습니다: {path}");
            }

            switch (entry.Event)
            {
                case JournalEvent.RunStarted:
                    runId = entry.RunId;
                    archiveRoot = entry.Destination;
                    break;

                case JournalEvent.Planned:
                    var id = RequireOpId(entry, lineNumber);
                    if (operations.ContainsKey(id))
                    {
                        throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 작업 ID가 중복됩니다 ({id}).");
                    }

                    var kind = entry.Kind
                        ?? throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 작업 종류가 없습니다.");
                    var destination = entry.Destination
                        ?? throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 대상 경로가 없습니다.");
                    var op = new PlannedOperation(id, kind, entry.Source, destination, entry.Length, []);
                    order.Add(id);
                    operations[id] = new OpState(op, OpStatus.Pending, null, 0);
                    break;

                case JournalEvent.RunFinished:
                    finished = true;
                    break;

                default:
                    var opId = RequireOpId(entry, lineNumber);
                    if (!operations.TryGetValue(opId, out var current))
                    {
                        throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 계획에 없는 작업입니다 ({opId}).");
                    }

                    operations[opId] = entry.Event switch
                    {
                        JournalEvent.Started => current with { Status = OpStatus.Started },
                        JournalEvent.Completed => current with { Status = OpStatus.Completed, Hash = entry.Hash, CompletedSeq = lineNumber },
                        JournalEvent.Skipped => current with { Status = OpStatus.Skipped },
                        JournalEvent.Failed => current with { Status = OpStatus.Failed },
                        JournalEvent.Undone => current with { Status = OpStatus.Undone },
                        JournalEvent.UndoSkipped => current with { Status = OpStatus.UndoSkipped },
                        _ => current,
                    };
                    break;
            }
        }

        if (runId is null || archiveRoot is null)
        {
            throw new InvalidDataException($"작업 기록에 시작 정보(RunStarted)가 없습니다: {path}");
        }

        return new RunState(runId, archiveRoot, order.Select(id => operations[id]).ToList(), finished);
    }

    private static string RequireOpId(JournalEntry entry, int lineNumber) =>
        entry.OpId ?? throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 작업 ID가 없습니다.");

    private static JournalEntry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalEntry>(line, JournalWriter.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ReadLines(IFileSystem fileSystem, string path)
    {
        var lines = new List<string>();
        using var stream = fileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
