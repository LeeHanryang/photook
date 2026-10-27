using System.Globalization;
using PhotoOk.Core.Execution;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.Core.History;

public enum RunStatus { Completed, CompletedWithFailures, Unfinished, PlanIncomplete, UndoInProgress, Undone }

/// <summary>
/// 지난 실행 하나의 요약. <paramref name="FileOperations"/>는 폴더 만들기를 뺀 작업 수, <paramref name="CompletedFiles"/>와
/// <paramref name="FailedFiles"/>는 그 가운데 완료·실패한 수다. <paramref name="JournalPath"/>는 읽어 낸 항목이 가장 많은 사본이다.
/// </summary>
public sealed record RunSummary(string RunId, string JournalPath, string ArchiveRoot, DateTime StartedLocal, RunStatus Status,
    int FileOperations, int CompletedFiles, int FailedFiles, bool CanResume, bool CanUndo);

public sealed record HistoryError(string JournalPath, string Message);

/// <summary>앱 데이터의 작업 기록 폴더를 읽어 지난 실행 목록을 만든다. 기록은 읽기만 한다.</summary>
public sealed class RunHistory(IFileSystem fileSystem, string appDataRoot)
{
    private const string Unreadable = "기록을 읽을 수 없습니다";
    private const string RunIdTimeFormat = "yyyyMMdd-HHmmss";

    /// <summary>실행 ID 내림차순(최근 실행이 먼저). 읽을 수 없는 기록은 목록에서 빼고 오류로 알린다.</summary>
    public (IReadOnlyList<RunSummary> Runs, IReadOnlyList<HistoryError> Errors) List()
    {
        var runs = new List<RunSummary>();
        var errors = new List<HistoryError>();
        var folder = Path.Combine(appDataRoot, "journal");
        if (!fileSystem.DirectoryExists(folder))
        {
            return (runs, errors);
        }

        List<string> files;
        try
        {
            files = fileSystem
                .EnumerateFiles(folder, recursive: false, (path, _) => errors.Add(new HistoryError(path, Unreadable)))
                .Where(f => string.Equals(Path.GetExtension(f), ".jsonl", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new HistoryError(folder, Unreadable));
            return (runs, errors);
        }

        foreach (var file in files)
        {
            try
            {
                runs.Add(Summarize(file));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
            {
                errors.Add(new HistoryError(file, Unreadable));
            }
        }

        runs.Sort((a, b) => string.CompareOrdinal(b.RunId, a.RunId));
        return (runs, errors);
    }

    private RunSummary Summarize(string file)
    {
        var (path, state, _) = JournalCopies.Choose(fileSystem, appDataRoot, file);
        var started = ParseStart(state.RunId);
        var fileOps = state.Operations.Where(o => o.Operation.Kind != OperationKind.CreateDirectory).ToList();
        var completed = fileOps.Count(o => o.Status == OpStatus.Completed);
        var failed = fileOps.Count(o => o.Status == OpStatus.Failed);
        var status = Judge(state, failed);
        var canResume = status == RunStatus.Unfinished;
        var canUndo = status is RunStatus.Completed or RunStatus.CompletedWithFailures or RunStatus.Unfinished
            or RunStatus.UndoInProgress;
        return new RunSummary(state.RunId, path, state.ArchiveRoot, started, status, fileOps.Count, completed, failed,
            canResume, canUndo);
    }

    private static RunStatus Judge(RunState state, int failedFiles)
    {
        if (state.UndoStarted)
        {
            var pending = state.Operations.Any(o => o.Status is OpStatus.Completed or OpStatus.Started or OpStatus.Failed);
            return pending ? RunStatus.UndoInProgress : RunStatus.Undone;
        }

        if (state.Operations.Count != state.ExpectedOperations)
        {
            return RunStatus.PlanIncomplete;
        }

        if (state.Finished)
        {
            return failedFiles > 0 ? RunStatus.CompletedWithFailures : RunStatus.Completed;
        }

        return RunStatus.Unfinished;
    }

    /// <summary>실행 ID 앞부분(<c>yyyyMMdd-HHmmss</c>)을 현지 시각으로 읽는다. 읽을 수 없으면 <see cref="FormatException"/>.</summary>
    private static DateTime ParseStart(string runId)
    {
        if (runId.Length < RunIdTimeFormat.Length
            || !DateTime.TryParseExact(runId[..RunIdTimeFormat.Length], RunIdTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var started))
        {
            throw new FormatException("실행 ID에서 시작 시각을 읽을 수 없습니다.");
        }

        return started;
    }
}
