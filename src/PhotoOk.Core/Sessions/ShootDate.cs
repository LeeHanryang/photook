using PhotoOk.Core.Metadata;

namespace PhotoOk.Core.Sessions;

public static class ShootDate
{
    /// <summary>
    /// 촬영 시각(현지 벽시계, <see cref="DateTimeKind.Unspecified"/>). 촬영 시각 메타데이터가 있으면 그대로,
    /// 없으면 수정 시각(UTC)을 <paramref name="zone"/> 현지 시각으로 바꿔 쓴다.
    /// </summary>
    public static DateTime EffectiveTime(PhotoInfo file, TimeZoneInfo zone)
    {
        if (file.CaptureTime is { } capture)
        {
            return DateTime.SpecifyKind(capture, DateTimeKind.Unspecified);
        }

        var utc = DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified);
    }

    /// <summary>촬영일. 시각이 <paramref name="dayStart"/> 이전이면 전날로 친다.</summary>
    public static DateOnly ShootDay(DateTime localTime, TimeOnly dayStart)
    {
        var date = DateOnly.FromDateTime(localTime);
        if (TimeOnly.FromDateTime(localTime) < dayStart && date > DateOnly.MinValue)
        {
            return date.AddDays(-1);
        }

        return date;
    }
}
