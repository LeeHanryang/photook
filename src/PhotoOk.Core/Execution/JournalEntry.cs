using PhotoOk.Core.Planning;

namespace PhotoOk.Core.Execution;

/// <summary>
/// <see cref="UndoStarted"/>는 작업 ID 없이 한 번만 쓴다: 되돌리기를 시작한 기록은 이어 하기 대상이 아니다.
/// <see cref="TornLineClosed"/>는 작업 ID 없는 표시다: 줄바꿈 없이 끊긴 마지막 줄을 닫았다는 뜻이며, 바로 앞의 읽을 수 없는 줄은
/// 손상이 아니라 쓰다 만 줄로 보고 무시한다. 상태에는 영향이 없다.
/// <see cref="CleanupRoot"/>(고른 폴더), <see cref="FolderRemoved"/>(치운 폴더), <see cref="Warning"/>(이어 해도 남길 경고)도 작업 ID가 없고
/// 작업 상태를 바꾸지 않는다. 다만 사건이므로 항목 수에는 들어간다.
/// <see cref="FolderRemoved"/>는 사람이 볼 기록일 뿐 이 값을 읽어 판단하는 코드는 없다(항목 수에만 들어간다). 폴더를 치운 뒤 기록하기 전에
/// 끊기면 이 줄이 남지 않으므로, 치운 폴더의 전체 목록으로 믿으면 안 된다. 이어 하기와 되돌리기는 디스크를 보고 판단한다.
/// </summary>
public enum JournalEvent
{
    RunStarted, Planned, Started, Completed, Skipped, Failed, Undone, UndoSkipped, RunFinished, UndoStarted, TornLineClosed,
    CleanupRoot, FolderRemoved, Warning,
}

/// <summary>
/// 작업 기록 한 줄. <c>RunStarted</c>의 <see cref="Destination"/>에는 아카이브 루트를, <c>CleanupRoot</c>에는 고른 폴더를,
/// <c>FolderRemoved</c>에는 치운 폴더를 넣는다. <c>Warning</c>의 문구는 <see cref="Message"/>에 넣는다.
/// 검토 사유(ReviewReason)는 기록하지 않는다.
/// </summary>
public sealed record JournalEntry(string RunId, JournalEvent Event, string? OpId = null, OperationKind? Kind = null,
    string? Source = null, string? Destination = null, long Length = 0, string? Hash = null, string? Message = null,
    DateTime TimestampUtc = default);
