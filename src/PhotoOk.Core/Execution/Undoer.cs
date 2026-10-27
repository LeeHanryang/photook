using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

/// <summary>되돌리지 않고 남긴 작업. 실행 전체를 막는 문제(보관 드라이브 없음)는 <see cref="OpId"/>가 빈 문자열이다.</summary>
public sealed record UndoSkip(string OpId, string Path, string Reason);

/// <summary><see cref="Undone"/>은 이번 되돌리기에서 Undone으로 기록한 작업 수다.</summary>
public sealed record UndoResult(int Undone, IReadOnlyList<UndoSkip> Skipped);

/// <summary>
/// 실행 하나를 기록만으로 되돌린다. 원본 보존이 최우선이다.
/// <list type="bullet">
/// <item>복사본은 원본이 아직 있고 내용이 같을 때만 격리 폴더로 옮긴다(지우지 않는다). 원본이 없으면 복사본이 유일한 사본이므로 남긴다.</item>
/// <item>옮긴 파일은 원래 자리가 비어 있을 때만 돌려놓는다. 실행 후 바뀐 파일은 그대로 둔다.</item>
/// <item>이번 실행이 만든 폴더는 비어 있을 때만 지운다. 원래 있던 폴더(Skipped)는 건드리지 않는다.</item>
/// </list>
/// 처리 순서: Completed·Started·Failed 작업을 한 번에, 기록에 마지막으로 나온 순서의 역순(<see cref="OpState.LastSeq"/> 내림차순)으로
/// 처리한다. Completed는 기록대로 되돌리고, Started·Failed(실제로는 끝났을 수 있다)는 디스크를 보고 실제로 한 일만 되돌린다.
/// 실행되지 않았다고 판단한 Started·Failed 작업은 "실행되지 않은 작업이라 되돌릴 것이 없습니다"로 기록해 끝낸다. 그래야 다음 되돌리기가
/// 이미 돌려놓은 파일을 그 작업의 결과로 착각하지 않는다.
/// 첫 파일 작업 전에 UndoStarted를 한 번 쓴다(이미 있으면 쓰지 않는다). 이 표시가 있는 기록은 이어 하기가 거부한다.
/// 결과는 작업마다 Undone 또는 UndoSkipped(Message=사유)로 같은 기록 사본에 쓴다. 기록 사본은 이어 하기와 같은 방식으로 고르고
/// (같은 실행ID에서 항목이 가장 많은 것) 그 사본에만 쓴다. 이미 Undone·UndoSkipped인 작업은 건너뛰므로 다시 실행해도 안전하다.
/// UndoSkipped는 최종이다. 다만 드라이브가 빠졌거나 입출력 오류처럼 다시 연결·재시도하면 풀리는 사유는 기록하지 않고 결과로만 알려,
/// 나중에 되돌리기를 다시 하면 이어서 처리된다. 그런 사유가 하나라도 있었던 되돌리기에서는 "폴더가 비어 있지 않습니다"도 기록하지 않는다.
/// 보관 드라이브가 없으면 아무것도 쓰지 않고 그 사유 하나만 돌려준다. 받은 기록 파일 자체를 읽을 수 없으면 예외가 그대로 나간다.
/// 기록을 남길 수 없으면 <see cref="JournalUnavailableException"/>.
/// </summary>
public sealed class Undoer(ExecutionContext context)
{
    internal const string CopyMissingMessage = "복사본이 없습니다";
    internal const string ModifiedAfterRunMessage = "실행 후 바뀐 파일이라 그대로 둡니다";
    internal const string SourceGoneMessage = "원본이 없어 복사본을 유지합니다";
    internal const string SpotTakenMessage = "원래 자리에 다른 파일이 있습니다";
    internal const string MovedFileMissingMessage = "옮긴 파일을 찾을 수 없습니다";
    internal const string FolderNotEmptyMessage = "폴더가 비어 있지 않습니다";
    internal const string SourceVolumeMissingMessage = "원래 드라이브가 연결되어 있지 않아 옮기지 않았습니다";
    internal const string NotPerformedMessage = "실행되지 않은 작업이라 되돌릴 것이 없습니다";
    internal const string UnknownFolderMessage = "만들었는지 알 수 없는 폴더라 그대로 둡니다";

