using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Core.Planning;

public enum TransferMode { Copy, Move }

/// <summary>작업 종류. 가져오기는 다른 곳에서 아카이브로, 흡수는 흩어진 폴더에서 아카이브로, 재정리는 아카이브 안에서 다시 정리한다.</summary>
public enum JobKind { Import, Absorb, Reorganize }

/// <summary>하위 폴더 처리. 유지는 뜻 있는 폴더를 프리셋 폴더 아래에 붙이고, 제자리는 그 파일을 옮기지 않고, 합치기는 유지하지 않는다.</summary>
public enum SubfolderPolicy { Keep, InPlace, Merge }

public enum OperationKind { CreateDirectory, Copy, Move }

/// <summary>
/// 작업을 만들지 않은 이유. <see cref="ReviewKeptInPlace"/>, <see cref="OtherVolume"/>, <see cref="SubfolderKeptInPlace"/>는 파일을 제자리에 두며
/// 그 기록의 대상은 원본 자리와 같다. <see cref="DuplicateInArchive"/>는 아카이브 어딘가에 같은 내용이 있다는 뜻이다.
/// </summary>
public enum SkipReason
{
    AlreadyInPlace,
    DuplicateAtDestination,
    DuplicateInPlan,
    ReviewKeptInPlace,
    OtherVolume,
    DuplicateInArchive,
    SubfolderKeptInPlace,
}

/// <summary>
/// 실행할 작업 하나. <paramref name="Source"/>는 CreateDirectory이면 null이다.
/// <paramref name="Length"/>는 원본 파일 크기(CreateDirectory는 0).
/// </summary>
public sealed record PlannedOperation(string Id, OperationKind Kind, string? Source, string Destination, long Length,
    IReadOnlyList<ReviewReason> Reasons);

/// <summary>작업을 만들지 않고 건너뛴 파일. <paramref name="ExistingPath"/>는 같은 내용이 이미 있는 자리다(<see cref="SkipReason.DuplicateInArchive"/>).</summary>
public sealed record PlannedSkip(string Source, string Destination, SkipReason Reason, string? ExistingPath = null);

/// <summary>
/// 정리 계획. 작업은 적힌 순서대로 실행한다. <paramref name="CleanupRoots"/>는 비게 된 폴더를 치울 때 넘지 않는 고른 폴더들이다
/// (재정리와 이동 흡수에만 있다).
/// </summary>
public sealed record Plan(string ArchiveRoot, IReadOnlyList<PlannedOperation> Operations, IReadOnlyList<PlannedSkip> Skipped,
    IReadOnlyList<string> Notes, IReadOnlyList<string>? CleanupRoots = null);

/// <summary>
/// 계획 요청. <paramref name="Roots"/>는 고른 폴더, <paramref name="Index"/>는 아카이브 색인이다.
/// <paramref name="KeepReviewInPlace"/>이면 판단이 필요한 묶음을 옮기지 않는다.
/// <paramref name="KeptFoldersOf"/>는 묶음이 유지할 하위 폴더 이름들을 돌려주며, <paramref name="Subfolders"/>와 함께 가져오기에는 쓰지 않는다.
/// </summary>
public sealed record PlanRequest(IReadOnlyList<Session> Sessions, Preset Preset, string ArchiveRoot,
    TransferMode Mode, TimeZoneInfo Zone, IReadOnlyDictionary<string, string>? EventNames = null,
    JobKind Kind = JobKind.Import, IReadOnlyList<string>? Roots = null, ArchiveIndex? Index = null,
    bool KeepReviewInPlace = false, SubfolderPolicy Subfolders = SubfolderPolicy.Keep,
    Func<FileGroup, IReadOnlyList<string>>? KeptFoldersOf = null);
