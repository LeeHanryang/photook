using System.Text;
using System.Text.Json;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

public static class JournalReader
{
    /// <summary>
    /// 기록 파일 하나를 읽어 실행 상태를 복원한다. 닫는 중괄호 없이 끝나 읽을 수 없는 줄(쓰다 만 줄)은 무시하고,
    /// 중괄호로 끝나는데 읽을 수 없는 줄은 손상이므로 <see cref="InvalidDataException"/>을 던진다.
    /// 파일이 줄바꿈 문자로 끝나지 않으면 마지막 비지 않은 줄은 줄바꿈까지 쓰기 전에 끊긴 것이므로, 내용과 상관없이(중괄호로 끝나거나
    /// 읽을 수 있는 줄이어도) 기록되지 않은 것으로 보고 무시한다. 기록하는 쪽이 그런 줄을 닫으면 바로 뒤에 TornLineClosed 표시를
    /// 남기므로, 표시가 뒤따르는 읽을 수 없는 줄은 중괄호로 끝나도 무시한다. 표시 자체는 상태와 항목 수에 영향이 없다.
    /// </summary>
    public static RunState Load(IFileSystem fileSystem, string path)
    {
        var (lines, endsWithNewline) = ReadLines(fileSystem, path);

        var lastNonEmpty = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Trim().Length > 0)
            {
                lastNonEmpty = i;
                break;
            }
        }

        // 줄바꿈 없이 끝난 꼬리가 마지막 비지 않은 줄일 때만 버린다. 꼬리가 공백뿐이면 그 앞 줄은 줄바꿈까지 쓰인 진짜 항목이다.
        if (!endsWithNewline && lastNonEmpty == lines.Count - 1)
        {
            lastNonEmpty--;
        }

        string? runId = null;
        string? archiveRoot = null;
        var finished = false;
        var undoStarted = false;
        var expectedOperations = 0;
        var entryCount = 0;
        var order = new List<string>();
        var operations = new Dictionary<string, OpState>(StringComparer.Ordinal);
        var cleanupRoots = new List<string>();
        var warnings = new List<string>();

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
                // 쓰다 만 줄은 대개 닫는 중괄호로 끝나지 않는다. 그런 줄은 어디에 있든 무시한다.
                // 중괄호로 끝나도 기록하는 쪽이 닫은 조각(다음 항목이 TornLineClosed 표시)이면 무시하고,
                // 그 밖에 중괄호로 끝나는데 읽을 수 없으면 진짜 손상이므로 예외를 던진다.
                if (!lines[i].TrimEnd().EndsWith('}') || IsClosedTornLine(lines, i))
                {
                    continue;
                }

                throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄을 읽을 수 없습니다: {path}");
            }

            // 잘린 줄을 닫았다는 표시일 뿐 사건이 아니다. 상태도 바꾸지 않고 항목 수에도 넣지 않는다.
            if (entry.Event == JournalEvent.TornLineClosed)
            {
                continue;
            }

            entryCount++;
            switch (entry.Event)
            {
                case JournalEvent.RunStarted:
                    runId = entry.RunId;
                    archiveRoot = entry.Destination;
                    if (entry.Length < 0 || entry.Length > int.MaxValue)
                    {
                        throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 계획 작업 수가 올바르지 않습니다.");
                    }

                    expectedOperations = (int)entry.Length;
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
                    operations[id] = new OpState(op, OpStatus.Pending, null, 0, lineNumber);
                    break;

                case JournalEvent.RunFinished:
                    finished = true;
                    break;

                case JournalEvent.UndoStarted:
                    undoStarted = true;
                    break;

                // 작업 상태와 상관없는 사건이다. 값이 빠져 있어도 손상으로 보지 않는다: 이 줄들 때문에 되돌리기가 막히면 안 된다.
                case JournalEvent.CleanupRoot:
                    if (!string.IsNullOrWhiteSpace(entry.Destination))
                    {
                        cleanupRoots.Add(entry.Destination);
                    }

                    break;

                case JournalEvent.Warning:
                    if (!string.IsNullOrWhiteSpace(entry.Message))
                    {
                        warnings.Add(entry.Message);
                    }

                    break;

                case JournalEvent.FolderRemoved:
                    break;

                default:
                    var opId = RequireOpId(entry, lineNumber);
                    if (!operations.TryGetValue(opId, out var current))
                    {
                        throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 계획에 없는 작업입니다 ({opId}).");
                    }

                    var updated = entry.Event switch
                    {
                        JournalEvent.Started => current with { Status = OpStatus.Started },
                        JournalEvent.Completed => current with { Status = OpStatus.Completed, Hash = entry.Hash, CompletedSeq = lineNumber },
                        JournalEvent.Skipped => current with { Status = OpStatus.Skipped },
                        JournalEvent.Failed => current with { Status = OpStatus.Failed },
                        JournalEvent.Undone => current with { Status = OpStatus.Undone },
                        JournalEvent.UndoSkipped => current with { Status = OpStatus.UndoSkipped },
                        _ => current,
                    };
                    operations[opId] = updated with { LastSeq = lineNumber };
                    break;
            }
        }

        if (runId is null || archiveRoot is null)
        {
            throw new InvalidDataException($"작업 기록에 시작 정보(RunStarted)가 없습니다: {path}");
        }

        return new RunState(runId, archiveRoot, order.Select(id => operations[id]).ToList(), finished,
            expectedOperations, entryCount, undoStarted)
        {
            CleanupRoots = cleanupRoots,
            Warnings = warnings,
        };
    }

    private static string RequireOpId(JournalEntry entry, int lineNumber) =>
        entry.OpId ?? throw new InvalidDataException($"작업 기록 {lineNumber}번째 줄: 작업 ID가 없습니다.");

    /// <summary>
    /// <paramref name="index"/>의 읽을 수 없는 줄 다음의 첫 항목이 TornLineClosed 표시인지. 빈 줄과, 닫는 중괄호로 끝나지 않는
    /// 읽을 수 없는 줄(표시를 쓰다가 다시 끊긴 조각)은 건너뛴다. 그 밖의 줄을 만나면 거기서 판단한다.
    /// 줄바꿈 없이 끝나 기록되지 않은 것으로 보는 꼬리도 살핀다. 표시를 다 쓴 뒤 줄바꿈 직전에 끊겼어도 조각은 닫힌 것이다.
    /// </summary>
    private static bool IsClosedTornLine(List<string> lines, int index)
    {
        for (var j = index + 1; j < lines.Count; j++)
        {
            if (lines[j].Trim().Length == 0)
            {
                continue;
            }

            var next = TryParse(lines[j]);
            if (next is null)
            {
                if (!lines[j].TrimEnd().EndsWith('}'))
                {
                    continue;
                }

                return false;
            }

            return next.Event == JournalEvent.TornLineClosed;
        }

        return false;
    }

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

    /// <summary>줄 목록과, 파일이 줄바꿈 문자(LF)로 끝나는지. 빈 파일은 줄바꿈으로 끝나지 않는다.</summary>
    private static (List<string> Lines, bool EndsWithNewline) ReadLines(IFileSystem fileSystem, string path)
    {
        string text;
        using (var stream = fileSystem.OpenReadShared(path))
        using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
        {
            text = reader.ReadToEnd();
        }

        var lines = new List<string>();
        using (var lineReader = new StringReader(text))
        {
            while (lineReader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }

        return (lines, text.Length > 0 && text[^1] == '\n');
    }
}
