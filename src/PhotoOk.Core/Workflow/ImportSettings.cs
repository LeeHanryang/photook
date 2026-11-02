using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;

namespace PhotoOk.Core.Workflow;

/// <summary>
/// 가져오기 화면에서 사용자가 고른 값. 미리보기에서 옵션을 바꾸면 고쳐 쓴 새 값이 된다.
/// <paramref name="PresetFile"/>은 <paramref name="Preset"/>을 읽은 파일이다(내장 프리셋은 null). 같은 이름의 사용자 프리셋을 가려 낸다.
/// <paramref name="SplitMultiDayFolders"/>는 재정리 미리보기에서 그때그때 고르는 값이며 저장하지 않는다(실행마다 다시 고른다).
/// <paramref name="PhotoDateGroups"/>는 재정리 미리보기에서 사진 날짜로 보낸 묶음의 <see cref="Scanning.FileGroup.Id"/>다. 저장하지 않고, 앞 화면으로 돌아가면 사라진다.
/// 비었으면 늘 null로 두고 고칠 때는 새 집합을 만든다(레코드 비교는 집합의 참조만 보므로 "바뀌지 않음"을 그것으로 알아챈다).
/// </summary>
public sealed record ImportSettings(
    IReadOnlyList<string> Sources,
    bool IncludeSubfolders,
    string ArchiveRoot,
    Preset Preset,
    TransferMode Mode,
    JobKind Kind = JobKind.Import,
    bool UseFolderNames = false,
    bool SplitUnderscores = false,
    SubfolderPolicy Subfolders = SubfolderPolicy.Keep,
    bool KeepReviewInPlace = true,
    string? PresetFile = null,
    bool SplitMultiDayFolders = false,
    IReadOnlySet<string>? PhotoDateGroups = null)
{
    /// <summary>이 값으로 묶음의 정리 날짜를 정하는 것.</summary>
    public ShootDayResolver DayResolver(TimeZoneInfo zone) => new(Preset.DayStart, zone, Kind, ArchiveRoot, PhotoDateGroups);
}
