using System.Globalization;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Core.Workflow;

namespace PhotoOk.ViewModels;

/// <summary>카드 한 장의 날짜 안내와 날짜 버튼이 듣는 묶음(<see cref="DateCheck.NotesOf"/>).</summary>
internal sealed record DateNotes(string? FolderNote, string? PhotoNote, string? NoCaptureNote,
    IReadOnlyList<string> FolderGroups, IReadOnlyList<string> PhotoGroups)
{
    public bool HasSameContent(DateNotes other) =>
        this with { FolderGroups = other.FolderGroups, PhotoGroups = other.PhotoGroups } == other
        && FolderGroups.SequenceEqual(other.FolderGroups, StringComparer.Ordinal)
        && PhotoGroups.SequenceEqual(other.PhotoGroups, StringComparer.Ordinal);
}

/// <summary>
/// 재정리에서 옮겨질 수 있는 묶음 하나의 폴더 날짜와 사진 날짜(하루 시작 시각을 따른 촬영일)를 맞춰 본 값.
/// 촬영 시각이 없으면 수정 시각의 달력 날짜와 맞춰 보며 그것을 사진 날짜라고 부르지 않는다.
/// </summary>
internal sealed record DateCheck(string GroupId, int Files, DateOnly FolderDay, DateOnly ActualDay, string Source, bool DayStartApplied,
    bool HasCaptureTime, bool ChosePhotoDate)
{
    private static readonly string[] SourceOrder = ["사진 정보", "파일 이름"];

    public bool Differs => FolderDay != ActualDay;

    /// <summary>폴더 날짜로 정리하는데 사진 날짜가 다르다.</summary>
    public bool ToFolderDate => Differs && HasCaptureTime && !ChosePhotoDate;

    /// <summary>폴더 날짜와 다른 사진 날짜로 보냈다.</summary>
    public bool ToPhotoDate => Differs && HasCaptureTime && ChosePhotoDate;

    /// <summary>
    /// 재정리가 아니거나 하루 폴더 안에 있지 않으면 null.
    /// </summary>
    public static DateCheck? Of(ImportSettings settings, ShootDayResolver resolver, FileGroup group, TimeZoneInfo zone)
    {
        if (settings.Kind != JobKind.Reorganize
            || FolderDate.Of(group.Primary.Path, settings.ArchiveRoot) is not { } folderDay)
        {
            return null;
        }

        var primary = group.Primary;
        var calendarDay = DateOnly.FromDateTime(ShootDate.EffectiveTime(primary, zone));
        var files = group.Companions.Count + 1;
        if (primary.CaptureTime is null)
        {
            return new DateCheck(group.Id, files, folderDay, calendarDay, "", DayStartApplied: false, HasCaptureTime: false, ChosePhotoDate: false);
        }

        var photoDay = resolver.PhotoDay(group);
        return new DateCheck(group.Id, files, folderDay, photoDay, primary.CaptureSource == CaptureTimeSource.FileName ? "파일 이름" : "사진 정보",
            calendarDay != photoDay, HasCaptureTime: true, resolver.ChosePhotoDate(group));
    }

    /// <summary>
    /// 카드의 날짜 안내와 버튼이 듣는 묶음. 폴더 날짜로 정리하는 파일은 수와 사진 날짜의 범위를 보이고, 사진 날짜로 보낸 파일과 촬영 시각이 없는 파일은
    /// 폴더 날짜와 다른 날짜가 각각 한 가지면 날짜를 그대로, 아니면 수를 보인다. 촬영 시각이 없는 파일은 버튼을 받지 않는다.
    /// </summary>
    public static DateNotes NotesOf(IReadOnlyList<DateCheck> checks)
    {
        var toFolder = checks.Where(check => check.ToFolderDate).ToList();
        var toPhoto = checks.Where(check => check.ToPhotoDate).ToList();
        var noCapture = checks.Where(check => check.Differs && !check.HasCaptureTime).ToList();
        return new DateNotes(
            toFolder.Count == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"폴더 날짜로 정리 · 사진 날짜가 다른 파일 {toFolder.Sum(check => check.Files)}개 ({DayRange(toFolder)})"),
            Summary(toPhoto, "사진 날짜로 보낸 파일"),
            Summary(noCapture, "촬영 시각 없는 파일"),
            toFolder.ConvertAll(check => check.GroupId),
            toPhoto.ConvertAll(check => check.GroupId));
    }

    // 누르기 전에 시계가 틀린 폴더인지 알아볼 수 있게 사진 날짜의 범위를 보인다. 한 날이면 그 날짜만.
    private static string DayRange(IReadOnlyList<DateCheck> checks)
    {
        var first = checks.Min(check => check.ActualDay);
        var last = checks.Max(check => check.ActualDay);
        return first == last
            ? first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{first:yyyy-MM-dd} ~ {last:yyyy-MM-dd}");
    }

    private static string? Summary(IReadOnlyList<DateCheck> checks, string countLabel)
    {
        if (checks.Count == 0)
        {
            return null;
        }

        return checks.Select(check => check.FolderDay).Distinct().Count() == 1 && checks.Select(check => check.ActualDay).Distinct().Count() == 1
            ? Line(checks)
            : string.Create(CultureInfo.InvariantCulture, $"{countLabel} {checks.Sum(check => check.Files)}개");
    }

    /// <summary>상세 칸의 문구. 폴더 날짜로 정리하면 그렇다고 알리고, 그 밖에는 카드의 한 줄 문구와 같다.</summary>
    public string DetailLine() => ToFolderDate
        ? string.Create(CultureInfo.InvariantCulture, $"폴더 날짜로 정리 · 사진 날짜 {ActualDay:yyyy-MM-dd} ({SourcesText([this])})")
        : Line([this]);

    /// <summary>
    /// 예: "폴더 날짜 2024-06-24 → 사진 날짜 2024-06-23 (사진 정보)", "폴더 날짜 2024-06-24 · 촬영 시각 없음 (수정 시각 2024-07-01)".
    /// 하루 시작 시각 보정은 출처에서 드러낸다. 촬영 시각의 있고 없음이 같은 값들만 받는다.
    /// </summary>
    private static string Line(IReadOnlyList<DateCheck> checks)
    {
        var first = checks[0];
        if (!first.HasCaptureTime)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"폴더 날짜 {first.FolderDay:yyyy-MM-dd} · 촬영 시각 없음 (수정 시각 {first.ActualDay:yyyy-MM-dd})");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"폴더 날짜 {first.FolderDay:yyyy-MM-dd} → 사진 날짜 {first.ActualDay:yyyy-MM-dd} ({SourcesText(checks)})");
    }

    private static string SourcesText(IReadOnlyList<DateCheck> checks)
    {
        var sources = SourceOrder.Where(source => checks.Any(check => check.Source == source)).ToList();
        if (checks.Any(check => check.DayStartApplied))
        {
            sources.Add("하루 시작 시각 보정");
        }

        return string.Join(", ", sources);
    }
}
