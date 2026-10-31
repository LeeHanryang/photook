using System.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Presets;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Naming;

public class FolderNameClassifierTests
{
    private static readonly FolderNameContext Plain = new(DeviceVocabulary.Empty, IgnoredFolderNames.Empty);

    [Theory]
    [InlineData("2017-08-15_가족여행_속초", "가족여행_속초", true)]
    [InlineData("2025.03.09 데이트", "데이트", true)]
    [InlineData("20250309 데이트", "데이트", true)]
    [InlineData("250309 데이트", "데이트", true)]
    [InlineData("[2025-01-01] 데이트", "데이트", true)]
    [InlineData("2025-03-08~03-10 여행", "여행", true)]
    [InlineData("2025-03-09 14-30-00 돌잔치", "돌잔치", true)]
    [InlineData("09-03-2025 돌잔치", "돌잔치", true)]
    [InlineData("03_09_25 돌잔치", "돌잔치", true)]
    [InlineData("2025년 3월 9일 돌잔치", "돌잔치", true)]
    [InlineData("2025년 돌잔치", "돌잔치", true)]
    [InlineData("3월 돌잔치", "돌잔치", true)]
    [InlineData("May 2023 Birthday", "Birthday", true)]
    [InlineData("02 Feb My Birthday", "My Birthday", true)]
    [InlineData("2024 제주", "제주", true)]
    [InlineData("데이트", "데이트", false)]
    [InlineData("1박2일", "1박2일", false)]
    [InlineData("가고시마", "가고시마", false)]
    [InlineData("여행", "여행", false)]
    public void Classify_StripsDate(string folder, string expectedName, bool expectedHadDate)
    {
        var result = FolderNameClassifier.Classify(folder, Plain);

        Assert.NotNull(result);
        Assert.Equal(expectedName, result.Name);
        Assert.Equal(expectedHadDate, result.HadDate);
    }

    [Theory]
    // 날짜만
    [InlineData("2026-05-08")]
    [InlineData("[2025-01-01]")]
    [InlineData("2025")]
    [InlineData("03")]
    [InlineData("2025년")]
    [InlineData("May 2023")]
    // 카메라 자동 폴더
    [InlineData("DCIM")]
    [InlineData("MISC")]
    [InlineData("100MSDCF")]
    [InlineData("101_PANA")]
    [InlineData("100APPLE")]
    [InlineData("100NCD40")]
    // 무의미한 모양
    [InlineData("1690164019845")]
    [InlineData("1234")]
    [InlineData("---")]
    [InlineData("a3f9c2d1e0")]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301")]
    [InlineData("새 폴더")]
    [InlineData("새 폴더 (2)")]
    [InlineData("New folder")]
    [InlineData("Untitled")]
    [InlineData("제목 없음")]
    [InlineData("folder1")]
    [InlineData("데이트 사본")]
    [InlineData("Copy of 여행")]
    // 일반 단어
    [InlineData("Downloads")]
    [InlineData("다운로드")]
    [InlineData("Pictures")]
    [InlineData("사진")]
    [InlineData("사진1")]
    [InlineData("Camera 01")]
    [InlineData("Camera Roll")]
    [InlineData("Screenshots")]
    [InlineData("KakaoTalk")]
    [InlineData("KakaoTalkDownload")]
    [InlineData("카카오톡 받은 파일")]
    [InlineData("Telegram Desktop")]
    [InlineData("NAVER MYBOX")]
    [InlineData("images")]
    [InlineData("auxiliary")]
    [InlineData("Takeout")]
    [InlineData("Google Photos")]
    [InlineData("Google 포토")]
    [InlineData("Photos from 2023")]
    [InlineData("2023의 사진")]
    [InlineData("휴지통")]
    [InlineData("미분류")]
    [InlineData("기타")]
    [InlineData("국내")]
    [InlineData("해외")]
    [InlineData("여행별")]
    [InlineData("카메라별")]
    [InlineData("베스트샷")]
    [InlineData("selected")]
    [InlineData("라이트룸")]
    [InlineData("Lightroom")]
    [InlineData("편집본")]
    [InlineData("저화질")]
    [InlineData("저용량")]
    [InlineData("소")]
    // 포맷 이름
    [InlineData("JPG")]
    [InlineData("jpeg")]
    [InlineData("RAW")]
    [InlineData("raw")]
    [InlineData("RAF")]
    [InlineData("RW2")]
    [InlineData("GIF")]
    [InlineData("EXIF")]
    [InlineData("MP4")]
    [InlineData("동영상")]
    // 모델 이름 모양
    [InlineData("S9")]
    [InlineData("R6")]
    [InlineData("A7M4")]
    [InlineData("X100VI")]
    [InlineData("GR3")]
    [InlineData("x100vi")]
    [InlineData("Z-8")]
    // 제품군 단어
    [InlineData("폰")]
    [InlineData("휴대폰")]
    [InlineData("아이폰")]
    [InlineData("iPhone 15")]
    [InlineData("갤럭시S23")]
    [InlineData("Galaxy S23")]
    [InlineData("오즈모 포켓4")]
    [InlineData("오즈모 나노")]
    [InlineData("포켓5")]
    [InlineData("GoPro")]
    [InlineData("Hero7")]
    [InlineData("DJI")]
    [InlineData("Nikon")]
    public void Classify_Rejects(string folder)
    {
        Assert.Null(FolderNameClassifier.Classify(folder, Plain));
    }

