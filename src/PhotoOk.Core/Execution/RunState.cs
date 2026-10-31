using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

public enum OpStatus { Pending, Started, Completed, Skipped, Failed, Undone, UndoSkipped }

/// <summary>
/// 작업 하나의 복원된 상태. <paramref name="CompletedSeq"/>는 Completed 줄의 1부터 센 줄 번호(없으면 0),
/// <paramref name="LastSeq"/>는 이 작업의 마지막 줄(Planned 포함)의 줄 번호다.
/// </summary>
public sealed record OpState(PlannedOperation Operation, OpStatus Status, string? Hash, long CompletedSeq, long LastSeq);

/// <summary>
/// 기록 파일 하나에서 복원한 실행 상태. <paramref name="ExpectedOperations"/>는 RunStarted에 적힌 계획 작업 수로,
/// <see cref="Operations"/>의 수와 다르면 계획을 기록하다 끊긴 것이다. <paramref name="EntryCount"/>는 읽어 낸 항목(줄) 수다
/// (잘린 줄을 닫았다는 표시인 TornLineClosed는 사건이 아니므로 세지 않는다: 사본 중 더 긴 기록을 고를 때 표시 때문에 길어 보이지 않게).
/// <paramref name="UndoStarted"/>는 UndoStarted 항목이 있으면 true다.
/// </summary>
public sealed record RunState(string RunId, string ArchiveRoot, IReadOnlyList<OpState> Operations, bool Finished,
    int ExpectedOperations, int EntryCount, bool UndoStarted)
{
    /// <summary>비게 된 폴더를 치울 때 넘지 않는 고른 폴더들(CleanupRoot 항목). 이전 버전의 기록에는 없어 빈 목록이다.</summary>
    public IReadOnlyList<string> CleanupRoots { get; init; } = [];

    /// <summary>이어 해도 사라지지 않아야 하는 경고(Warning 항목), 기록된 순서대로. 이전 버전의 기록에는 없어 빈 목록이다.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
