using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

public enum OpStatus { Pending, Started, Completed, Skipped, Failed, Undone, UndoSkipped }

/// <summary>작업 하나의 복원된 상태. <paramref name="CompletedSeq"/>는 Completed 줄의 1부터 센 줄 번호(없으면 0).</summary>
public sealed record OpState(PlannedOperation Operation, OpStatus Status, string? Hash, long CompletedSeq);

/// <summary>
/// 기록 파일 하나에서 복원한 실행 상태. <paramref name="ExpectedOperations"/>는 RunStarted에 적힌 계획 작업 수로,
/// <see cref="Operations"/>의 수와 다르면 계획을 기록하다 끊긴 것이다. <paramref name="EntryCount"/>는 읽어 낸 항목(줄) 수다.
/// </summary>
public sealed record RunState(string RunId, string ArchiveRoot, IReadOnlyList<OpState> Operations, bool Finished,
    int ExpectedOperations, int EntryCount);