    [Theory]
    [InlineData("포켓몬 전시")]
    [InlineData("폰케이스 촬영")]
    [InlineData("G20 정상회의")]
    [InlineData("Z 데이트")]
    [InlineData("EOS R6")] // 어휘에 없으면 통과한다. 어휘가 있을 때의 거름은 Classify_RejectsDeviceFromVocabulary에서 본다.
    public void Classify_Keeps(string folder)
    {
        var result = FolderNameClassifier.Classify(folder, Plain);

        Assert.NotNull(result);
        Assert.Equal(folder, result.Name);
        Assert.False(result.HadDate);
    }

    [Theory]
    [InlineData("라이트룸", FolderKind.Meaningful)]
    [InlineData("Lightroom", FolderKind.Meaningful)]
    [InlineData("포토샵", FolderKind.Meaningful)]
    [InlineData("편집본", FolderKind.Meaningful)]
    [InlineData("보정", FolderKind.Meaningful)]
    [InlineData("베스트샷", FolderKind.Meaningful)]
    [InlineData("셀렉트", FolderKind.Meaningful)]
    [InlineData("selected", FolderKind.Meaningful)]
    [InlineData("저화질", FolderKind.Meaningful)]
    [InlineData("저용량", FolderKind.Meaningful)]
    [InlineData("고용량", FolderKind.Meaningful)]
    [InlineData("대용량", FolderKind.Meaningful)]
    [InlineData("프레임", FolderKind.Meaningful)]
    [InlineData("인화", FolderKind.Meaningful)]
    [InlineData("내보내기", FolderKind.Meaningful)]
    [InlineData("라이트룸 (2)", FolderKind.Meaningful)]
    [InlineData("2024-06-15 라이트룸", FolderKind.Meaningful)]
    [InlineData("데이트", FolderKind.EventCandidate)]
    [InlineData("테이블1", FolderKind.EventCandidate)]
    [InlineData("JPG", FolderKind.Disposable)]
    [InlineData("Zfc", FolderKind.Disposable)]
    [InlineData("2026-05-08", FolderKind.Disposable)]
    [InlineData("DCIM", FolderKind.Disposable)]
    [InlineData("Downloads", FolderKind.Disposable)]
    [InlineData("Telegram Desktop", FolderKind.Disposable)]
    [InlineData("새 폴더", FolderKind.Disposable)]
    [InlineData("여행별", FolderKind.Disposable)]
    [InlineData("미분류", FolderKind.Disposable)]
    [InlineData("국내", FolderKind.Disposable)]
    [InlineData("해외", FolderKind.Disposable)]
    [InlineData("01_촬영", FolderKind.Disposable)]
    [InlineData("ffmpeg", FolderKind.Disposable)]
    [InlineData("해외_selected", FolderKind.Disposable)]
    [InlineData("베스트", FolderKind.Meaningful)]
    [InlineData("best", FolderKind.Meaningful)]
    [InlineData("선택", FolderKind.Meaningful)]
    [InlineData("원본", FolderKind.Meaningful)]
    [InlineData("original", FolderKind.Meaningful)]
    [InlineData("출력", FolderKind.Meaningful)]
    [InlineData("output", FolderKind.Meaningful)]
    [InlineData("고화질", FolderKind.Meaningful)]
    [InlineData("resize", FolderKind.Meaningful)]
    [InlineData("리사이즈", FolderKind.Meaningful)]
    [InlineData("대", FolderKind.Meaningful)]
    [InlineData("중", FolderKind.Meaningful)]
    [InlineData("소", FolderKind.Meaningful)]
    [InlineData("보정_셀렉트", FolderKind.Meaningful)]
    [InlineData("라이트룸_원본", FolderKind.Meaningful)]
    [InlineData("보정_데이트", FolderKind.EventCandidate)]
    [InlineData("미분류_보정", FolderKind.Disposable)]
    [InlineData(".thumbnails", FolderKind.Disposable)]
    [InlineData("", FolderKind.Disposable)]
    public void KindOf_Table(string folder, FolderKind expected)
    {
        var context = new FolderNameContext(DeviceVocabulary.From([], [new DeviceAlias("Zfc")]), IgnoredFolderNames.Empty);

        Assert.Equal(expected, FolderNameClassifier.KindOf(folder, context));
    }

