using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;

namespace PhotoOk.Core.Workflow;

/// <summary>
/// 가져오기 화면에서 사용자가 고른 값. 미리보기에서 옵션을 바꾸면 고쳐 쓴 새 값이 된다.
/// <paramref name="PresetFile"/>은 <paramref name="Preset"/>을 읽은 파일이다(내장 프리셋은 null). 같은 이름의 사용자 프리셋을 가려 낸다.
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
    string? PresetFile = null);
