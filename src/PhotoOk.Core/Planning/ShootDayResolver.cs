using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Core.Planning;

/// <summary>
/// 묶음이 정리될 날짜를 한 곳에서 정한다. 가져오기와 흡수는 언제나 사진 날짜다.
/// 재정리는 대표 파일의 촬영 시각이 있고 아카이브 안에 하루 폴더가 있으면 그 폴더 날짜를 따르되,
/// <paramref name="photoDateGroups"/>(<see cref="FileGroup.Id"/>, 대소문자 무시)에 든 묶음은 사진 날짜를 쓴다.
/// </summary>
public sealed class ShootDayResolver(TimeOnly dayStart, TimeZoneInfo zone, JobKind kind, string archiveRoot,
    IReadOnlySet<string>? photoDateGroups = null)
{
    // 받은 집합의 비교 방식과 상관없이 대소문자를 무시하려고 복사해 둔다.
    private readonly HashSet<string> _photoDateGroups = photoDateGroups is null
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(photoDateGroups, StringComparer.OrdinalIgnoreCase);

    /// <summary>사진 날짜: 대표 파일의 유효 시각(<see cref="ShootDate.EffectiveTime"/>)에 하루 시작 시각을 적용한 날.</summary>
    public DateOnly PhotoDay(FileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return ShootDate.ShootDay(ShootDate.EffectiveTime(group.Primary, zone), dayStart);
    }

    /// <summary>폴더 날짜: 재정리이고 대표 파일에 촬영 시각이 있으며 가장 가까운 하루 폴더가 있을 때 그 날짜. 그 밖에는 null.</summary>
    public DateOnly? FolderDay(FileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return kind == JobKind.Reorganize && group.Primary.CaptureTime is not null
            ? FolderDate.Of(group.Primary.Path, archiveRoot)
            : null;
    }

    /// <summary>사용자가 사진 날짜를 고른 묶음인가.</summary>
    public bool ChosePhotoDate(FileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _photoDateGroups.Contains(group.Id);
    }

    /// <summary>묶음이 정리될 날짜. 사진 날짜를 골랐으면 사진 날짜, 아니면 폴더 날짜, 폴더 날짜가 없으면 사진 날짜.</summary>
    public DateOnly Resolve(FileGroup group) => ChosePhotoDate(group) ? PhotoDay(group) : FolderDay(group) ?? PhotoDay(group);
}