    [Theory]
    [InlineData("라이트룸")]
    [InlineData("Lightroom")]
    [InlineData("포토샵")]
    [InlineData("편집본")]
    [InlineData("보정")]
    [InlineData("베스트샷")]
    [InlineData("셀렉트")]
    [InlineData("selected")]
    [InlineData("저화질")]
    [InlineData("저용량")]
    [InlineData("고용량")]
    [InlineData("대용량")]
    [InlineData("프레임")]
    [InlineData("인화")]
    [InlineData("내보내기")]
    [InlineData("베스트")]
    [InlineData("선택")]
    [InlineData("원본")]
    [InlineData("출력")]
    [InlineData("resize")]
    [InlineData("대")]
    [InlineData("보정_셀렉트")]
    public void Classify_MeaningfulIsNotEventName(string folder)
    {
        Assert.Null(FolderNameClassifier.Classify(folder, Plain));
    }

    [Theory]
    [InlineData("중복")]
    [InlineData("중복1")]
    [InlineData("2025-03-01 중복")]
    public void Classify_DuplicateFolder_GivesNoName(string folder)
    {
        Assert.Null(FolderNameClassifier.Classify(folder, Plain));
        Assert.Equal(FolderKind.Disposable, FolderNameClassifier.KindOf(folder, Plain));
    }

    [Theory]
    [InlineData("IMG")]
    [InlineData("IMG_20240615")]
    [InlineData("DSC_20240615")]
    [InlineData("DSCF_20240615")]
    [InlineData("DSCN_20240615")]
    [InlineData("VID_20240615")]
    [InlineData("PXL_20240615")]
    [InlineData("MVIMG_20240615")]
    [InlineData("MOV_20240615")]
    [InlineData("dsc")]
    public void Classify_CameraFilePrefix_GivesNoName(string folder)
    {
        Assert.Null(FolderNameClassifier.Classify(folder, Plain));
        Assert.Equal(FolderKind.Disposable, FolderNameClassifier.KindOf(folder, Plain));
    }

    [Fact]
    public void KindOf_UserIgnoredName_IsMeaningful()
    {
        var fs = new FakeFileSystem();
        const string path = @"C:\data\ignored-folder-names.txt";
        fs.AddFile(path, "데이트\r\n"u8.ToArray());
        var context = new FolderNameContext(DeviceVocabulary.Empty, IgnoredFolderNames.Load(fs, path));

        // 이름으로 쓰지 않을 뿐, 폴더는 없애지 않는다.
        Assert.Equal(FolderKind.Meaningful, FolderNameClassifier.KindOf("데이트", context));
        Assert.Null(FolderNameClassifier.Classify("데이트", context));
    }

    [Theory]
    [InlineData("Zf")]
    [InlineData("Z f")]
    [InlineData("z-f")]
    [InlineData("NIKON Z f")]
    public void Classify_RejectsDeviceFromVocabulary(string folder)
    {
        var context = new FolderNameContext(
            DeviceVocabulary.From([Photo("NIKON", "Z f")], []), IgnoredFolderNames.Empty);

        Assert.Null(FolderNameClassifier.Classify(folder, context));
    }

