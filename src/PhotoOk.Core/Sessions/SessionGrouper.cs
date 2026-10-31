using System.Globalization;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Sessions;

public static class SessionGrouper
{
    /// <summary>
    /// 파일 그룹을 촬영일별로 묶는다. 그룹의 시각은 대표 파일의 <see cref="ShootDate.EffectiveTime"/>이다.
    /// <paramref name="eventNameOf"/>가 있으면 같은 날이라도 이름(앞뒤 공백과 대소문자 무시)이 다르면 다른 세션으로 나눈다.
    /// 세션은 시작일 오름차순(같은 날은 이름 없는 쪽이 먼저, 그다음 이름 순), 세션 안의 그룹은 시각 오름차순(같으면 대표 경로 순)으로 돌려준다.
    /// <paramref name="isFormatName"/>은 그룹의 이름을 프리셋 형식에서 읽었는지 알려 준다. 세션 안에 하나라도 그러면 세션의 이름을 형식에서 읽은 것으로 본다.
    /// </summary>
    public static IReadOnlyList<Session> GroupByDay(IEnumerable<FileGroup> groups, TimeOnly dayStart, TimeZoneInfo zone,
        Func<FileGroup, string?>? eventNameOf = null, Func<FileGroup, bool>? isFormatName = null)
    {
        return groups
            .Select(g =>
            {
                var time = ShootDate.EffectiveTime(g.Primary, zone);
                var name = eventNameOf?.Invoke(g)?.Trim();
                return (Group: g, Time: time, Day: ShootDate.ShootDay(time, dayStart),
                    Name: string.IsNullOrEmpty(name) ? null : name);
            })
            .GroupBy(x => (x.Day, Key: x.Name?.ToUpperInvariant() ?? string.Empty))
            .OrderBy(s => s.Key.Day)
            .ThenBy(s => s.Key.Key.Length > 0)
            .ThenBy(s => s.Key.Key, StringComparer.Ordinal)
            .Select(s =>
            {
                var ordered = s.OrderBy(x => x.Time)
                    .ThenBy(x => x.Group.Primary.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var name = ordered[0].Name;
                var day = s.Key.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return new Session(name is null ? day : day + "|" + name, s.Key.Day, s.Key.Day,
                    ordered.Select(x => x.Group).ToList(), name,
                    name is not null && isFormatName is not null && ordered.Any(x => isFormatName(x.Group)));
            })
            .ToList();
    }
}
