namespace PhotoOk.Core.Execution;

/// <summary>
/// 실행이 끝난 방식. <see cref="Cancelled"/>와 <see cref="Interrupted"/>는 기록을 끝맺지 않으므로(RunFinished 없음) 이어 하기 대상으로 남는다.
/// </summary>
public enum RunOutcome { Completed, CompletedWithFailures, Cancelled, Interrupted, BlockedByPreflight }

public sealed record OperationFailure(string OpId, string? Source, string Destination, string Message);

/// <summary>
/// 실행 결과. <see cref="RunOutcome.BlockedByPreflight"/>이면 기록을 만들지 않았으므로 <see cref="RunId"/>는 빈 문자열이고
/// <see cref="JournalPath"/>는 null이다. <see cref="Succeeded"/>는 완료(Completed)와 이미 있어 건너뛴(Skipped) 작업의 수다.
/// <see cref="JournalPath"/>는 [아카이브, 앱 데이터] 순서에서 빠진 줄 없이 끝까지 쓰인 첫 기록 파일이다.
/// </summary>
public sealed record ExecutionResult(string RunId, RunOutcome Outcome, int Succeeded, IReadOnlyList<OperationFailure> Failures,
    IReadOnlyList<PreflightIssue> PreflightIssues, IReadOnlyList<string> Warnings, string? JournalPath);
