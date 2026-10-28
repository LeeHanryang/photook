using PhotoOk.Core.Workflow;

namespace PhotoOk.ViewModels;

/// <summary>
/// 실행 화면이 "원본을 직접 정리해도 안전합니다"를 보일지 정하는 데 쓰는 스캔 사실.
/// <paramref name="ScanErrors"/>는 스캔 때 읽지 못한 파일·폴더 수, <paramref name="IgnoredFiles"/>는 사진·동영상이 아니어서 가져오지 않은 파일 수,
/// <paramref name="SubfoldersNotImported"/>는 하위 폴더를 포함하지 않았는데 소스 폴더에 하위 폴더가 있었는지(확인하지 못했으면 true)다.
/// </summary>
public sealed record ScanFacts(int ScanErrors, int IgnoredFiles, bool SubfoldersNotImported)
{
    public static ScanFacts From(ScanOutcome scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return new ScanFacts(scan.Errors.Count, scan.Ignored.Count, scan.SubfoldersNotScanned);
    }
}
