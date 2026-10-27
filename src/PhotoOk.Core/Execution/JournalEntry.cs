using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

/// <summary><see cref="UndoStarted"/>는 작업 ID 없이 한 번만 쓴다: 되돌리기를 시작한 기록은 이어 하기 대상이 아니다.</summary>
public enum JournalEvent { RunStarted, Planned, Started, Completed, Skipped, Failed, Undone, UndoSkipped, RunFinished, UndoStarted }

/// <summary>
/// 작업 기록 한 줄. <c>RunStarted</c>의 <see cref="Destination"/>에는 아카이브 루트를 넣는다.
/// 검토 사유(ReviewReason)는 기록하지 않는다.
/// </summary>
public sealed record JournalEntry(string RunId, JournalEvent Event, string? OpId = null, OperationKind? Kind = null,
    string? Source = null, string? Destination = null, long Length = 0, string? Hash = null, string? Message = null,
    DateTime TimestampUtc = default);
