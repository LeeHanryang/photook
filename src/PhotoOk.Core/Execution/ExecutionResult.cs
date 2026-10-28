namespace PhotoOk.Core.Execution;

/// <summary>
/// 실행이 끝난 방식. <see cref="Cancelled"/>와 <see cref="Interrupted"/>는 기록을 끝맺지 않으므로(RunFinished 없음) 이어 하기 대상으로 남는다.
/// <see cref="PlanIncomplete"/>는 이어 하기 전용이다: 계획을 기록하다 끊긴 실행이라 아무것도 하지 않았고, 다시 계획해야 한다.
/// <see cref="UndoStarted"/>도 이어 하기 전용이다: 되돌리기를 시작한 기록이라 아무것도 하지 않았고 기록에도 쓰지 않았다.
/// </summary>
public enum RunOutcome { Completed, CompletedWithFailures, Cancelled, Interrupted, BlockedByPreflight, PlanIncomplete, UndoStarted }

public sealed record OperationFailure(string OpId, string? Source, string Destination, string Message);

/// <summary>
/// 실행 결과. <see cref="RunOutcome.BlockedByPreflight"/>이면 기록을 만들지 않았으므로 <see cref="RunId"/>는 빈 문자열이고
/// <see cref="JournalPath"/>는 null이다. <see cref="Succeeded"/>는 완료(Completed)와 이미 있어 건너뛴(Skipped) 작업의 수다.
/// <see cref="JournalPath"/>는 [아카이브, 앱 데이터] 순서에서 빠진 줄 없이 끝까지 쓰인 첫 기록 파일이다.
/// </summary>
public sealed record ExecutionResult(string RunId, RunOutcome Outcome, int Succeeded, IReadOnlyList<OperationFailure> Failures,
    IReadOnlyList<PreflightIssue> PreflightIssues, IReadOnlyList<string> Warnings, string? JournalPath);

/// <summary>
/// 실행 진행률. 파일 작업(복사·이동) 하나가 끝날 때마다(완료·건너뜀·실패) 보고한다. 폴더 만들기는 보고하지도 세지도 않는다
/// (미리보기의 파일 수, 결과의 "처리한 파일 N개"와 같은 기준). <see cref="Total"/>은 계획한 파일 작업 수,
/// <see cref="Done"/>은 지금까지 끝난 파일 작업 수(이어 하기는 이전 실행에서 끝난 파일 작업을 포함한다),
/// <see cref="CurrentPath"/>는 방금 끝난 작업의 대상 경로다.
/// </summary>
public sealed record ExecutionProgress(int Done, int Total, string? CurrentPath);
