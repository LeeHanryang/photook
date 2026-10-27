using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

/// <summary>
/// <see cref="UndoStarted"/>는 작업 ID 없이 한 번만 쓴다: 되돌리기를 시작한 기록은 이어 하기 대상이 아니다.
/// <see cref="TornLineClosed"/>는 작업 ID 없는 표시다: 줄바꿈 없이 끊긴 마지막 줄을 닫았다는 뜻이며, 바로 앞의 읽을 수 없는 줄은
/// 손상이 아니라 쓰다 만 줄로 보고 무시한다. 상태에는 영향이 없다.
/// </summary>
public enum JournalEvent
{
    RunStarted, Planned, Started, Completed, Skipped, Failed, Undone, UndoSkipped, RunFinished, UndoStarted, TornLineClosed,
}

/// <summary>
/// 작업 기록 한 줄. <c>RunStarted</c>의 <see cref="Destination"/>에는 아카이브 루트를 넣는다.
/// 검토 사유(ReviewReason)는 기록하지 않는다.
/// </summary>
public sealed record JournalEntry(string RunId, JournalEvent Event, string? OpId = null, OperationKind? Kind = null,
    string? Source = null, string? Destination = null, long Length = 0, string? Hash = null, string? Message = null,
    DateTime TimestampUtc = default);
