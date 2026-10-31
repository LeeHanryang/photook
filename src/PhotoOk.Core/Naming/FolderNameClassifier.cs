using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoOk.Core.Naming;

/// <param name="Name">날짜를 떼고 남은, 이벤트 이름으로 쓸 글자.</param>
/// <param name="HadDate">이름에 날짜가 붙어 있었는가. 사람이 날짜와 함께 일부러 지은 이름이라 가장 강한 신호다.</param>
public sealed record FolderNameCandidate(string Name, bool HadDate);

/// <summary>폴더 이름의 종류. 이름으로 쓸 수 있는 폴더, 이름으로는 못 쓰지만 유지할 폴더, 없어져도 되는 폴더.</summary>
public enum FolderKind { EventCandidate, Meaningful, Disposable }

/// <param name="Devices">이번 스캔의 사진에서 모은 기기 이름.</param>
/// <param name="Ignored">사용자가 다시 쓰지 않기로 한 폴더 이름.</param>
public sealed record FolderNameContext(DeviceVocabulary Devices, IgnoredFolderNames Ignored);

/// <summary>
/// 폴더 이름이 사람이 고른 이벤트 이름인지 판별한다. 디스크를 건드리지 않는 순수 함수이며 예외를 던지지 않는다.
/// 폴더 하나는 앞에서부터 날짜 떼기, 카메라 자동 폴더, 무의미한 모양, 일반 단어, 파일 형식, 기기 이름, 사용자 제외 단어 순으로
/// 검사하고 하나라도 걸리면 후보가 아니다. 날짜는 해석하지 않고 지우기만 한다(촬영일은 EXIF에서 얻는다).
/// </summary>
public static partial class FolderNameClassifier
{
    // ------------------------------------------------------------------
    // 날짜 정규식. 모두 숫자 경계를 지켜 긴 숫자 안에서 날짜를 찾지 않는다.
    // ------------------------------------------------------------------

    private const string Year = @"(?:19|20)\d{2}";
    private const string Month = @"(?:0?[1-9]|1[0-2])";
    private const string Day = @"(?:0?[1-9]|[12]\d|3[01])";
    private const string Sep = @"[-._]";
    private const string RangeMark = @"\s*[~～–]\s*";
    private const string TimeSuffix = @"(?:[\sT_]+(?:[01]\d|2[0-3])[-:.][0-5]\d(?:[-:.][0-5]\d)?(?!\d))?";

    private const string EnglishMonth =
        "(?:January|February|March|April|May|June|July|August|September|October|November|December"
        + "|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sept|Sep|Oct|Nov|Dec)";

