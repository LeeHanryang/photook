using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoOk.Core.Metadata;

/// <summary>
/// 파일 이름에 든 날짜·시각을 촬영일로 읽는다. EXIF·QuickTime에서 촬영일을 얻지 못했을 때만 쓴다.
/// 읽는 형식은 앞에서부터 처음 맞는 것을 쓴다. 날짜는 1990-01-01 이상, 오늘 다음 날 0시 미만이어야 한다.
/// </summary>
public static partial class FileNameDate
{
    private static readonly DateTime Earliest = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    // YYYY-MM-DD_HH-MM-SS, 앞뒤에 숫자가 붙지 않아야 한다.
    [GeneratedRegex(@"(?<!\d)(\d{4})-(\d{2})-(\d{2})_(\d{2})-(\d{2})-(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Dashed();

    // yyyyMMdd_HHmmss, 앞에는 숫자가 없어야 하고 뒤에는 밀리초 숫자가 붙어도 된다.
    [GeneratedRegex(@"(?<!\d)(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex Compact();

    // 날짜만 있는 형식. 시각은 00:00으로 본다.
    [GeneratedRegex(@"KakaoTalk_(\d{4})(\d{2})(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyKakao();

    // yyyy.MM.dd, 앞뒤에 숫자가 붙지 않아야 한다. 시각은 00:00으로 본다.
    [GeneratedRegex(@"(?<!\d)(\d{4})\.(\d{2})\.(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedDate();

    // yyyyMMdd, 앞뒤에 숫자가 붙지 않아야 한다(13자리 밀리초 시각 같은 긴 숫자는 날짜로 보지 않는다).
    // 뒤에 _HHmmss 모양이 이어지면 시각이 있는 이름인데 시각이 올바르지 않은 것이므로 날짜만 읽지 않는다. 시각은 00:00으로 본다.
    [GeneratedRegex(@"(?<!\d)(\d{4})(\d{2})(\d{2})(?!\d|_\d{6})", RegexOptions.CultureInvariant)]
    private static partial Regex CompactDate();

    /// <summary>파일 이름에서 읽은 현지 벽시계 시각(<see cref="DateTimeKind.Unspecified"/>). 없거나 올바르지 않으면 null.</summary>
    public static DateTime? TryParse(string fileName, DateTime todayLocal)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var limit = todayLocal.Date.AddDays(1);
        // 형식마다 처음 맞는 곳 하나만 본다. 그것이 달력에 없는 날짜면 같은 형식의 뒤쪽 일치가 아니라 다음 형식으로 넘어간다.
        foreach (var regex in new[] { Dashed(), Compact(), DateOnlyKakao(), DottedDate(), CompactDate() })
        {
            var match = regex.Match(fileName);
            if (match.Success && ToDateTime(match, limit) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static DateTime? ToDateTime(Match match, DateTime limit)
    {
        var groups = match.Groups;
        var year = Number(groups, 1);
        var month = Number(groups, 2);
        var day = Number(groups, 3);
        var hour = groups.Count > 4 ? Number(groups, 4) : 0;
        var minute = groups.Count > 5 ? Number(groups, 5) : 0;
        var second = groups.Count > 6 ? Number(groups, 6) : 0;

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return null;
        }

        var value = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return value >= Earliest && value < limit ? value : null;
    }

    private static int Number(GroupCollection groups, int index) =>
        int.Parse(groups[index].Value, NumberStyles.None, CultureInfo.InvariantCulture);
}
