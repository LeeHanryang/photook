using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

public enum OpStatus { Pending, Started, Completed, Skipped, Failed, Undone, UndoSkipped }

/// <summary>작업 하나의 복원된 상태. <paramref name="CompletedSeq"/>는 Completed 줄의 1부터 센 줄 번호(없으면 0).</summary>
public sealed record OpState(PlannedOperation Operation, OpStatus Status, string? Hash, long CompletedSeq);

public sealed record RunState(string RunId, string ArchiveRoot, IReadOnlyList<OpState> Operations, bool Finished);
