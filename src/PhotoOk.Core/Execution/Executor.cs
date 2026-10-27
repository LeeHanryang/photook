using System.IO.Hashing;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

/// <summary>
/// 계획을 디스크에 적용하는 유일한 곳. 원본은 읽기 전용으로만 열고, 덮어쓰지 않으며, 지울 것은 격리 폴더로 옮긴다.
/// 모든 작업은 먼저 기록(Planned)한 뒤 Started → Completed/Skipped/Failed 순서로 기록한다.
/// 끝까지 시도한 실행만 RunFinished로 끝맺는다. 취소·중단된 실행은 이어 하기 대상으로 남는다.
/// </summary>
public sealed class Executor(ExecutionContext context)
{
    internal const string TempSuffix = ".photook-tmp";
    internal const string VerificationFailedMessage = "복사 검증 실패";
    internal const string DestinationTakenMessage = "대상에 같은 이름의 파일이 생겼습니다";
    internal const string SourceVolumeMissingMessage = "원본 드라이브가 연결되어 있지 않습니다";
    internal const string DirectoryExistsMessage = "이미 있는 폴더라 만들지 않았습니다";
    internal const string SizeChangedBeforeMoveMessage = "파일 크기가 계획 때와 달라 옮기지 않았습니다";
    internal const string SizeChangedAfterMoveMessage = "옮긴 파일의 크기가 계획과 다릅니다 (파일은 새 위치에 있습니다)";
    internal const string MissingSourceMessage = "원본 경로가 없는 작업입니다";

    private const int BufferSize = 1024 * 1024;

    public ExecutionResult Execute(Plan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var fs = context.FileSystem;

        var issues = Preflight.Check(plan, fs);
        if (issues.Any(i => i.Severity == Severity.Error))
        {
            return new ExecutionResult(string.Empty, RunOutcome.BlockedByPreflight, 0, [], issues, [], null);
        }

        var runId = JournalPaths.NewRunId(context.UtcNow());
        var journal = new JournalWriter(fs,
            [JournalPaths.InArchive(plan.ArchiveRoot, runId), JournalPaths.InAppData(context.AppDataRoot, runId)]);
        var run = new Run(runId, journal, new SafeDelete(fs, plan.ArchiveRoot, runId));

        // 선기록: 첫 작업을 시작하기 전에 모든 작업을 기록해 둔다.
        journal.Write(new JournalEntry(runId, JournalEvent.RunStarted, Destination: plan.ArchiveRoot, TimestampUtc: context.UtcNow()));
        foreach (var op in plan.Operations)
        {
            journal.Write(new JournalEntry(runId, JournalEvent.Planned, op.Id, op.Kind, op.Source, op.Destination, op.Length,
                TimestampUtc: context.UtcNow()));
        }

        var outcome = RunOperations(run, plan.Operations, ct);
        return run.ToResult(outcome, issues);
    }

    public ExecutionResult Resume(string journalPath, CancellationToken ct = default) =>
        throw new NotImplementedException();

    // ------------------------------------------------------------------
    // 실행 흐름 (이어 하기에서도 그대로 쓴다)
    // ------------------------------------------------------------------

