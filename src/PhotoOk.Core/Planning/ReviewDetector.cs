using PhotoOk.Core.Layout;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Planning;

/// <summary>파일 그룹에 사람이 판단해야 할 이유가 있는지 찾는다.</summary>
public static class ReviewDetector
{
    private static readonly HashSet<string> DeviceTokens = new(StringComparer.Ordinal) { "기기", "제조사", "모델" };

    /// <summary>사유를 enum 값 순서로, 중복 없이 돌려준다.</summary>
    public static IReadOnlyList<ReviewReason> Detect(FileGroup group, Preset preset, DeviceAliasResolver aliases)
    {
        var primary = group.Primary;
        var reasons = new List<ReviewReason>();

        if (primary.CaptureTime is null && primary.Kind != MediaKind.Sidecar)
        {
            reasons.Add(ReviewReason.NoCaptureDate);
        }

        if (primary.Kind is MediaKind.Image or MediaKind.Raw or MediaKind.Video
            && aliases.Resolve(primary) is null
            && DefaultBranchUsesDevice(preset))
        {
            reasons.Add(ReviewReason.UnknownDevice);
        }

        if (primary.Kind == MediaKind.Sidecar)
        {
            reasons.Add(ReviewReason.OrphanSidecar);
        }

        return reasons;
    }

    /// <summary>기본 분기(마지막 분기)의 단계 중 하나라도 기기·제조사·모델 토큰을 쓰는가.</summary>
    private static bool DefaultBranchUsesDevice(Preset preset)
    {
        if (preset.Branches.Count == 0)
        {
            return false;
        }

        return preset.Branches[^1].Levels
            .Any(level => PathTemplate.Parse(level.Template).Tokens.Any(DeviceTokens.Contains));
    }
}
