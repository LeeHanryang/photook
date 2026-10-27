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
/// UndoSkipped는 최종이다. 다만 다시 연결·재시도하면 풀리는 사유는 기록하지 않고 결과로만 알려, 나중에 되돌리기를 다시 하면 이어서 처리된다.
/// 그중 디스크 상태를 그대로 남겨 앞 작업들의 판단을 틀리게 할 수 있는 것(작업 중 입출력 오류, 원래 드라이브가 없어 못 돌려놓은 이동)은
/// 그 자리에서 이번 되돌리기를 멈춘다. 뒤의 작업은 그 작업이 풀린 뒤의 디스크를 봐야 하기 때문이다.
/// 원본을 확인할 수 없어 복사본을 남기는 경우는 멈추지 않는다. 다만 그 복사본 자리가 아직 되돌리지 않은 다른 작업의 원래 자리라면
/// 남긴 복사본이 그 작업을 막으므로 멈춘다. 이 경우는 시작 전에 미리 확인해, 파일을 하나도 건드리지 않고 UndoStarted도 쓰지 않은 채
/// 그 사유 하나만 돌려준다(루프 안의 같은 확인은 안전망이다).
/// 사진을 임시 이름(<c>.photook-tmp</c>, 스캔에 보이지 않는다)으로 남기지 않는다: 원래 자리에 다른 파일이나 폴더가 있으면
/// 원래 이름 옆의 첫 빈 이름(<c>이름_1.확장자</c>…)으로 돌려놓고 Undone으로 기록하며, 결과에 그 경로를 알린다.
/// 이 실행의 자리 바꾸기 임시 경로에 있는 파일은 크기가 달라졌어도 항상 꺼낸다.
/// 그런 사유가 있었던 되돌리기에서는 "폴더가 비어 있지 않습니다"도 기록하지 않는다(남긴 복사본 때문에 비어 있지 않을 수 있다).
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
    internal const string RenamedBackMessage = "원래 자리에 다른 파일이 있어 이름을 바꿔 되돌렸습니다: ";
    internal const string SizeChangedRestoredMessage = "크기가 달라졌지만 숨겨지지 않도록 원래 자리로 되돌렸습니다: ";

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
            new SafeDelete(fs, state.ArchiveRoot, state.RunId), state.Operations);

        // 기록에 나중에 나온 작업부터. 앞 작업의 대상을 뒤 작업이 원본으로 쓴 경우(자리 바꾸기)에도 역순이면 맞게 풀린다.
        var pending = state.Operations
            .Where(o => o.Status is OpStatus.Completed or OpStatus.Started or OpStatus.Failed)
            .OrderByDescending(o => o.LastSeq)
            .ToList();

        // 남길 복사본이 다른 작업의 원래 자리를 막고 있으면 파일을 하나도 건드리기 전에 멈춘다. 루프 안에서 멈추면 이미 처리한
        // 뒤 작업(자리 바꾸기의 두 번째 다리)이 사진을 임시 이름으로 옮긴 뒤라, 드라이브를 다시 꽂을 때까지 사진이 숨는다.
        var blocking = FindKeptCopyHoldingOpenSpot(run, pending);
        if (blocking is not null)
        {
            return new UndoResult(0, [blocking]);
        }

        // 이어 하기가 이 기록으로 작업을 다시 하지 않도록, 파일을 건드리기 전에 표시한다.
        if (!state.UndoStarted)
        {
            run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.UndoStarted, TimestampUtc: context.UtcNow()));
        }

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

                outcome = Outcome.Blocked(Executor.DescribeFailure(ex));
            }

            Apply(run, opState.Operation, outcome);
            if (outcome.Kind == OutcomeKind.Blocked)
            {
                break;
            }
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
                return KeepCopyForNow(run, op);
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
            return Outcome.Blocked(SourceVolumeMissingMessage);
        }

        var sourceExists = fs.FileExists(op.Source);
        if (!fs.FileExists(op.Destination))
        {
            // 원래 자리에 같은 크기의 파일이 있으면 앞선 되돌리기가 돌려놓고 기록하기 전에 끊긴 것이다.
            return sourceExists && fs.GetFile(op.Source).Length == op.Length
                ? Outcome.Undone(op.Source)
                : Outcome.Skipped(MovedFileMissingMessage);
        }

        if (sourceExists || fs.DirectoryExists(op.Source))
        {
            // 원래 자리에 파일이든 폴더든 있으면 자리가 찬 것이다.
            // 임시 이름은 스캔에 보이지 않는다. 그 이름으로 남기지 말고 원래 자리 옆에 보이는 이름으로 돌려놓는다.
            return IsTempName(op.Destination) && SameVolume(op.Source, op.Destination)
                ? RestoreBesideOriginal(op)
                : Outcome.Skipped(SpotTakenMessage);
        }

        if (!SameVolume(op.Source, op.Destination))
        {
            return Outcome.Skipped(Executor.CrossVolumeMoveMessage);
        }

        if (fs.GetFile(op.Destination).Length != op.Length)
        {
            return IsRunTempMove(op) ? MoveBackDespiteSize(op) : Outcome.Skipped(ModifiedAfterRunMessage);
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
                return KeepCopyForNow(run, op);
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
            return Outcome.Blocked(SourceVolumeMissingMessage);
        }

        if (!SameVolume(op.Source, op.Destination))
        {
            return Outcome.Skipped(NotPerformedMessage);
        }

        // 이 실행의 임시 이름(원래 이름 + .photook-tmp- + 이 작업의 ID)에 있는 파일은 이 작업이 옮긴 것이다. 크기와 상관없이 꺼낸다.
        var runTemp = IsRunTempMove(op);
        var sizeMatches = fs.GetFile(op.Destination).Length == op.Length;
        if (fs.FileExists(op.Source) || fs.DirectoryExists(op.Source))
        {
            // 원래 자리에 파일이든 폴더든 있으면 자리가 찬 것이다.
            // 임시 이름에 계획한 크기의 파일이 있으면 이 작업이 옮긴 것이다. 임시 이름으로 남기지 않는다.
            return runTemp || (IsTempName(op.Destination) && sizeMatches)
                ? RestoreBesideOriginal(op)
                : Outcome.Skipped(NotPerformedMessage);
        }

        // 옮긴 뒤 크기가 달라 실패로 기록된 경우. 파일은 새 자리에 있으므로 알린다.
        if (!sizeMatches)
        {
            return runTemp ? MoveBackDespiteSize(op) : Outcome.Skipped(ModifiedAfterRunMessage);
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

    /// <summary>
    /// 원본을 확인할 수 없어 복사본을 남긴다. 복사본 자리가 아직 되돌리지 않은 다른 작업의 원래 자리라면(자리 바꾸기에서 비운 자리를
    /// 다른 드라이브의 복사가 채운 경우) 남긴 복사본이 그 작업을 막으므로 이번 되돌리기를 멈춘다. 아니면 기록하지 않고 계속한다.
    /// 보통은 <see cref="FindKeptCopyHoldingOpenSpot"/>가 시작 전에 같은 경우를 잡는다. 여기는 그 뒤 디스크가 바뀐 경우의 안전망이다.
    /// </summary>
    private Outcome KeepCopyForNow(UndoRun run, PlannedOperation op)
    {
        if (!run.HoldsOpenSpot(op))
        {
            return Outcome.Transient(SourceGoneMessage);
        }

        return Outcome.Blocked(KeptCopyReason(op));
    }

    private string KeptCopyReason(PlannedOperation op) =>
        op.Source is not null && !IsVolumePresent(op.Source) ? Executor.SourceVolumeMissingMessage : SourceGoneMessage;

    /// <summary>
    /// 시작 전 미리 확인: 이번 되돌리기가 처리할 복사 중, 원본을 확인할 수 없어(드라이브 없음·읽기 불가) 남겨야 하는데 그 복사본 자리가
    /// 아직 되돌리지 않은 다른 작업의 원래 자리인 것. 있으면 그 복사의 사유를 돌려주고, 되돌리기는 아무것도 쓰지 않고 멈춰야 한다.
    /// 루프에서 <see cref="KeepCopyForNow"/>가 막는 조건과 같게 본다: 복사본이 있고, Completed면 기록한 해시 그대로여야 한다
    /// (복사본을 읽을 수 없으면 그대로인 것으로 보고 막는 쪽을 택한다). 아무 파일도 바꾸지 않는다.
    /// </summary>
    private UndoSkip? FindKeptCopyHoldingOpenSpot(UndoRun run, IEnumerable<OpState> pending)
    {
        var fs = context.FileSystem;
        foreach (var state in pending)
        {
            var op = state.Operation;
            if (op.Kind != OperationKind.Copy || op.Source is null || !fs.FileExists(op.Destination))
            {
                continue;
            }

            if (state.Status == OpStatus.Completed && !CopyLooksIntact(op, state.Hash))
            {
                continue;
            }

            if (IsSourceUnavailable(op.Source) && run.HoldsOpenSpot(op))
            {
                return new UndoSkip(op.Id, op.Destination, KeptCopyReason(op));
            }
        }

        return null;
    }

    /// <summary>복사본이 기록한 해시 그대로인지. 기록이 없으면 루프가 "실행 후 바뀐 파일"로 남기므로 false. 읽을 수 없으면 true(보수적).</summary>
    private bool CopyLooksIntact(PlannedOperation op, string? hash)
    {
        if (hash is null)
        {
            return false;
        }

        try
        {
            return string.Equals(context.Hasher.Hash(op.Destination), hash, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// 원본 드라이브가 없거나, 원본 파일이 있는데 열 수 없는지(<see cref="SourceState.Unavailable"/>와 같은 조건).
    /// 원본이 없는 것은 해당하지 않는다(그 복사본은 최종적으로 남기고, 막힌 자리는 이름을 바꿔 되돌리기로 푼다). 파일은 열어 보기만 한다.
    /// </summary>
    private bool IsSourceUnavailable(string source)
    {
        var fs = context.FileSystem;
        if (!IsVolumePresent(source))
        {
            return true;
        }

        if (!fs.FileExists(source))
        {
            return false;
        }

        try
        {
            using var stream = fs.OpenRead(source);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// 원래 자리에 다른 파일이 있을 때 임시 이름의 파일을 원래 이름 옆의 첫 빈 이름(<c>이름_1.확장자</c>, <c>_2</c>…)으로 옮긴다.
    /// 덮어쓰지 않는 이동이므로 그사이 이름이 채워지면 예외가 나고 이번 되돌리기는 멈춘다.
    /// </summary>
    private Outcome RestoreBesideOriginal(PlannedOperation op)
    {
        var fs = context.FileSystem;
        var source = op.Source!;
        var directory = Path.GetDirectoryName(source)!;
        var name = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        string target;
        var n = 1;
        do
        {
            target = Path.Combine(directory, $"{name}_{n}{extension}");
            n++;
        }
        while (fs.FileExists(target) || fs.DirectoryExists(target));

        fs.CreateDirectory(directory);
        fs.Move(op.Destination, target);
        return Outcome.Undone(target, RenamedBackMessage + target);
    }

    /// <summary>원래 자리가 비어 있으면 크기가 달라졌어도 임시 이름에 숨겨 두지 않고 원래 자리로 돌려놓고 알린다.</summary>
    private Outcome MoveBackDespiteSize(PlannedOperation op)
    {
        MoveBack(op);
        return Outcome.Undone(op.Source, SizeChangedRestoredMessage + op.Source);
    }

    /// <summary>
    /// 자리 바꾸기의 첫 다리인지: 대상이 정확히 <c>원본 경로 + ".photook-tmp-" + 이 작업의 ID</c>다.
    /// 그 경로에 있는 파일은 이 실행의 이 작업만 만들 수 있다. 두 번째 다리(임시 이름에서 최종 자리로)는 해당하지 않는다.
    /// </summary>
    private static bool IsRunTempMove(PlannedOperation op) =>
        op.Kind == OperationKind.Move && op.Source is not null
        && string.Equals(op.Destination, op.Source + Executor.TempSuffix + "-" + op.Id, StringComparison.OrdinalIgnoreCase);

    private static bool IsTempName(string path) =>
        Path.GetFileName(path).Contains(Executor.TempSuffix, StringComparison.OrdinalIgnoreCase);

    private void Apply(UndoRun run, PlannedOperation op, Outcome outcome)
    {
        if (outcome.Kind is OutcomeKind.Undone or OutcomeKind.Skipped)
        {
            run.Resolved.Add(op.Id);
        }

        switch (outcome.Kind)
        {
            case OutcomeKind.Undone:
                run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.Undone, op.Id, op.Kind, Destination: outcome.Location,
                    TimestampUtc: context.UtcNow()));
                run.Undone++;
                if (outcome.Reason is not null)
                {
                    run.Skipped.Add(new UndoSkip(op.Id, op.Destination, outcome.Reason));
                }

                break;

            case OutcomeKind.Skipped:
                run.Journal.Write(new JournalEntry(run.RunId, JournalEvent.UndoSkipped, op.Id, op.Kind, Message: outcome.Reason,
                    TimestampUtc: context.UtcNow()));
                run.Skipped.Add(new UndoSkip(op.Id, op.Destination, outcome.Reason!));
                break;

            case OutcomeKind.Transient:
            case OutcomeKind.Blocked:
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

    private enum OutcomeKind { Undone, Skipped, Transient, Blocked }

    private enum SourceState { Matches, Differs, Missing, Unavailable }

    /// <summary>
    /// 작업 하나의 되돌리기 결과. Undone·Skipped는 기록하고, Transient·Blocked는 결과로만 알린다(다음 되돌리기에서 다시 본다).
    /// Blocked는 이번 되돌리기를 그 자리에서 멈춘다. Transient는 디스크 상태를 바꾸지 않고 남기는 경우(복사본 유지)라 계속한다.
    /// <see cref="Location"/>은 Undone일 때 파일이 지금 있는 곳(격리 경로나 원래 자리).
    /// </summary>
    private readonly record struct Outcome(OutcomeKind Kind, string? Reason, string? Location)
    {
        /// <summary><paramref name="note"/>가 있으면 기록은 Undone이지만 사용자에게 알릴 내용이 있다(결과의 Skipped에 함께 싣는다).</summary>
        public static Outcome Undone(string? location, string? note = null) => new(OutcomeKind.Undone, note, location);

        public static Outcome Skipped(string reason) => new(OutcomeKind.Skipped, reason, null);

        public static Outcome Transient(string reason) => new(OutcomeKind.Transient, reason, null);

        public static Outcome Blocked(string reason) => new(OutcomeKind.Blocked, reason, null);
    }

    private sealed class UndoRun(string runId, string archiveRoot, JournalWriter journal, SafeDelete safeDelete,
        IReadOnlyList<OpState> operations)
    {
        /// <summary>이번 되돌리기에서 Undone·UndoSkipped로 기록한 작업.</summary>
        public HashSet<string> Resolved { get; } = new(StringComparer.Ordinal);

        /// <summary>복사본 자리가 아직 Undone·UndoSkipped가 아닌 다른 작업의 원래 자리(원본 경로)인지.</summary>
        public bool HoldsOpenSpot(PlannedOperation copy)
        {
            var spot = Path.GetFullPath(copy.Destination);
            return operations.Any(o => o.Operation.Id != copy.Id
                && o.Operation.Source is not null
                && o.Status is not (OpStatus.Undone or OpStatus.UndoSkipped)
                && !Resolved.Contains(o.Operation.Id)
                && string.Equals(Path.GetFullPath(o.Operation.Source), spot, StringComparison.OrdinalIgnoreCase));
        }

        public string RunId { get; } = runId;

        public string ArchiveRoot { get; } = archiveRoot;

        public JournalWriter Journal { get; } = journal;

        public SafeDelete SafeDelete { get; } = safeDelete;

        public List<UndoSkip> Skipped { get; } = [];

        public int Undone { get; set; }

        public bool HadTransient { get; set; }
    }
}
