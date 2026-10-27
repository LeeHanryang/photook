namespace PhotoOk.Core.Planning;

/// <summary>사람이 판단해야 하는 이유. 하나라도 있으면 "판단 필요" 분기로 간다.</summary>
public enum ReviewReason { NoCaptureDate, UnknownDevice, OrphanSidecar }

public static class ReviewReasonText
{
    public static string Label(ReviewReason reason) => reason switch
    {
        ReviewReason.NoCaptureDate => "촬영일 없음",
        ReviewReason.UnknownDevice => "기기 모름",
        ReviewReason.OrphanSidecar => "짝 없는 사이드카",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
