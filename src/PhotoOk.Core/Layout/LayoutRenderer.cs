using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Core.Layout;

/// <param name="OmitOriginalFolder">원래 폴더를 유지할 폴더가 따로 나타내므로 <c>{원래폴더}</c>를 빈 값으로 본다.</param>
public sealed record LayoutInput(Session Session, FileGroup Group, IReadOnlyList<ReviewReason> Reasons, TimeZoneInfo Zone,
    string? EventName = null, bool OmitOriginalFolder = false);

/// <summary>프리셋의 폴더 템플릿을 파일 그룹마다 실제 상대 폴더로 바꾼다.</summary>
public sealed class LayoutRenderer(Preset preset)
{
    private readonly DeviceAliasResolver _devices = new(preset.DeviceAliases);
    private readonly ConcurrentDictionary<string, PathTemplate> _templates = new(StringComparer.Ordinal);

    /// <summary>위에서부터 <see cref="BranchCondition.Always"/>이거나 (<see cref="BranchCondition.NeedsReview"/>이고 사유가 있는) 첫 분기.</summary>
    public LayoutBranch SelectBranch(IReadOnlyList<ReviewReason> reasons)
    {
        foreach (var branch in preset.Branches)
        {
            if (branch.When == BranchCondition.Always
                || (branch.When == BranchCondition.NeedsReview && reasons.Count > 0))
            {
                return branch;
            }
        }

        throw new InvalidOperationException("조건에 맞는 분기가 없습니다");
    }

    /// <summary>아카이브 루트 기준 상대 폴더. 구분자는 <c>\</c>.</summary>
    public string RenderDirectory(LayoutInput input)
    {
        var levels = new List<string>();
        foreach (var level in SelectBranch(input.Reasons).Levels)
        {
            var template = _templates.GetOrAdd(level.Template, PathTemplate.Parse);
            var rendered = RenderLevel(template, input);
            if (rendered is null)
            {
                if (level.WhenEmpty == EmptyValue.Replace && !string.IsNullOrEmpty(level.Replacement))
                {
                    levels.Add(PathSanitizer.SanitizeSegment(level.Replacement));
                }

                continue;
            }

            levels.AddRange(rendered.Select(PathSanitizer.SanitizeSegment));
        }

        return string.Join('\\', levels);
    }

    /// <summary>
    /// 단계 하나가 만드는 폴더 조각들. 단계의 모든 토큰이 빈 값이면 null. 토큰이 하나도 없는 단계는 항상 글자 그대로.
    /// 이벤트 이름의 <c>\</c>는 폴더를 나눈다: 첫 조각은 그 앞의 글자와 한 폴더에 남고, 나머지는 바로 아래 폴더가 되며,
    /// 이벤트 뒤의 글자는 마지막 조각에 붙는다. 빈 조각과 <c>.</c>·<c>..</c>는 버린다.
    /// </summary>
    private List<string>? RenderLevel(PathTemplate template, LayoutInput input)
    {
        var pieces = new List<string>();
        var text = new StringBuilder();
        var hasToken = false;
        var anyValue = false;
        foreach (var part in template.Parts)
        {
            if (part.Token is null)
            {
                text.Append(part.Literal);
                continue;
            }

            hasToken = true;
            var value = TokenValue(part, input);
            if (part.Token == "이벤트")
            {
                var eventPieces = EventPieces(value);
                for (var i = 0; i < eventPieces.Count; i++)
                {
                    if (i > 0)
                    {
                        pieces.Add(text.ToString());
                        text.Clear();
                    }

                    anyValue = true;
                    text.Append(eventPieces[i]);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(value))
            {
                anyValue = true;
                text.Append(value);
            }
        }

        if (hasToken && !anyValue)
        {
            return null;
        }

        pieces.Add(text.ToString());
        return pieces;
    }

    // 이벤트 이름을 \로 나눈 조각 중 비었거나 점뿐인 조각(., .., ...)은 버린다. 아카이브 밖으로 나가는 경로를 만들 수 없다.
    private static List<string> EventPieces(string eventName)
    {
        var pieces = new List<string>();
        foreach (var raw in eventName.Split('\\'))
        {
            var piece = raw.Trim();
            if (piece.Length > 0 && piece.Any(c => c != '.'))
            {
                pieces.Add(piece);
            }
        }

        return pieces;
    }

    private string TokenValue(TemplatePart part, LayoutInput input)
    {
        var primary = input.Group.Primary;
        var culture = CultureInfo.InvariantCulture;
        switch (part.Token)
        {
            case "연도": return ShootDay(input).ToString(part.Format ?? "yyyy", culture);
            case "월": return ShootDay(input).ToString(part.Format ?? "MM", culture);
            case "일": return ShootDay(input).ToString(part.Format ?? "dd", culture);
            case "날짜": return ShootDay(input).ToString(part.Format ?? "yyyy-MM-dd", culture);
            case "기간": return PeriodFormatter.Format(input.Session.Start, input.Session.End);
            case "기기": return _devices.Resolve(primary) ?? "";
            case "제조사": return primary.CameraMake?.Trim() ?? "";
            case "모델": return primary.CameraModel?.Trim() ?? "";
            case "포맷": return FormatGroupName(primary);
            case "확장자": return Path.GetExtension(primary.Path).TrimStart('.').ToUpperInvariant();
            case "원래폴더": return input.OmitOriginalFolder ? "" : Path.GetFileName(Path.GetDirectoryName(primary.Path)) ?? "";
            case "사유": return string.Join('·', input.Reasons.Select(ReviewReasonText.Label));
            case "이벤트": return input.EventName?.Trim() ?? "";
            default: throw new InvalidOperationException($"알 수 없는 토큰: {{{part.Token}}}");
        }
    }

    private DateOnly ShootDay(LayoutInput input) =>
        ShootDate.ShootDay(ShootDate.EffectiveTime(input.Group.Primary, input.Zone), preset.DayStart);

    private string FormatGroupName(PhotoInfo primary)
    {
        var extension = Path.GetExtension(primary.Path);
        foreach (var (name, extensions) in preset.FormatGroups)
        {
            if (extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }

        return "";
    }
}
