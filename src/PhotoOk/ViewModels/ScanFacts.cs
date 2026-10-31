using PhotoOk.Core.Planning;
using PhotoOk.Core.Workflow;

namespace PhotoOk.ViewModels;

/// <summary>
/// 실행 화면이 결과를 어떻게 풀어 보일지 정하는 데 쓰는 스캔·계획의 사실.
/// <paramref name="ScanErrors"/>는 스캔 때 읽지 못한 파일·폴더 수, <paramref name="IgnoredFiles"/>는 사진·동영상이 아니어서 가져오지 않은 파일 수,
/// <paramref name="SubfoldersNotImported"/>는 하위 폴더를 포함하지 않았는데 소스 폴더에 하위 폴더가 있었는지(확인하지 못했으면 true)다.
/// 아래 값은 흡수·재정리에서만 뜻이 있다(가져오기는 기본값 그대로):
/// <paramref name="Kind"/>는 작업 종류, <paramref name="AnyMoved"/>는 계획에 이동 작업이 있는지,
/// <paramref name="KeptInPlace"/>는 옮기거나 복사하지 않고 제자리에 둔 파일 수(이미 제자리·하위 폴더·다른 볼륨. 흡수는 판단 필요도 센다),
/// <paramref name="ReviewKept"/>는 재정리에서 판단 필요라 제자리에 둔 파일 수(흡수는 <paramref name="KeptInPlace"/>에 센다),
/// <paramref name="AlreadyInArchive"/>는 흡수에서 같은 내용이 이미 있어 남긴 파일 수(아카이브 어딘가·대상 자리·이번 작업 안),
/// <paramref name="DuplicateKept"/>는 재정리에서 같은 파일이 있어 그대로 둔 파일 수다.
/// </summary>
public sealed record ScanFacts(int ScanErrors, int IgnoredFiles, bool SubfoldersNotImported, JobKind Kind = JobKind.Import,
    bool AnyMoved = false, int KeptInPlace = 0, int ReviewKept = 0, int AlreadyInArchive = 0, int DuplicateKept = 0)
{
    public static ScanFacts From(ScanOutcome scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return new ScanFacts(scan.Errors.Count, scan.Ignored.Count, scan.SubfoldersNotScanned);
    }

    /// <summary>스캔의 사실에 계획에서 센 값을 더한다. 가져오기는 더하지 않는다.</summary>
    public static ScanFacts From(ScanOutcome scan, JobKind kind, Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var facts = From(scan);
        if (kind == JobKind.Import)
        {
            return facts;
        }

        int Count(params SkipReason[] reasons) => plan.Skipped.Count(skip => reasons.Contains(skip.Reason));

        var reorganize = kind == JobKind.Reorganize;
        return facts with
        {
            Kind = kind,
            AnyMoved = plan.Operations.Any(op => op.Kind == OperationKind.Move),
            // 흡수에서 제자리에 남은 파일은 복사하지 않았으므로 원본을 정리해도 안전하다고 말할 수 없다. 판단 필요도 함께 센다.
            KeptInPlace = reorganize
                ? Count(SkipReason.AlreadyInPlace, SkipReason.SubfolderKeptInPlace, SkipReason.OtherVolume)
                : Count(SkipReason.AlreadyInPlace, SkipReason.SubfolderKeptInPlace, SkipReason.OtherVolume, SkipReason.ReviewKeptInPlace),
            ReviewKept = reorganize ? Count(SkipReason.ReviewKeptInPlace) : 0,
            AlreadyInArchive = reorganize
                ? 0
                : Count(SkipReason.DuplicateInArchive, SkipReason.DuplicateAtDestination, SkipReason.DuplicateInPlan),
            DuplicateKept = reorganize ? Count(SkipReason.DuplicateAtDestination, SkipReason.DuplicateInPlan) : 0,
        };
    }
}
