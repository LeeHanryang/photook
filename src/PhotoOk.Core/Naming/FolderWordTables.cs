using System.Collections.Frozen;

namespace PhotoOk.Core.Naming;

/// <summary>
/// 폴더 이름 판별기가 쓰는 단어 표. 일반 단어만 둔다(개인 폴더 이름은 넣지 않는다).
/// 단어를 늘릴 때는 아래 배열에 한 줄을 더하면 된다. 비교는 <see cref="FolderNameNormalizer.Normalize"/>한 값(공백·<c>-</c>·<c>_</c>를 지우고
/// 소문자)으로 하므로 대소문자와 띄어쓰기는 신경 쓰지 않아도 된다.
/// </summary>
internal static class FolderWordTables
{
    private static readonly string[] SystemWords =
    [
        "Downloads", "다운로드", "Desktop", "바탕화면", "Documents", "문서", "Pictures", "사진", "Photos",
        "Camera", "카메라", "Camera Roll", "Saved Pictures", "Movies", "Videos", "Screenshots", "스크린샷",
        "thumbnails", "cache", "temp", "tmp", "임시",
    ];

    private static readonly string[] AppWords =
    [
        "KakaoTalk", "KakaoTalkDownload", "카카오톡", "카카오톡 받은 파일", "Telegram Desktop", "Telegram Images",
        "WhatsApp Images", "WhatsApp Video", "Instagram", "Facebook", "Snapchat", "Snow", "B612", "NAVER MYBOX",
        "Naver", "OneDrive", "Google Drive", "iCloud Photos", "images", "auxiliary",
    ];

    // Google Takeout. "Photos from yyyy"와 "yyyy의 사진"은 숫자가 들어 있어 FolderNameClassifier의 정규식으로 검사한다.
    private static readonly string[] TakeoutWords =
    [
        "Takeout", "Google Photos", "Google 포토", "Trash", "Bin", "휴지통", "Archive", "보관함",
    ];

    private static readonly string[] SortingWords =
    [
        "백업", "Backup", "정리", "미분류", "기타", "etc", "misc", "일반", "테스트", "test", "sample", "샘플",
        "국내", "해외", "여행별", "카메라별", "날짜별", "월별", "연도별", "중복",
    ];

    // 이름으로는 쓰지 않지만 사람이 일부러 만든 작업 폴더라 하위 폴더로 유지하는 단어(FolderKind.Meaningful).
    // 일반 단어로도 거르므로 Classify는 이 단어를 이벤트 이름으로 돌려주지 않는다.
    private static readonly string[] MeaningfulList =
    [
        "라이트룸", "Lightroom", "포토샵", "Photoshop", "보정", "보정본", "편집", "편집본", "edit", "edited",
        "베스트샷", "셀렉", "셀렉트", "selected", "저화질", "고화질", "저용량", "고용량", "대용량",
        "프레임", "인화", "내보내기", "export",
        // 사람이 나눈 구분(고르기·원본·크기)
        "베스트", "best", "선택", "원본", "original", "출력", "output", "resize", "리사이즈", "대", "중", "소",
    ];

    // 실제 폴더 이름을 재어 보고 더한 단어(작업 폴더, 프로그램이 만든 폴더).
    private static readonly string[] ObservedWords =
    [
        "촬영", "보관", "webp", "no_frame", "미니카메라", "기기테스트", "Peak Files", "NKSC_PARAM",
        "Expert RAW", "translations", "tls", "styles", "platforms", "networkinformation", "imageformats", "iconengines",
        "generic", "ffmpeg",
    ];

    // 카메라·휴대폰이 파일 이름 앞에 붙이는 글자. 날짜를 떼고 이것만 남으면(IMG_20240615) 이름이 아니다.
    private static readonly string[] CameraFilePrefixes =
    [
        "IMG", "DSC", "DSCF", "DSCN", "VID", "PXL", "MVIMG", "MOV",
    ];

    private static readonly string[] FormatWords =
    [
        "JPG", "JPEG", "RAW", "NEF", "CR2", "CR3", "ARW", "RAF", "RW2", "ORF", "DNG", "HEIC", "HEIF", "PNG", "GIF",
        "TIFF", "MP4", "MOV", "EXIF", "XMP", "동영상", "영상", "Video",
    ];

    // 세대 번호와 상관없이 이 단어로 시작하고 나머지가 영문·숫자뿐인 단어는 기기 이름으로 본다(포켓4, 갤럭시S23).
    private static readonly string[] ProductFamilyWordList =
    [
        "휴대폰", "핸드폰", "스마트폰", "아이폰", "iPhone", "갤럭시", "Galaxy", "픽셀", "Pixel", "오즈모", "Osmo",
        "포켓", "Pocket", "고프로", "GoPro", "히어로", "Hero", "인스타360", "Insta360", "DJI", "드론", "Drone",
        "Canon", "캐논", "Nikon", "니콘", "Sony", "소니", "Fujifilm", "후지", "Panasonic", "파나소닉",
        "Leica", "라이카", "Ricoh", "리코", "Olympus", "올림푸스", "Adobe",
    ];

    // 여러 단어로 된 제품군 이름. 이름 앞부분이 이것이고 나머지가 영문·숫자뿐이면 기기 이름이다.
    private static readonly string[] ProductFamilyPhraseList = ["OM System"];

    /// <summary>"폰"은 이름 전체가 이것일 때만 기기 이름이다("폰케이스"는 통과).</summary>
    private static readonly string[] WholeNameOnlyFamilyList = ["폰"];

    /// <summary>일반 단어(시스템·앱·Takeout·정리·보정과 뜻 있는 폴더 단어). 끝의 숫자를 떼고 비교한다.</summary>
    public static FrozenSet<string> GenericWords { get; } =
        Normalized(SystemWords, AppWords, TakeoutWords, SortingWords, ObservedWords, CameraFilePrefixes, MeaningfulList);

    /// <summary>뜻 있는 폴더 단어. <see cref="GenericWords"/>에도 들어 있다. 끝의 숫자를 떼고 비교한다.</summary>
    public static FrozenSet<string> MeaningfulWords { get; } = Normalized(MeaningfulList);

    /// <summary>파일 형식 이름.</summary>
    public static FrozenSet<string> FormatNames { get; } = Normalized(FormatWords);

    /// <summary>제품군 단어(한 단어).</summary>
    public static IReadOnlyList<string> ProductFamilyWords { get; } = NormalizedList(ProductFamilyWordList);

    /// <summary>제품군 이름(여러 단어).</summary>
    public static IReadOnlyList<string> ProductFamilyPhrases { get; } = NormalizedList(ProductFamilyPhraseList);

    /// <summary>이름 전체가 같을 때만 기기 이름인 제품군 단어.</summary>
    public static FrozenSet<string> WholeNameOnlyFamilyWords { get; } = Normalized(WholeNameOnlyFamilyList);

    private static FrozenSet<string> Normalized(params string[][] lists) =>
        lists.SelectMany(list => list).Select(FolderNameNormalizer.Normalize).ToFrozenSet(StringComparer.Ordinal);

    private static string[] NormalizedList(string[] list) => list.Select(FolderNameNormalizer.Normalize).ToArray();
}