    [Theory]
    [InlineData("01_촬영")]
    [InlineData("99_보관")]
    [InlineData("_중복_검토")]
    [InlineData(".thumbnails")]
    [InlineData("해외_selected")]
    [InlineData("RAW_JPG")]
    [InlineData("Adobe Premiere Pro Auto-Save")]
    [InlineData("Adobe Premiere Pro Audio Previews")]
    [InlineData("Peak Files")]
    [InlineData("NKSC_PARAM")]
    [InlineData("Expert RAW")]
    [InlineData("SomeApp-1.8.0-windows-x64")]
    [InlineData("전시회.PRV")]
    [InlineData("대용량")]
    [InlineData("고용량")]
    [InlineData("webp")]
    [InlineData("no_frame")]
    [InlineData("미니카메라")]
    [InlineData("기기테스트")]
    [InlineData("translations")]
    [InlineData("tls")]
    [InlineData("styles")]
    [InlineData("platforms")]
    [InlineData("networkinformation")]
    [InlineData("imageformats")]
    [InlineData("iconengines")]
    [InlineData("generic")]
    [InlineData("ffmpeg")]
    public void Classify_RejectsObservedFolders(string folder)
    {
        Assert.Null(FolderNameClassifier.Classify(folder, Plain));
    }

    [Fact]
    public void Classify_RejectsPiecesThatAreAllGenericOrDevice()
    {
        var context = new FolderNameContext(
            DeviceVocabulary.From([Photo(null, "ZFC")], []), IgnoredFolderNames.Empty);

        Assert.Null(FolderNameClassifier.Classify("미분류_ZFC", context));
        // 어휘에 ZFC가 없으면 모델 모양이 아니므로 통과한다.
        Assert.NotNull(FolderNameClassifier.Classify("미분류_ZFC", Plain));
    }

    [Theory]
    [InlineData("02_여행", "여행")]
    [InlineData("1_돌잔치", "돌잔치")]
    [InlineData("여행_셀렉트", "여행_셀렉트")]
    [InlineData("공모전_사진전", "공모전_사진전")]
    [InlineData("돌잔치_gif", "돌잔치_gif")]
    [InlineData("v1.2 촬영회", "v1.2 촬영회")]
    [InlineData("1박2일", "1박2일")]
    [InlineData("2박 3일 제주", "2박 3일 제주")]
    [InlineData("10일간 제주", "10일간 제주")]
    [InlineData("3일차 투어", "3일차 투어")]
    public void Classify_KeepsObservedNames(string folder, string expectedName)
    {
        var result = FolderNameClassifier.Classify(folder, Plain);

        Assert.NotNull(result);
        Assert.Equal(expectedName, result.Name);
        Assert.False(result.HadDate);
    }

    [Theory]
    [InlineData("CR2")]
    [InlineData("CR3")]
    [InlineData("RW2")]
    [InlineData("MP4")]
    public void FormatNames_ContainEntriesWithDigits(string format)
    {
        // 숫자가 든 형식 이름은 모델 모양 검사에 걸리기 전에 형식 표로도 거른다.
        Assert.Contains(FolderNameNormalizer.Normalize(format), (IEnumerable<string>)FolderWordTables.FormatNames);
    }

