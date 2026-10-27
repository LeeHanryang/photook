using PhotoOk.Core.Metadata;

namespace PhotoOk.Core.Scanning;

/// <summary>
/// 사진 한 장과 거기에 딸린 파일(RAW 편집 사이드카, 라이브 포토 영상). 항상 함께 옮긴다.
/// </summary>
public sealed record FileGroup(PhotoInfo Primary, IReadOnlyList<PhotoInfo> Companions)
{
    public string Id => Primary.Path;

    public IEnumerable<PhotoInfo> AllFiles => [Primary, .. Companions];

    /// <summary>그룹 키 = 대표 파일 이름에서 확장자를 뺀 것. 대표가 사이드카이면 짝 확장자(IMG.RW2.xmp의 .RW2)도 뺀다.</summary>
    public string Key { get; } = ComputeKey(Primary);

    private static string ComputeKey(PhotoInfo primary)
    {
        var name = Path.GetFileName(primary.Path);
        return primary.Kind == MediaKind.Sidecar
            ? Scanner.SplitSidecarName(name, FileKinds.Default).Key
            : Path.GetFileNameWithoutExtension(name);
    }
}
