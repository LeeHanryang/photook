using PhotoOk.Core.Presets;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Core.Planning;

public enum TransferMode { Copy, Move }

public enum OperationKind { CreateDirectory, Copy, Move }

public enum SkipReason { AlreadyInPlace, DuplicateAtDestination, DuplicateInPlan }

/// <summary>
/// 실행할 작업 하나. <paramref name="Source"/>는 CreateDirectory이면 null이다.
/// <paramref name="Length"/>는 원본 파일 크기(CreateDirectory는 0).
/// </summary>
public sealed record PlannedOperation(string Id, OperationKind Kind, string? Source, string Destination, long Length,
    IReadOnlyList<ReviewReason> Reasons);

/// <summary>작업을 만들지 않고 건너뛴 파일.</summary>
public sealed record PlannedSkip(string Source, string Destination, SkipReason Reason);

/// <summary>정리 계획. 작업은 적힌 순서대로 실행한다.</summary>
public sealed record Plan(string ArchiveRoot, IReadOnlyList<PlannedOperation> Operations, IReadOnlyList<PlannedSkip> Skipped,
    IReadOnlyList<string> Notes);

public sealed record PlanRequest(IReadOnlyList<Session> Sessions, Preset Preset, string ArchiveRoot,
    TransferMode Mode, TimeZoneInfo Zone);