    [Fact]
    public void Classify_NeverThrows_AndFinishesOnLongRuns()
    {
        var inputs = new[]
        {
            new string(' ', 255), new string('0', 255), new string('-', 255), new string('[', 255), new string('(', 255),
            "a" + new string(' ', 253) + "]", "[" + new string(' ', 253) + "a", new string('1', 127) + "." + new string('2', 127),
            string.Concat(Enumerable.Repeat("1-", 127)), string.Concat(Enumerable.Repeat("May ", 63)),
            string.Concat(Enumerable.Repeat("_", 255)), "a" + new string('_', 253) + "b",
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            var exception = Record.Exception(() => FolderNameClassifier.Classify(input, Plain));
            Assert.Null(exception);
        }

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"{stopwatch.Elapsed}");
    }

    [Fact]
    public void Classify_WithoutVocabulary_KeepsDeviceLikeNames()
    {
        // 어휘가 없으면 모양·제품군 규칙에 걸리지 않는 기기 이름은 통과한다(어휘가 있어야 거른다).
        Assert.NotNull(FolderNameClassifier.Classify("Zf", Plain));
        Assert.NotNull(FolderNameClassifier.Classify("Z f", Plain));
    }

    [Fact]
    public void Classify_RejectsDeviceFromVocabulary_ModelWithoutMake()
    {
        var context = new FolderNameContext(
            DeviceVocabulary.From([Photo(null, "EOS R6")], []), IgnoredFolderNames.Empty);

        Assert.Null(FolderNameClassifier.Classify("EOS R6", context));
        Assert.NotNull(FolderNameClassifier.Classify("EOS R6 결혼식", context));
    }

    [Fact]
    public void Classify_RejectsUserIgnored_IgnoringCaseAndSpaces()
    {
        var fs = new FakeFileSystem();
        const string path = @"C:\data\ignored-folder-names.txt";
        fs.AddFile(path, "셀렉션\r\nMy Event\r\n"u8.ToArray());
        var context = new FolderNameContext(DeviceVocabulary.Empty, IgnoredFolderNames.Load(fs, path));

        Assert.Null(FolderNameClassifier.Classify("셀렉션", context));
        Assert.Null(FolderNameClassifier.Classify("2025-03-09 셀렉션", context));
        Assert.Null(FolderNameClassifier.Classify("my_event", context));
        Assert.Null(FolderNameClassifier.Classify("MY  EVENT", context));
        Assert.NotNull(FolderNameClassifier.Classify("셀렉션 모음", context));
    }

    [Theory]
    [InlineData("데이트. ", "데이트")]
    [InlineData("데이트...", "데이트")]
    [InlineData(" 데이트 ", "데이트")]
    public void Classify_StripsTrailingDotsAndSpaces(string folder, string expectedName)
    {
        var result = FolderNameClassifier.Classify(folder, Plain);

        Assert.NotNull(result);
        Assert.Equal(expectedName, result.Name);
        Assert.False(result.HadDate);
    }

    [Fact]
    public void Classify_NeverThrows()
    {
        foreach (var input in new[] { "", "   ", "\t", new string('가', 300), new string('a', 300), "[[[(", "2025-13-45", "\ud800" })
        {
            var exception = Record.Exception(() => FolderNameClassifier.Classify(input, Plain));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void Classify_EmptyOrBlank_IsNull()
    {
        Assert.Null(FolderNameClassifier.Classify("", Plain));
        Assert.Null(FolderNameClassifier.Classify("   ", Plain));
        Assert.Null(FolderNameClassifier.Classify("\t", Plain));
    }

    [Fact]
    public void Classify_ImpossibleDate_IsNotStripped()
    {
        var result = FolderNameClassifier.Classify("13-45-2025 돌잔치", Plain);

        Assert.NotNull(result);
        Assert.False(result.HadDate);
        Assert.Contains("돌잔치", result.Name);
    }

    [Fact]
    public void Classify_DoesNotFindDateInsideLongDigits()
    {
        // 긴 숫자 안에서 날짜를 찾지 않는다. 숫자뿐인 이름은 어차피 후보가 아니다.
        Assert.Null(FolderNameClassifier.Classify("1690164019845", Plain));
        var result = FolderNameClassifier.Classify("가 12025030912 나", Plain);
        Assert.NotNull(result);
        Assert.False(result.HadDate);
    }

    [Fact]
    public void PickName_PrefersDatedName()
    {
        var name = FolderNameClassifier.PickName(["테이블1", "코닥골드", "2026-05-08 저녁모임", "2026"], Plain);

        Assert.Equal("저녁모임", name);
    }

    [Fact]
    public void PickName_NearestDatedWins()
    {
        var name = FolderNameClassifier.PickName(["2026-05-08 세부", "2026-05-01 큰 행사"], Plain);

        Assert.Equal("세부", name);
    }

    [Fact]
    public void PickName_NearestUndated()
    {
        Assert.Equal("데이트", FolderNameClassifier.PickName(["폰", "데이트", "2025", "사진"], Plain));
        Assert.Equal("가고시마", FolderNameClassifier.PickName(["가고시마", "여행별"], Plain));
    }

    [Fact]
    public void PickName_NoneLeft()
    {
        Assert.Null(FolderNameClassifier.PickName(["Telegram Desktop", "Downloads"], Plain));
        Assert.Null(FolderNameClassifier.PickName([], Plain));
    }

    private static PhotoInfo Photo(string? make, string? model) =>
        new(@"C:\a.jpg", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), MediaKind.Image, null, make, model, null);
}