    // 2025-03-08~03-10, 2025-03-08~2025-03-10
    [GeneratedRegex(@"(?<!\d)" + Year + Sep + Month + Sep + Day + RangeMark + "(?:" + Year + Sep + ")?" + Month + "(?:" + Sep + Day + ")?(?!\\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DateRange();

    // 2025-03-09, 2025.03.09, 2025_03_09, 뒤에 시각이 붙어도 된다(2025-03-09 14-30-00).
    [GeneratedRegex(@"(?<!\d)" + Year + Sep + Month + Sep + Day + "(?!\\d)" + TimeSuffix, RegexOptions.CultureInvariant)]
    private static partial Regex YearMonthDay();

    // 20250309
    [GeneratedRegex(@"(?<!\d)" + Year + @"(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CompactDate();

    // 250309
    [GeneratedRegex(@"(?<!\d)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SixDigitDate();

    // 09-03-2025, 03-09-2025. 일과 월 순서는 구별하지 않는다.
    [GeneratedRegex(@"(?<!\d)" + Day + Sep + Day + Sep + Year + "(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthYear();

    // 25-03-09, 03_09_25, 09.03.25. 세 수 중 날짜로 읽히는 조합이 있을 때만 날짜로 본다.
    [GeneratedRegex(@"(?<!\d)(\d{2})" + Sep + @"(\d{2})" + Sep + @"(\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex TwoDigitYearDate();

    // 2025-03, 2025.03, 202503
    [GeneratedRegex(@"(?<!\d)" + Year + "(?:" + Sep + Month + @"|(?:0[1-9]|1[0-2]))(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex YearMonth();

    // 2025년 3월 9일, 2025년 3월, 2025년, 3월 9일, 3월, 9일. 1박2일·2박 3일의 "3일", "1일차", "10일간"은 날짜가 아니다.
    [GeneratedRegex(
        @"(?<!\d)(?:" + Year + @"\s*년(?:\s*" + Month + @"\s*월(?:\s*" + Day + @"\s*일)?)?"
        + "|" + Month + @"\s*월(?:\s*" + Day + @"\s*일)?"
        + @"|(?<!박\s*)" + Day + @"\s*일(?!차|간))",
        RegexOptions.CultureInvariant)]
    private static partial Regex KoreanDate();

    // May 2023, 02 Feb, June, Mar 5, 2023
    [GeneratedRegex(
        @"(?<!\p{L})(?:(?<!\d)\d{1,2}(?:st|nd|rd|th)?\s+)?" + EnglishMonth + @"\.?(?![\p{L}'’])"
        + @"(?:\s+\d{1,2}(?:st|nd|rd|th)?(?![\p{L}\d]))?(?:,?\s+" + Year + @"(?!\d))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EnglishDate();

    // 앞의 숫자가 4자리 이상이면 그 숫자와 구분 기호도 지운다. 3자리 이하는 두어 1박2일을 살린다. 2000D처럼 영문이 붙은 것은 두어 둔다.
    [GeneratedRegex(@"^\d{4,}(?![A-Za-z\d])[\s._\-·]*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"[\[\(\{（［]\s*[\]\)\}）］]", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"([\[\(\{（［])\s+|(?<!\s)\s+([\]\)\}）］])", RegexOptions.CultureInvariant)]
    private static partial Regex BracketPadding();

    // 이름 앞의 번호 접두(01_, 2., 3 ). 숫자 뒤에 구분 기호가 없으면 떼지 않는다(1박2일).
    [GeneratedRegex(@"^\d{1,3}[_.\s-]+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPrefix();

    // 1.8.0, 2.5
    [GeneratedRegex(@"(?<!\d)\d+\.\d+(?:\.\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    // ------------------------------------------------------------------
    // 그 밖의 정규식
    // ------------------------------------------------------------------

    // 카메라 자동 폴더: DCF 규격(100MSDCF, 101_PANA, 100APPLE). DCIM·MISC는 단어 표에서 거른다.
    [GeneratedRegex(@"^\d{3}[A-Za-z0-9_]{5}$", RegexOptions.CultureInvariant)]
    private static partial Regex CameraAutoFolder();

    [GeneratedRegex(@"^DCIM$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DcimFolder();

    // 숫자·기호뿐인 이름
    [GeneratedRegex(@"^[\d\p{P}\p{S}\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsAndSymbolsOnly();

    [GeneratedRegex(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Guid();

    [GeneratedRegex(@"^[0-9a-f]{8,}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HexOnly();

    // 새 폴더, 새 폴더 (2), New folder, Untitled, 제목 없음, folder1
    [GeneratedRegex(@"^(?:새\s*폴더|new\s*folder|untitled(?:\s*folder)?|제목\s*없음|folder)\s*(?:\(\d+\)|\d+)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderName();

    // 데이트 사본, Copy of 여행, 여행 - Copy
    [GeneratedRegex(@"^(?:.+\s(?:사본|복사본)(?:\s*\(\d+\))?|Copy of .+|.+ - Copy(?:\s*\(\d+\))?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CopyName();

    // Google Takeout의 연도 폴더. 숫자가 들어 있어 단어 표 대신 정규식으로 본다.
    [GeneratedRegex(@"^(?:photos\s+from\s+\d{4}|\d{4}\s*의\s*사진)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TakeoutYearFolder();

    // 모델 이름 모양: S9, R6, A7M4, X100VI, GR3, Z-8. 스펙의 식(끝에 영문 0~4자)으로는 A7M4가 걸리지 않아, 영문 뒤에 숫자 0~2자를 더 허용한다.
    [GeneratedRegex(@"^[A-Za-z]{1,4}[- ]?\d{1,4}(?:[A-Za-z]{1,4}\d{0,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelShape();

    // ------------------------------------------------------------------
    // 공개 함수
    // ------------------------------------------------------------------

    /// <summary>폴더 이름 하나를 검사한다. 이벤트 이름이 아니면 null. 예외를 던지지 않는다.</summary>
    public static FolderNameCandidate? Classify(string folderName, FolderNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Analyze(folderName, context, out var candidate);
        return candidate;
    }

    /// <summary>
    /// 폴더 이름의 종류. <see cref="Classify"/>가 후보를 돌려주면 이벤트 후보, 뜻 있는 폴더 표에 있거나 사용자가 제외한 이름이면 뜻 있는 폴더,
    /// 그 밖은 없어져도 되는 폴더다. 예외를 던지지 않는다.
    /// </summary>
    public static FolderKind KindOf(string folderName, FolderNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Analyze(folderName, context, out _);
    }

    /// <summary><see cref="KindOf"/>와 <see cref="Classify"/>를 한 번에 구한다. 같은 이름을 되풀이해 판별하는 쪽이 결과를 기억해 두려고 쓴다.</summary>
    internal static (FolderKind Kind, FolderNameCandidate? Candidate) Judge(string folderName, FolderNameContext context)
    {
        var kind = Analyze(folderName, context, out var candidate);
        return (kind, candidate);
    }

    private static FolderKind Analyze(string folderName, FolderNameContext context, out FolderNameCandidate? candidate)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return FolderKind.Disposable;
        }

        // 숨김 모양: 원래 이름이 _ 또는 .로 시작한다.
        if (folderName.TrimStart()[0] is '_' or '.')
        {
            return FolderKind.Disposable;
        }

        var raw = TrimSeparators(folderName);
        if (raw.Length == 0 || TakeoutYearFolder().IsMatch(raw) || Guid().IsMatch(raw))
        {
            return FolderKind.Disposable;
        }

        // 1. 날짜 떼기
        var (stripped, hadDate) = StripDate(raw);
        var name = TidyAfterStrip(stripped);
        if (name.Length == 0)
        {
            return FolderKind.Disposable;
        }

        // 2. 카메라 자동 폴더 (번호 접두를 떼기 전에 본다: 101_PANA)
        if (IsCameraAutoFolder(name))
        {
            return FolderKind.Disposable;
        }

        // 번호 접두 떼기(01_촬영 -> 촬영). 날짜가 아니므로 HadDate로 치지 않는다.
        name = TrimSeparators(NumberPrefix().Replace(name, string.Empty));
        if (name.Length == 0)
        {
            return FolderKind.Disposable;
        }

        // 3. 무의미한 모양과 프로그램 폴더
        if (IsMeaninglessShape(name) || IsProgramFolder(name))
        {
            return FolderKind.Disposable;
        }

        // 4~5. 일반 단어와 파일 형식 이름. 일반 단어 중 뜻 있는 폴더 표에 있는 것은 이름으로 쓰지 않아도 폴더는 유지한다.
        var normalized = FolderNameNormalizer.Normalize(name);
        if (IsGenericOrFormat(normalized))
        {
            return IsMeaningfulWord(normalized) ? FolderKind.Meaningful : FolderKind.Disposable;
        }

        // 6. 기기 이름
        if (IsDeviceName(name, normalized, context.Devices))
        {
            return FolderKind.Disposable;
        }

        // 조각 전부 뜻 있는 폴더(보정_셀렉트)는 이름으로 쓰지 않지만 폴더는 유지한다.
        if (AllPiecesMeaningful(name))
        {
            return FolderKind.Meaningful;
        }

        // 조각 전부 일반: _로 나눈 조각이 둘 이상이고 조각마다 일반 단어·형식·기기 이름이면 거른다(미분류_ZFC).
        if (AllPiecesGeneric(name, context.Devices))
        {
            return FolderKind.Disposable;
        }

        // 7. 사용자 제외 단어: 이름으로만 쓰지 않는다. 폴더는 사람이 만든 그대로 유지한다.
        if (context.Ignored.Contains(name))
        {
            return FolderKind.Meaningful;
        }

        candidate = new FolderNameCandidate(name, hadDate);
        return FolderKind.EventCandidate;
    }

    /// <summary>
    /// 파일에 가까운 폴더부터 나열한 이름들에서 이벤트 이름 하나를 고른다. 날짜가 붙어 있던 이름이 있으면 그중 가장 가까운 것,
    /// 없으면 후보 중 가장 가까운 것, 후보가 없으면 null.
    /// </summary>
    public static string? PickName(IReadOnlyList<string> foldersNearestFirst, FolderNameContext context)
    {
        var index = PickIndex(foldersNearestFirst, context);
        return index < 0 ? null : Classify(foldersNearestFirst[index], context)?.Name;
    }

    /// <summary><see cref="PickName"/>이 고른 폴더의 위치(파일에 가까운 쪽이 0). 후보가 없으면 -1.</summary>
    internal static int PickIndex(IReadOnlyList<string> foldersNearestFirst, FolderNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PickIndex(foldersNearestFirst, name => Classify(name, context));
    }

    /// <summary><paramref name="classify"/>로 폴더를 판별해 고른다.</summary>
    internal static int PickIndex(IReadOnlyList<string> foldersNearestFirst, Func<string, FolderNameCandidate?> classify)
    {
        ArgumentNullException.ThrowIfNull(foldersNearestFirst);

        var nearest = -1;
        for (var i = 0; i < foldersNearestFirst.Count; i++)
        {
            var candidate = classify(foldersNearestFirst[i]);
            if (candidate is null)
            {
                continue;
            }

            if (candidate.HadDate)
            {
                return i;
            }

            if (nearest < 0)
            {
                nearest = i;
            }
        }

        return nearest;
    }

    // ------------------------------------------------------------------
    // 단계별 검사
    // ------------------------------------------------------------------

    private static (string Text, bool HadDate) StripDate(string text)
    {
        var current = text;
        foreach (var remover in DateRemovers)
        {
            current = remover(current);
        }

        current = LeadingNumber().Replace(current, string.Empty);
        return (current, !string.Equals(current, text, StringComparison.Ordinal));
    }

    // 긴 형식부터 지운다(앞의 규칙이 먼저 가져가야 뒤의 짧은 규칙이 일부만 떼지 않는다).
    private static readonly Func<string, string>[] DateRemovers =
    [
        text => DateRange().Replace(text, string.Empty),
        text => YearMonthDay().Replace(text, string.Empty),
        text => CompactDate().Replace(text, string.Empty),
        text => SixDigitDate().Replace(text, string.Empty),
        text => DayMonthYear().Replace(text, string.Empty),
        text => TwoDigitYearDate().Replace(text, ReplaceTwoDigitYearDate),
        text => YearMonth().Replace(text, string.Empty),
        text => KoreanDate().Replace(text, string.Empty),
        text => EnglishDate().Replace(text, string.Empty),
    ];

    private static string ReplaceTwoDigitYearDate(Match match)
    {
        var a = Number(match.Groups[1]);
        var b = Number(match.Groups[2]);
        var c = Number(match.Groups[3]);
        // yy-MM-dd, MM-dd-yy, dd-MM-yy 중 하나로 읽히면 날짜다.
        var isDate = (IsMonth(b) && IsDay(c)) || (IsMonth(a) && IsDay(b)) || (IsDay(a) && IsMonth(b));
        return isDate ? string.Empty : match.Value;
    }

    private static int Number(Group group) =>
        int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static bool IsMonth(int value) => value is >= 1 and <= 12;

    private static bool IsDay(int value) => value is >= 1 and <= 31;

    // 날짜를 지우고 남은 글자를 다듬는다: 빈 괄호, 괄호 안쪽 공백, 겹친 공백, 앞뒤 구분 기호.
    private static string TidyAfterStrip(string text)
    {
        var current = text;
        for (var i = 0; i < 4; i++)
        {
            var next = EmptyBrackets().Replace(BracketPadding().Replace(current, "$1$2"), string.Empty);
            next = TrimSeparators(WhitespaceRun().Replace(next, " "));
            if (string.Equals(next, current, StringComparison.Ordinal))
            {
                break;
            }

            current = next;
        }

        return TrimSeparators(current);
    }

    // 앞뒤의 공백과 _ - . · 를 지운다.
    private static string TrimSeparators(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSeparator(text[start]))
        {
            start++;
        }

        while (end > start && IsSeparator(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c is '_' or '-' or '.' or '·';

    private static bool IsCameraAutoFolder(string name) => DcimFolder().IsMatch(name) || CameraAutoFolder().IsMatch(name);

    private static bool IsMeaninglessShape(string name) =>
        DigitsAndSymbolsOnly().IsMatch(name)
        || HexOnly().IsMatch(name)
        || Guid().IsMatch(name)
        || PlaceholderName().IsMatch(name)
        || CopyName().IsMatch(name);

    private static bool IsGenericOrFormat(string normalized) =>
        IsGenericWord(normalized)
        || FolderWordTables.FormatNames.Contains(normalized)
        || FolderWordTables.FormatNames.Contains(FolderNameNormalizer.TrimTrailingDigits(normalized));

    private static bool AllPiecesMeaningful(string name)
    {
        var pieces = name.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return pieces.Length >= 2 && pieces.All(piece => IsMeaningfulWord(FolderNameNormalizer.Normalize(piece)));
    }

    private static bool AllPiecesGeneric(string name, DeviceVocabulary devices)
    {
        var pieces = name.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length < 2)
        {
            return false;
        }

        foreach (var piece in pieces)
        {
            var normalized = FolderNameNormalizer.Normalize(piece);
            if (!IsGenericOrFormat(normalized) && !IsDeviceName(piece, normalized, devices))
            {
                return false;
            }
        }

        return true;
    }

    // 프로그램이 만든 폴더: 버전이 붙고 공백이 없는 이름(SomeApp-1.8.0-x64), .PRV로 끝나는 이름.
    private static bool IsProgramFolder(string name) =>
        name.EndsWith(".PRV", StringComparison.OrdinalIgnoreCase)
        || (VersionNumber().IsMatch(name) && !name.Any(char.IsWhiteSpace));

    private static bool IsGenericWord(string normalized) =>
        FolderWordTables.GenericWords.Contains(normalized)
        || FolderWordTables.GenericWords.Contains(FolderNameNormalizer.TrimTrailingDigits(normalized))
        || FolderWordTables.GenericWords.Contains(TrimTrailingCopyNumber(normalized));

    private static bool IsMeaningfulWord(string normalized) =>
        FolderWordTables.MeaningfulWords.Contains(normalized)
        || FolderWordTables.MeaningfulWords.Contains(FolderNameNormalizer.TrimTrailingDigits(normalized))
        || FolderWordTables.MeaningfulWords.Contains(TrimTrailingCopyNumber(normalized));

    // "사진 (2)" -> "사진"
    private static string TrimTrailingCopyNumber(string normalized)
    {
        var match = TrailingParenNumber().Match(normalized);
        return match.Success ? normalized[..match.Index] : normalized;
    }

    [GeneratedRegex(@"\(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingParenNumber();

    private static bool IsDeviceName(string name, string normalized, DeviceVocabulary devices)
    {
        if (devices.Contains(name) || ModelShape().IsMatch(name))
        {
            return true;
        }

        // 폰은 이름 전체일 때만(폰케이스는 통과).
        if (FolderWordTables.WholeNameOnlyFamilyWords.Contains(normalized))
        {
            return true;
        }

        foreach (var phrase in FolderWordTables.ProductFamilyPhrases)
        {
            if (normalized.StartsWith(phrase, StringComparison.Ordinal) && IsAsciiAlphanumeric(normalized.AsSpan(phrase.Length)))
            {
                return true;
            }
        }

        // 공백·_로 나눈 단어 하나가 제품군 단어로 시작하고 나머지가 영문·숫자뿐이면 기기 이름이다(포켓4, 갤럭시S23).
        foreach (var token in name.Split([' ', '_', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = FolderNameNormalizer.Normalize(token);
            foreach (var family in FolderWordTables.ProductFamilyWords)
            {
                if (word.StartsWith(family, StringComparison.Ordinal) && IsAsciiAlphanumeric(word.AsSpan(family.Length)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAsciiAlphanumeric(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
