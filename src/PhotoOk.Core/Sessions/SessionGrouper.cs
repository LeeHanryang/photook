using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Sessions;

public static class SessionGrouper
{
    /// <summary>
    /// 파일 그룹을 촬영일별로 묶는다. 그룹의 시각은 대표 파일의 <see cref="ShootDate.EffectiveTime"/>이다.
    /// 세션은 시작일 오름차순, 세션 안의 그룹은 시각 오름차순(같으면 대표 경로 순)으로 돌려준다.
    /// </summary>
    public static IReadOnlyList<Session> GroupByDay(IEnumerable<FileGroup> groups, TimeOnly dayStart, TimeZoneInfo zone)
    {
        return groups
            .Select(g => (Group: g, Time: ShootDate.EffectiveTime(g.Primary, zone)))
            .GroupBy(x => ShootDate.ShootDay(x.Time, dayStart))
            .OrderBy(day => day.Key)
            .Select(day => new Session(
                day.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                day.Key,
                day.Key,
                day.OrderBy(x => x.Time)
                    .ThenBy(x => x.Group.Primary.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Group)
                    .ToList()))
            .ToList();
    }
}