    public UndoResult Undo(string journalPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        var fs = context.FileSystem;
        var (path, state, _) = JournalCopies.Choose(fs, context.AppDataRoot, journalPath);

        // 보관 드라이브가 없으면 복사본은 "없음"으로 보인다. 그런 상태로 최종 판단을 기록하면 안 된다.
        if (!IsVolumePresent(state.ArchiveRoot))
        {
            return new UndoResult(0, [new UndoSkip(string.Empty, state.ArchiveRoot, Executor.ArchiveVolumeMissingMessage)]);
        }

        var run = new UndoRun(state.RunId, state.ArchiveRoot, new JournalWriter(fs, [path]),
            new SafeDelete(fs, state.ArchiveRoot, state.RunId));

        // 이어 하기가 이 기록으로 작업을 다시 하지 않도록, 파일을 건드리기 전에 표시한다.
        if (!state.UndoStarted)
        {
            run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.UndoStarted, TimestampUtc: context.UtcNow()));
        }

        // 기록에 나중에 나온 작업부터. 앞 작업의 대상을 뒤 작업이 원본으로 쓴 경우(자리 바꾸기)에도 역순이면 맞게 풀린다.
        var pending = state.Operations
            .Where(o => o.Status is OpStatus.Completed or OpStatus.Started or OpStatus.Failed)
            .OrderByDescending(o => o.LastSeq);

        foreach (var opState in pending)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!IsVolumePresent(state.ArchiveRoot))
            {
                run.Skipped.Add(new UndoSkip(string.Empty, state.ArchiveRoot, Executor.ArchiveVolumeMissingMessage));
                break;
            }

            Outcome outcome;
            try
            {
                outcome = opState.Status == OpStatus.Completed
                    ? UndoCompleted(run, opState)
                    : UndoUnrecorded(run, opState.Operation);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!IsVolumePresent(state.ArchiveRoot))
                {
                    run.Skipped.Add(new UndoSkip(string.Empty, state.ArchiveRoot, Executor.ArchiveVolumeMissingMessage));
                    break;
                }

                outcome = Outcome.Transient(Executor.DescribeFailure(ex));
            }

            Apply(run, opState.Operation, outcome);
        }

        return new UndoResult(run.Undone, run.Skipped.ToList());
    }

    // ------------------------------------------------------------------
    // Completed 작업
    // ------------------------------------------------------------------

    private Outcome UndoCompleted(UndoRun run, OpState state) => state.Operation.Kind switch
    {
        OperationKind.Copy => UndoCopy(run, state.Operation, state.Hash),
        OperationKind.Move => UndoMove(state.Operation),
        OperationKind.CreateDirectory => UndoCreateDirectory(run, state.Operation),
        _ => throw new InvalidOperationException($"알 수 없는 작업 종류입니다: {state.Operation.Kind}"),
    };

    /// <summary>복사본이 기록한 해시(<paramref name="hash"/>) 그대로이고 원본도 그대로일 때만 복사본을 격리한다.</summary>
    private Outcome UndoCopy(UndoRun run, PlannedOperation op, string? hash)
    {
        var fs = context.FileSystem;
        if (!fs.FileExists(op.Destination))
        {
            // 앞선 되돌리기가 격리한 뒤 기록하기 전에 끊겼다면 복사본은 격리 폴더에 있다.
            var quarantined = Path.Combine(run.SafeDelete.QuarantineRoot, Path.GetRelativePath(run.ArchiveRoot, op.Destination));
            return hash is not null && fs.FileExists(quarantined)
                   && string.Equals(context.Hasher.Hash(quarantined), hash, StringComparison.Ordinal)
                ? Outcome.Undone(quarantined)
                : Outcome.Skipped(CopyMissingMessage);
        }

        var destinationHash = context.Hasher.Hash(op.Destination);
        if (hash is null || !string.Equals(destinationHash, hash, StringComparison.Ordinal))
        {
            return Outcome.Skipped(ModifiedAfterRunMessage);
        }

        switch (CheckSource(op, hash))
        {
            case SourceState.Matches:
                break;
            case SourceState.Unavailable:
                return Outcome.Transient(SourceGoneMessage);
            default:
                return Outcome.Skipped(SourceGoneMessage);
        }

        return Outcome.Undone(run.SafeDelete.Quarantine(op.Destination));
    }

    /// <summary>옮긴 파일이 그대로 있고 원래 자리가 비어 있으면 돌려놓는다(원래 폴더가 없으면 만든다).</summary>
    private Outcome UndoMove(PlannedOperation op)
    {
        var fs = context.FileSystem;
        if (op.Source is null)
        {
            return Outcome.Skipped(Executor.MissingSourceMessage);
        }

        if (!IsVolumePresent(op.Source))
        {
            return Outcome.Transient(SourceVolumeMissingMessage);
        }

        var sourceExists = fs.FileExists(op.Source);
        if (!fs.FileExists(op.Destination))
        {
            // 원래 자리에 같은 크기의 파일이 있으면 앞선 되돌리기가 돌려놓고 기록하기 전에 끊긴 것이다.
            return sourceExists && fs.GetFile(op.Source).Length == op.Length
                ? Outcome.Undone(op.Source)
                : Outcome.Skipped(MovedFileMissingMessage);
        }

        if (sourceExists)
        {
            return Outcome.Skipped(SpotTakenMessage);
        }

        if (!SameVolume(op.Source, op.Destination))
        {
            return Outcome.Skipped(Executor.CrossVolumeMoveMessage);
        }

        if (fs.GetFile(op.Destination).Length != op.Length)
        {
            return Outcome.Skipped(ModifiedAfterRunMessage);
        }

        MoveBack(op);
        return Outcome.Undone(op.Source);
    }

    private Outcome UndoCreateDirectory(UndoRun run, PlannedOperation op)
    {
        // 이미 없으면 되돌릴 것이 없다(앞선 되돌리기가 지우고 기록하기 전에 끊겼을 수도 있다).
        if (!run.SafeDelete.RemoveCreatedDirectory(op.Destination) && context.FileSystem.DirectoryExists(op.Destination))
        {
            return run.HadTransient ? Outcome.Transient(FolderNotEmptyMessage) : Outcome.Skipped(FolderNotEmptyMessage);
        }

        return Outcome.Undone(null);
    }

    // ------------------------------------------------------------------
    // Started/Failed 작업: 실제로 끝났을 때만 되돌린다
    // ------------------------------------------------------------------

    private Outcome UndoUnrecorded(UndoRun run, PlannedOperation op) => op.Kind switch
    {
        OperationKind.Copy => UndoUnrecordedCopy(run, op),
        OperationKind.Move => UndoUnrecordedMove(op),
        // 끊긴 폴더 만들기는 원래 있던 폴더인지 알 수 없으므로 남긴다(빈 폴더는 파일을 잃게 하지 않는다).
        _ => context.FileSystem.DirectoryExists(op.Destination)
            ? Outcome.Skipped(UnknownFolderMessage)
            : Outcome.Skipped(NotPerformedMessage),
    };

    /// <summary>
    /// 끊긴 복사가 남긴 임시 파일을 격리하고, 대상에 지금 원본과 내용이 같은 파일이 있으면 완료된 복사처럼 격리한다.
    /// 내용이 다른 파일은 이번 실행이 만든 것이 아닐 수 있으므로 손대지 않는다.
    /// </summary>
    private Outcome UndoUnrecordedCopy(UndoRun run, PlannedOperation op)
    {
        var fs = context.FileSystem;
        var temp = op.Destination + Executor.TempSuffix;
        if (fs.FileExists(temp))
        {
            run.SafeDelete.Quarantine(temp);
        }

        if (op.Source is null || !fs.FileExists(op.Destination))
        {
            return Outcome.Skipped(NotPerformedMessage);
        }

        switch (CheckSource(op, context.Hasher.Hash(op.Destination)))
        {
            case SourceState.Matches:
                break;
            case SourceState.Unavailable:
                return Outcome.Transient(SourceGoneMessage);
            case SourceState.Missing:
                return Outcome.Skipped(SourceGoneMessage);
            default:
                // 원본과 내용이 다르면 이 파일이 이번 실행의 복사본이라고 볼 근거가 없다.
                return Outcome.Skipped(NotPerformedMessage);
        }

        return Outcome.Undone(run.SafeDelete.Quarantine(op.Destination));
    }

    /// <summary>대상에 계획한 크기의 파일이 있고 원래 자리가 비어 있으면, 옮긴 뒤 기록하지 못한 것이므로 돌려놓는다.</summary>
    private Outcome UndoUnrecordedMove(PlannedOperation op)
    {
        var fs = context.FileSystem;
        if (op.Source is null || !fs.FileExists(op.Destination))
        {
            return Outcome.Skipped(NotPerformedMessage);
        }

        if (!IsVolumePresent(op.Source))
        {
            return Outcome.Transient(SourceVolumeMissingMessage);
        }

        if (fs.FileExists(op.Source) || !SameVolume(op.Source, op.Destination))
        {
            return Outcome.Skipped(NotPerformedMessage);
        }

        // 옮긴 뒤 크기가 달라 실패로 기록된 경우. 파일은 새 자리에 있으므로 알린다.
        if (fs.GetFile(op.Destination).Length != op.Length)
        {
            return Outcome.Skipped(ModifiedAfterRunMessage);
        }

        MoveBack(op);
        return Outcome.Undone(op.Source);
    }

    // ------------------------------------------------------------------
    // 도우미
    // ------------------------------------------------------------------

    /// <summary>
    /// 복사본을 치워도 되는지 원본을 확인한다. 원본 드라이브가 없거나 원본을 읽을 수 없으면 <see cref="SourceState.Unavailable"/>
    /// (다시 연결하면 풀리는 일시적인 상태), 원본 경로나 파일이 없으면 <see cref="SourceState.Missing"/>.
    /// </summary>
    private SourceState CheckSource(PlannedOperation op, string expectedHash)
    {
        var fs = context.FileSystem;
        if (op.Source is null)
        {
            return SourceState.Missing;
        }

        if (!IsVolumePresent(op.Source))
        {
            return SourceState.Unavailable;
        }

        if (!fs.FileExists(op.Source))
        {
            return SourceState.Missing;
        }

        try
        {
            var sourceHash = context.Hasher.Hash(op.Source);
            return string.Equals(sourceHash, expectedHash, StringComparison.Ordinal) ? SourceState.Matches : SourceState.Differs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SourceState.Unavailable;
        }
    }

    private void MoveBack(PlannedOperation op)
    {
        var fs = context.FileSystem;
        var parent = Path.GetDirectoryName(op.Source!);
        if (!string.IsNullOrEmpty(parent))
        {
            fs.CreateDirectory(parent);
        }

        fs.Move(op.Destination, op.Source!);
    }

    private void Apply(UndoRun run, PlannedOperation op, Outcome outcome)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Undone:
                run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.Undone, op.Id, op.Kind, Destination: outcome.Location,
                    TimestampUtc: context.UtcNow()));
                run.Undone++;
                break;

            case OutcomeKind.Skipped:
                run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.UndoSkipped, op.Id, op.Kind, Message: outcome.Reason,
                    TimestampUtc: context.UtcNow()));
                run.Skipped.Add(new UndoSkip(op.Id, op.Destination, outcome.Reason!));
                break;

            case OutcomeKind.Transient:
                run.HadTransient = true;
                run.Skipped.Add(new UndoSkip(op.Id, op.Destination, outcome.Reason!));
                break;
        }
    }

    private bool IsVolumePresent(string path)
    {
        var fs = context.FileSystem;
        return fs.DirectoryExists(fs.GetVolumeRoot(path));
    }

    private bool SameVolume(string a, string b)
    {
        var fs = context.FileSystem;
        return string.Equals(fs.GetVolumeRoot(a), fs.GetVolumeRoot(b), StringComparison.OrdinalIgnoreCase);
    }

    private enum OutcomeKind { Undone, Skipped, Transient }

    private enum SourceState { Matches, Differs, Missing, Unavailable }

    /// <summary>
    /// 작업 하나의 되돌리기 결과. Undone·Skipped는 기록하고, Transient는 결과로만 알린다(다음 되돌리기에서 다시 본다).
    /// <see cref="Location"/>은 Undone일 때 파일이 지금 있는 곳(격리 경로나 원래 자리).
    /// </summary>
    private readonly record struct Outcome(OutcomeKind Kind, string? Reason, string? Location)
    {
        public static Outcome Undone(string? location) => new(OutcomeKind.Undone, null, location);

        public static Outcome Skipped(string reason) => new(OutcomeKind.Skipped, reason, null);

        public static Outcome Transient(string reason) => new(OutcomeKind.Transient, reason, null);
    }

    private sealed class UndoRun(string runId, string archiveRoot, JournalWriter journal, SafeDelete safeDelete)
    {
        public string RunId { get; } = runId;

        public string ArchiveRoot { get; } = archiveRoot;

        public JournalWriter Journal { get; } = journal;

        public SafeDelete SafeDelete { get; } = safeDelete;

        public List<UndoSkip> Skipped { get; } = [];

        public int Undone { get; set; }

        public bool HadTransient { get; set; }
    }
}
