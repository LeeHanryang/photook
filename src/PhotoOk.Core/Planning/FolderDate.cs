using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoOk.Core.Planning;

/// <summary>
/// 폴더 이름에 적힌 날짜를 읽는다. 경로 글자만 보며 디스크는 건드리지 않는다. 아카이브 루트와 그 위의 폴더는 보지 않는다.
/// 하루 폴더는 <c>yyyy-MM-dd</c>만 읽는다(<c>dd-mm</c>과 혼동을 피하려고). 여러 날 폴더는 시작과 끝을 이은 이름이다.
/// </summary>
public static partial class FolderDate
{
    // [2024-06-24] 일상, 2024-06-24, 2024-06-24 일상. 뒤에 숫자나 기간 표시가 이어지면 하루 날짜가 아니다.
    [GeneratedRegex(@"^(?:\[\s*(?<day>\d{4}-\d{2}-\d{2})\s*\]|(?<day>\d{4}-\d{2}-\d{2})(?![\d.\-])(?!\s*[~～–]))", RegexOptions.CultureInvariant)]
    private static partial Regex OneDay();

    [GeneratedRegex(@"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex AnyDay();

    // [2026-07-08~07-09] 소풍, [2026-07-08~2026-07-09], 2026-07-08~07-09 소풍. 끝은 숫자와 구분 표시뿐이고 그 뒤는 닫는 대괄호, 빈칸, 이름의 끝이다
    // (2024-06-15 ~ 16시 모임은 기간이 아니다).
    [GeneratedRegex(@"^\[?\s*\d{4}[-.]\d{1,2}[-.]\d{1,2}\s*[~～–]\s*\d[\d\-.]*(?:\s*\]|\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MultiDay();

    /// <summary>폴더 이름 하나가 하루 폴더면 그 날짜. 날짜가 앞에 하나만 있어야 하고(뒤에 이름은 있어도 된다), 기간은 하루 폴더가 아니다.</summary>
    public static DateOnly? DayOfSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (IsMultiDaySegment(segment) || OneDay().Match(segment) is not { Success: true } match || AnyDay().Count(segment) != 1)
        {
            return null;
        }

        return DateOnly.TryParseExact(match.Groups["day"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    /// <summary>파일에서 가장 가까운 하루 폴더의 날짜. 아카이브 안에 그런 폴더가 없으면 null.</summary>
    public static DateOnly? Of(string filePath, string archiveRoot)
    {
        foreach (var folder in FoldersBelowRoot(filePath, archiveRoot))
        {
            if (DayOfSegment(Path.GetFileName(folder)) is { } day)
            {
                return day;
            }
        }

        return null;
    }

    /// <summary>폴더 이름 하나가 <c>[시작~종료]</c> 또는 <c>시작~종료</c>인가.</summary>
    public static bool IsMultiDaySegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return MultiDay().IsMatch(segment);
    }

    /// <summary>파일이 든 여러 날 폴더들(가까운 것부터). 고른 폴더와 상관없이 아카이브 루트 아래를 모두 본다.</summary>
    public static IReadOnlyList<string> MultiDayFolders(string filePath, string archiveRoot) =>
        FoldersBelowRoot(filePath, archiveRoot).Where(folder => IsMultiDaySegment(Path.GetFileName(folder))).ToList();

    private static IEnumerable<string> FoldersBelowRoot(string filePath, string archiveRoot)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(archiveRoot);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(archiveRoot));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
             folder is not null && folder.Length > prefix.Length && folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
             folder = Path.GetDirectoryName(folder))
        {
            yield return folder;
        }
    }
}