    /// <summary>
    /// 작업을 순서대로 실행한다. 작업 사이마다 취소를 확인하고(지금 작업은 마친다), 원본 드라이브가 빠지면 즉시 멈춘다.
    /// 모든 작업을 시도했을 때만 RunFinished를 쓴다.
    /// </summary>
    private RunOutcome RunOperations(Run run, IEnumerable<PlannedOperation> operations, CancellationToken ct)
    {
        foreach (var op in operations)
        {
            if (ct.IsCancellationRequested)
            {
                return RunOutcome.Cancelled;
            }

            if (!ExecuteOperation(run, op))
            {
                return RunOutcome.Interrupted;
            }
        }

        run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.RunFinished, TimestampUtc: context.UtcNow()));
        return run.Failures.Count == 0 ? RunOutcome.Completed : RunOutcome.CompletedWithFailures;
    }

    /// <summary>
    /// 작업 하나를 Started → 실행 → Completed/Skipped/Failed로 기록하며 실행한다.
    /// 실행을 계속해도 되면 true, 원본 드라이브가 빠져 멈춰야 하면 false.
    /// 모의 크래시나 <see cref="JournalUnavailableException"/> 같은 다른 예외는 잡지 않는다.
    /// </summary>
    private bool ExecuteOperation(Run run, PlannedOperation op)
    {
        Record(run, op, JournalEvent.Started);

        StepResult result;
        try
        {
            result = op.Kind switch
            {
                OperationKind.CreateDirectory => CreateDirectory(op),
                OperationKind.Copy => Copy(run, op),
                OperationKind.Move => Move(op),
                _ => throw new InvalidOperationException($"알 수 없는 작업 종류입니다: {op.Kind}"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (op.Source is not null && !IsVolumePresent(op.Source))
            {
                RecordFailure(run, op, SourceVolumeMissingMessage);
                return false;
            }

            result = StepResult.Failed(ex.Message);
        }

        switch (result.Event)
        {
            case JournalEvent.Failed:
                RecordFailure(run, op, result.Message!);
                break;

            default:
                Record(run, op, result.Event, result.Hash, result.Message);
                run.Succeeded++;
                break;
        }

        return true;
    }

    private StepResult CreateDirectory(PlannedOperation op)
    {
        var fs = context.FileSystem;
        if (fs.DirectoryExists(op.Destination))
        {
            // 원래 있던 폴더는 되돌리기 대상이 아니다.
            return StepResult.Skipped(DirectoryExistsMessage);
        }

        fs.CreateDirectory(op.Destination);
        return StepResult.Completed();
    }

    /// <summary>
    /// 임시 이름에 쓰면서 원본 해시를 구하고 → 임시 파일을 다시 읽어 비교하고 → 일치할 때만 진짜 이름으로 옮긴다.
    /// 실패한 임시 파일은 격리 폴더로 치운다.
    /// </summary>
    private StepResult Copy(Run run, PlannedOperation op)
    {
        var fs = context.FileSystem;
        if (op.Source is null)
        {
            return StepResult.Failed(MissingSourceMessage);
        }

        if (fs.FileExists(op.Destination))
        {
            return StepResult.Failed(DestinationTakenMessage);
        }

        var temp = op.Destination + TempSuffix;
        if (fs.FileExists(temp))
        {
            run.SafeDelete.Quarantine(temp);
        }

        var createdTemp = false;
        try
        {
            var hash = new XxHash64();
            var buffer = new byte[BufferSize];
            using (var input = fs.OpenRead(op.Source))
            using (var output = fs.CreateNew(temp))
            {
                createdTemp = true;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var chunk = new ReadOnlySpan<byte>(buffer, 0, read);
                    hash.Append(chunk);
                    output.Write(chunk);
                }

                if (output is FileStream file)
                {
                    file.Flush(flushToDisk: true);
                }
                else
                {
                    output.Flush();
                }
            }

            var sourceHash = XxHasher.Format(hash.GetCurrentHashAsUInt64());
            var tempHash = context.Hasher.Hash(temp);
            if (!string.Equals(sourceHash, tempHash, StringComparison.Ordinal))
            {
                run.SafeDelete.Quarantine(temp);
                return StepResult.Failed(VerificationFailedMessage);
            }

            try
            {
                fs.Move(temp, op.Destination);
            }
            catch (IOException) when (fs.FileExists(op.Destination))
            {
                run.SafeDelete.Quarantine(temp);
                return StepResult.Failed(DestinationTakenMessage);
            }

            return StepResult.Completed(sourceHash);
        }
        catch (Exception ex) when (createdTemp && (ex is IOException or UnauthorizedAccessException))
        {
            TryQuarantine(run, temp);
            throw;
        }
    }

    /// <summary>같은 드라이브 안의 이름 바꾸기. 크기가 계획과 다르면 옮기지 않고, 옮긴 뒤 다르면 그대로 두고 실패로 기록한다.</summary>
    private StepResult Move(PlannedOperation op)
    {
        var fs = context.FileSystem;
        if (op.Source is null)
        {
            return StepResult.Failed(MissingSourceMessage);
        }

        if (fs.GetFile(op.Source).Length != op.Length)
        {
            return StepResult.Failed(SizeChangedBeforeMoveMessage);
        }

        if (fs.FileExists(op.Destination))
        {
            return StepResult.Failed(DestinationTakenMessage);
        }

        fs.Move(op.Source, op.Destination);

        if (fs.GetFile(op.Destination).Length != op.Length)
        {
            return StepResult.Failed(SizeChangedAfterMoveMessage);
        }

        return StepResult.Completed();
    }

    // ------------------------------------------------------------------
    // 도우미
    // ------------------------------------------------------------------

    private bool IsVolumePresent(string path)
    {
        var fs = context.FileSystem;
        return fs.DirectoryExists(fs.GetVolumeRoot(path));
    }

    /// <summary>실패한 복사가 남긴 임시 파일을 격리한다. 그마저 안 되면 경고로 남기고 이어 하기·되돌리기에 맡긴다.</summary>
    private void TryQuarantine(Run run, string temp)
    {
        try
        {
            if (context.FileSystem.FileExists(temp))
            {
                run.SafeDelete.Quarantine(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.Warnings.Add($"임시 파일을 치우지 못함: {temp}");
        }
    }

    private void Record(Run run, PlannedOperation op, JournalEvent ev, string? hash = null, string? message = null) =>
        run.Journal.Write(new JournalEntry(run.RunId, ev, op.Id, op.Kind, Hash: hash, Message: message, TimestampUtc: context.UtcNow()));

    private void RecordFailure(Run run, PlannedOperation op, string message)
    {
        Record(run, op, JournalEvent.Failed, message: message);
        run.Failures.Add(new OperationFailure(op.Id, op.Source, op.Destination, message));
    }

    private readonly record struct StepResult(JournalEvent Event, string? Hash, string? Message)
    {
        public static StepResult Completed(string? hash = null) => new(JournalEvent.Completed, hash, null);

        public static StepResult Skipped(string message) => new(JournalEvent.Skipped, null, message);

        public static StepResult Failed(string message) => new(JournalEvent.Failed, null, message);
    }

    /// <summary>실행 하나의 상태. 격리(SafeDelete)는 실행마다 하나다.</summary>
    private sealed class Run(string runId, JournalWriter journal, SafeDelete safeDelete)
    {
        public string RunId { get; } = runId;

        public JournalWriter Journal { get; } = journal;

        public SafeDelete SafeDelete { get; } = safeDelete;

        public List<OperationFailure> Failures { get; } = [];

        public List<string> Warnings { get; } = [];

        public int Succeeded { get; set; }

        public ExecutionResult ToResult(RunOutcome outcome, IReadOnlyList<PreflightIssue> issues) =>
            new(RunId, outcome, Succeeded, Failures.ToList(), issues, Journal.Warnings.Concat(Warnings).ToList(),
                Journal.HealthyPaths.FirstOrDefault());
    }
}
