using System.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Naming;

public class EventNameReaderTests
{
    private static readonly FolderNameContext Plain = new(DeviceVocabulary.Empty, IgnoredFolderNames.Empty);

    private static Preset PresetOf(params string[] levels) =>
        new("테스트", TimeOnly.MinValue,
            [new LayoutBranch("기본", BranchCondition.Always, levels.Select(l => new LevelTemplate(l)).ToList())],
            [], new Dictionary<string, IReadOnlyList<string>>());

    private static FileGroup Group(string path, DateTime? capture = null) =>
        new(new PhotoInfo(path, 100, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            MediaKind.Image, capture, null, null, null), []);

    private static string? Read(Preset preset, JobKind kind, bool useFolderNames, string root, string file, DateTime? capture = null) =>
        new EventNameReader(preset, kind, useFolderNames, [root], Plain).Read(Group(file, capture));

    private static readonly Preset DatedEvent = PresetOf("{연도}", "[{기간}] {이벤트}", "{기기}");

    [Fact]
    public void Reorganize_ReadsFromPresetFormat()
    {
        Assert.Equal("데이트",
            Read(DatedEvent, JobKind.Reorganize, false, @"D:\A\촬영", @"D:\A\촬영\2025\[2025-01-01] 데이트\Z8\a.jpg"));
    }

    [Fact]
    public void Reorganize_DateInFolderDiffersFromExif_StillReads()
    {
        Assert.Equal("데이트",
            Read(DatedEvent, JobKind.Reorganize, false, @"D:\A\촬영", @"D:\A\촬영\2025\[2025-01-01] 데이트\Z8\a.jpg",
                new DateTime(2025, 1, 2, 10, 0, 0)));
    }

    [Fact]
    public void Reorganize_NoEventInFolder_Null()
    {
        Assert.Null(Read(DatedEvent, JobKind.Reorganize, false, @"D:\A\촬영", @"D:\A\촬영\2025\[2025-01-01]\Zf\a.jpg"));
    }

    [Fact]
    public void Reorganize_LevelWithOnlyEventToken_NotUsedForFormat()
    {
        var preset = PresetOf("{이벤트}");
        var file = @"D:\A\촬영\Zf\a.jpg";
        var devices = new FolderNameContext(DeviceVocabulary.From([], [new DeviceAlias("Zf")]), IgnoredFolderNames.Empty);

        Assert.Null(Read(preset, JobKind.Reorganize, false, @"D:\A\촬영", file));
        Assert.Null(new EventNameReader(preset, JobKind.Reorganize, true, [@"D:\A\촬영"], devices).Read(Group(file)));
    }

    [Fact]
    public void Reorganize_DateEventFormat()
    {
        var preset = PresetOf("{날짜} {이벤트}");

        Assert.Equal("출사", Read(preset, JobKind.Reorganize, false, @"D:\A", @"D:\A\2025-01-01 출사\a.jpg"));
        // 판별기는 '사진'을 일반 단어로 거르므로, 켜 놓고도 '사진'이 나오면 형식이 먼저 적용된 것이다.
        Assert.Equal("사진", Read(preset, JobKind.Reorganize, true, @"D:\A", @"D:\A\2025-01-01 사진\a.jpg"));
    }

    [Fact]
    public void Reorganize_ReadsEveryBranchFormat()
    {
        var preset = new Preset("테스트", TimeOnly.MinValue,
        [
            new LayoutBranch("검토", BranchCondition.NeedsReview, [new LevelTemplate("{날짜} {이벤트}")]),
            new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("[{기간}] {이벤트}")]),
        ], [], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal("출사", Read(preset, JobKind.Reorganize, false, @"D:\A", @"D:\A\2025-01-01 출사\a.jpg"));
        Assert.Equal("데이트", Read(preset, JobKind.Reorganize, false, @"D:\A", @"D:\A\[2025-01-01] 데이트\a.jpg"));
    }

    [Fact]
    public void Reorganize_SameFormatInTwoBranches_ReadsOnce()
    {
        var level = new LevelTemplate("[{기간}] {이벤트}");
        var preset = new Preset("테스트", TimeOnly.MinValue,
        [
            new LayoutBranch("검토", BranchCondition.NeedsReview, [level]),
            new LayoutBranch("기본", BranchCondition.Always, [level]),
        ], [], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal("데이트", Read(preset, JobKind.Reorganize, false, @"D:\A", @"D:\A\[2025-01-01] 데이트\a.jpg"));
    }

    [Fact]
    public void Reorganize_FallsBackToClassifier_WhenOn()
    {
        var file = @"D:\A\촬영\데이트\a.jpg";

        Assert.Equal("데이트", Read(DatedEvent, JobKind.Reorganize, true, @"D:\A\촬영", file));
        Assert.Null(Read(DatedEvent, JobKind.Reorganize, false, @"D:\A\촬영", file));
    }

    [Fact]
    public void Absorb_UsesClassifierOnlyWhenOn()
    {
        var file = @"D:\다운\데이트\a.jpg";
        Assert.Equal("데이트", Read(DatedEvent, JobKind.Absorb, true, @"D:\다운", file));
        Assert.Null(Read(DatedEvent, JobKind.Absorb, false, @"D:\다운", file));
        Assert.Null(Read(DatedEvent, JobKind.Absorb, false, @"D:\다운", @"D:\다운\[2025-01-01] 데이트\a.jpg"));
    }

    [Fact]
    public void Absorb_FileDirectlyInRoot_UsesRootName()
    {
        Assert.Equal("데이트", Read(DatedEvent, JobKind.Absorb, true, @"D:\사진\데이트", @"D:\사진\데이트\a.jpg"));
    }

    [Theory]
    [InlineData(JobKind.Absorb, @"Telegram Desktop\a.jpg", null)] // 걸러진 폴더를 타고 고른 폴더까지 올라가지 않는다
    [InlineData(JobKind.Absorb, @"폰\a.jpg", null)]
    [InlineData(JobKind.Absorb, @"a.jpg", "모음")] // 바로 안에 있을 때만 고른 폴더의 이름을 쓴다
    [InlineData(JobKind.Absorb, @"소풍\Telegram Desktop\a.jpg", "소풍")]
    [InlineData(JobKind.Reorganize, @"Telegram Desktop\a.jpg", null)]
    [InlineData(JobKind.Reorganize, @"폰\a.jpg", null)]
    [InlineData(JobKind.Reorganize, @"a.jpg", "모음")]
    public void Classifier_RootNameOnlyForFileDirectlyInRoot(JobKind kind, string file, string? expected)
    {
        Assert.Equal(expected, Read(DatedEvent, kind, true, @"D:\모음", @"D:\모음\" + file));
    }

    [Fact]
    public void Classifier_RootNameRule_DoesNotChangeFormatReading()
    {
        // 형식 읽기는 고른 폴더의 이름도 그대로 읽는다.
        Assert.Equal("일상", Read(DatedEvent, JobKind.Reorganize, true, @"D:\A\[2024-06-15] 일상", @"D:\A\[2024-06-15] 일상\Telegram Desktop\a.jpg"));
    }

    [Fact]
    public void DoesNotLookAboveRoot()
    {
        Assert.Null(Read(DatedEvent, JobKind.Absorb, true, @"D:\데이트\다운로드", @"D:\데이트\다운로드\a.jpg"));
    }

    [Fact]
    public void DriveRootHasNoName()
    {
        Assert.Null(Read(DatedEvent, JobKind.Absorb, true, @"F:\", @"F:\a.jpg"));
    }

    [Fact]
    public void Import_NeverReads()
    {
        Assert.Null(Read(DatedEvent, JobKind.Import, true, @"D:\다운", @"D:\다운\데이트\a.jpg"));
    }

    [Fact]
    public void PresetWithoutEvent_NeverReads()
    {
        var preset = PresetOf("{연도}", "{날짜}");

        Assert.False(EventNameReader.PresetUsesEvents(preset));
        Assert.True(EventNameReader.PresetUsesEvents(DatedEvent));
        Assert.Null(Read(preset, JobKind.Absorb, true, @"D:\다운", @"D:\다운\데이트\a.jpg"));
    }

    [Fact]
    public void LongestMatchingRootWins()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\다운", @"D:\다운\데이트"], Plain);

        Assert.Equal("데이트", reader.Read(Group(@"D:\다운\데이트\a.jpg")));
    }

    [Fact]
    public void FileOutsideEveryRoot_Null()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\다운"], Plain);

        Assert.Null(reader.Read(Group(@"E:\데이트\a.jpg")));
    }

    // ------------------------------------------------------------------
    // 하위 폴더 읽기
    // ------------------------------------------------------------------

    private static readonly FolderNameContext WithZfc =
        new(DeviceVocabulary.From([], [new DeviceAlias("Zfc"), new DeviceAlias("x100vi")]), IgnoredFolderNames.Empty);

    private static EventNameReader ReaderOf(JobKind kind, bool useFolderNames, string root) =>
        new(DatedEvent, kind, useFolderNames, [root], WithZfc);

    [Fact]
    public void KeptFolders_BelowNamedFolder()
    {
        var group = Group(@"D:\A\2024\[2024-06-15] 일상\Zfc\라이트룸\테이블1\프레임\a.jpg");
        var reader = ReaderOf(JobKind.Absorb, true, @"D:\A");

        Assert.Equal("일상", reader.Read(group));
        Assert.Equal(["라이트룸", "테이블1", "프레임"], reader.KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_Reorganize_BelowFormatFolder()
    {
        var group = Group(@"D:\A\2024\[2024-06-15] 일상\Zfc\라이트룸\테이블1\a.jpg");
        var reader = ReaderOf(JobKind.Reorganize, false, @"D:\A");

        Assert.Equal("일상", reader.Read(group));
        Assert.Equal(["라이트룸", "테이블1"], reader.KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_DropsDeviceAndFormat()
    {
        var group = Group(@"D:\A\2024\[2024-06-15] 일상\x100vi\RAF\a.raf");

        Assert.Empty(ReaderOf(JobKind.Absorb, true, @"D:\A").KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_NoName_BelowRoot()
    {
        var group = Group(@"D:\A\라이트룸\a.jpg");
        var reader = ReaderOf(JobKind.Absorb, false, @"D:\A");

        Assert.Null(reader.Read(group));
        Assert.Equal(["라이트룸"], reader.KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_NoName_RootNameNotACandidate()
    {
        var group = Group(@"D:\Downloads\라이트룸\a.jpg");
        var reader = ReaderOf(JobKind.Absorb, true, @"D:\Downloads");

        Assert.Null(reader.Read(group));
        Assert.Equal(["라이트룸"], reader.KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_DropsAboveName()
    {
        var group = Group(@"D:\A\여행\제주도\폰\a.jpg");
        var reader = ReaderOf(JobKind.Absorb, true, @"D:\A");

        Assert.Equal("제주도", reader.Read(group));
        Assert.Empty(reader.KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_KeepsInFarToNearOrder()
    {
        var group = Group(@"D:\A\2024-06-15 일상\라이트룸\테이블1\a.jpg");
        var reader = ReaderOf(JobKind.Absorb, true, @"D:\A");

        Assert.Equal(["라이트룸", "테이블1"], reader.KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_Import_Empty()
    {
        var group = Group(@"D:\A\라이트룸\a.jpg");

        Assert.Empty(ReaderOf(JobKind.Import, true, @"D:\A").KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_RootItselfNotKept()
    {
        var group = Group(@"D:\A\라이트룸\a.jpg");

        Assert.Empty(ReaderOf(JobKind.Absorb, false, @"D:\A\라이트룸").KeptFolders(group));
        Assert.Empty(ReaderOf(JobKind.Absorb, true, @"D:\A\라이트룸").KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_FileOutsideEveryRoot_Empty()
    {
        Assert.Empty(ReaderOf(JobKind.Absorb, true, @"D:\A").KeptFolders(Group(@"E:\라이트룸\a.jpg")));
    }

    // ------------------------------------------------------------------
    // \로 나눈 이름 다시 읽기
    // ------------------------------------------------------------------

    [Fact]
    public void Reorganize_BackslashPiecesBelowFormatFolder_AreReadBack()
    {
        var group = Group(@"D:\A\2025\[2025-01-01] 가족여행\제주\Zfc\JPG\a.jpg");
        var reader = ReaderOf(JobKind.Reorganize, false, @"D:\A");

        Assert.Equal(@"가족여행\제주", reader.Read(group));
        Assert.Empty(reader.KeptFolders(group));
    }

    [Fact]
    public void Reorganize_ManyPieces_AreReadInOrder()
    {
        var group = Group(@"D:\A\[2025-01-01] 가족여행\제주\바닷가\a.jpg");

        Assert.Equal(@"가족여행\제주\바닷가", ReaderOf(JobKind.Reorganize, false, @"D:\A").Read(group));
    }

    [Theory]
    [InlineData(@"[2025-01-01] 가족여행\Zfc\제주\a.jpg", new[] { "제주" })] // 기기에서 멈춘다
    [InlineData(@"[2025-01-01] 가족여행\JPG\제주\a.jpg", new[] { "제주" })] // 포맷에서 멈춘다
    [InlineData(@"[2025-01-01] 가족여행\라이트룸\제주\a.jpg", new[] { "라이트룸", "제주" })] // 뜻 있는 폴더에서 멈춘다
    public void Reorganize_PiecesStopAtFirstNonCandidate(string relative, string[] kept)
    {
        var group = Group(@"D:\A\" + relative);
        var reader = ReaderOf(JobKind.Reorganize, false, @"D:\A");

        Assert.Equal("가족여행", reader.Read(group));
        Assert.Equal(kept, reader.KeptFolders(group));
    }

    [Fact]
    public void Reorganize_PiecesStopAtPresetOwnedName()
    {
        var group = Group(@"D:\A\사진첩\[2025-01-01] 가족여행\기타 이미지\a.tif");
        var reader = new EventNameReader(Owned, JobKind.Reorganize, false, [@"D:\A"], Plain);

        Assert.Equal("가족여행", reader.Read(group));
    }

    [Fact]
    public void Reorganize_KeptFoldersStartBelowLastPiece()
    {
        var group = Group(@"D:\A\[2025-01-01] 가족여행\제주\Zfc\라이트룸\테이블1\a.jpg");
        var reader = ReaderOf(JobKind.Reorganize, false, @"D:\A");

        Assert.Equal(@"가족여행\제주", reader.Read(group));
        Assert.Equal(["라이트룸", "테이블1"], reader.KeptFolders(group));
    }

    [Fact]
    public void Reorganize_PieceWithDate_KeepsFolderNameAsWritten()
    {
        // 판별기가 날짜를 떼어 낸 이름이 아니라 폴더 이름 그대로여야 다시 읽어도 자리가 같다.
        var group = Group(@"D:\A\[2025-01-01] 가족여행\2025-01-01 제주\a.jpg");

        Assert.Equal(@"가족여행\2025-01-01 제주", ReaderOf(JobKind.Reorganize, false, @"D:\A").Read(group));
    }

    [Fact]
    public void Reorganize_PiecesWithSplitUnderscores_NoEmptyPiece()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Plain, splitUnderscores: true);

        Assert.Equal(@"가\나\제주", reader.Read(Group(@"D:\A\[2025-01-01] 가_나\제주\a.jpg")));
        Assert.Equal("제주", reader.Read(Group(@"D:\A\[2025-01-01] ___\제주\a.jpg")));
    }

    [Fact]
    public void Reorganize_OriginalFolderInSameBranch_DoesNotReadPieces()
    {
        // {원래폴더}가 같은 분기에 있으면 그 폴더가 이름과 겹쳐 다시 정리할 때마다 깊어진다. 그때는 하위 폴더로 둔다.
        var preset = PresetOf("{연도}", "[{날짜}] {이벤트}", "{원래폴더}");
        var group = Group(@"D:\A\[2025-01-01] 가족여행\제주\a.jpg");
        var reader = new EventNameReader(preset, JobKind.Reorganize, false, [@"D:\A"], Plain);

        Assert.Equal("가족여행", reader.Read(group));
        Assert.Equal(["제주"], reader.KeptFolders(group));
    }

    [Fact]
    public void Absorb_DoesNotReadPieces()
    {
        // 판별기는 가까운 후보 하나를 고른다. 이어 읽기는 형식 읽기에만 있다.
        var group = Group(@"D:\A\가족여행\제주\a.jpg");

        Assert.Equal("제주", ReaderOf(JobKind.Absorb, true, @"D:\A").Read(group));
    }

    // ------------------------------------------------------------------
    // 고른 폴더가 이벤트 폴더보다 아래일 때
    // ------------------------------------------------------------------

    private static EventNameReader ReaderBelow(string root, bool useFolderNames = false, Preset? preset = null, string archive = @"D:\A") =>
        new(preset ?? DatedEvent, JobKind.Reorganize, useFolderNames, [root], WithZfc, archiveRoot: archive);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reorganize_RootBelowFormatFolder_StillReadsName(bool useFolderNames)
    {
        var root = @"D:\A\2024\[2024-06-15] 일상\Zfc";

        Assert.Equal("일상", ReaderBelow(root, useFolderNames).Read(Group(root + @"\a.jpg")));
        Assert.Equal("일상", ReaderBelow(root, useFolderNames).Read(Group(root + @"\라이트룸\테이블1\a.jpg")));
    }

    [Fact]
    public void Reorganize_RootBelowFormatFolder_KeptFoldersStartBelowName()
    {
        var root = @"D:\A\2024\[2024-06-15] 일상\라이트룸\Zfc";
        var group = Group(root + @"\보정\테이블1\a.jpg");
        var reader = ReaderBelow(root);

        Assert.Equal("일상", reader.Read(group));
        Assert.Equal(["라이트룸", "보정", "테이블1"], reader.KeptFolders(group)); // 어느 폴더를 골라도 결과가 같다. 기기 폴더는 빠진다
    }

    [Theory]
    [InlineData(@"일상\Zfc\라이트룸", @"c.jpg", new[] { "라이트룸" })]
    [InlineData(@"일상\Zfc\라이트룸", @"테이블1\a.jpg", new[] { "라이트룸", "테이블1" })]
    [InlineData(@"일상\Zfc\라이트룸\테이블1", @"a.jpg", new[] { "라이트룸", "테이블1" })]
    public void Reorganize_RootHandMadeFolder_IsKept(string root, string file, string[] kept)
    {
        var path = @"D:\A\2024\[2024-06-15] " + root;
        var reader = ReaderBelow(path);
        var group = Group(path + @"\" + file);

        Assert.Equal("일상", reader.Read(group));
        Assert.Equal(kept, reader.KeptFolders(group));
    }

    [Fact]
    public void Reorganize_NoNameFromAbove_RootStaysOutOfKept()
    {
        // 이름이 고른 폴더 위에서 온 것이 아니면 고른 폴더 자체는 담지 않는다.
        var reader = ReaderBelow(@"D:\A\모음\라이트룸");

        Assert.Empty(reader.KeptFolders(Group(@"D:\A\모음\라이트룸\a.jpg")));
        Assert.Equal(["테이블1"], reader.KeptFolders(Group(@"D:\A\모음\라이트룸\테이블1\a.jpg")));
    }

    [Fact]
    public void Reorganize_RootIsNamePiece_ReadsWholeName()
    {
        var root = @"D:\A\[2025-01-01] 가족여행\제주";
        var group = Group(root + @"\라이트룸\a.jpg");
        var reader = ReaderBelow(root);

        Assert.Equal(@"가족여행\제주", reader.Read(group));
        Assert.Equal(["라이트룸"], reader.KeptFolders(group));
    }

    [Fact]
    public void Reorganize_DoesNotLookAboveArchiveRoot()
    {
        var root = @"D:\[2024-06-15] 보관\모음";

        Assert.Null(ReaderBelow(root, archive: @"D:\[2024-06-15] 보관").Read(Group(root + @"\a.jpg")));
        Assert.Null(ReaderBelow(root, archive: @"D:\[2024-06-15] 보관\모음").Read(Group(root + @"\a.jpg")));
        Assert.Null(ReaderBelow(root, archive: @"E:\다른").Read(Group(root + @"\a.jpg")));
    }

    [Fact]
    public void Reorganize_ClassifierDoesNotLookAboveRoot()
    {
        // 위쪽의 형식에 맞지 않는 폴더는 판별기가 보지 않는다.
        var root = @"D:\A\데이트\다운로드";

        Assert.Null(ReaderBelow(root, useFolderNames: true).Read(Group(root + @"\a.jpg")));
    }

    [Fact]
    public void Reorganize_FormatBelowRoot_NearestWins()
    {
        var root = @"D:\A\[2024-06-15] 일상\모음";

        Assert.Equal("출사", ReaderBelow(root).Read(Group(root + @"\[2024-06-16] 출사\a.jpg")));
    }

    [Fact]
    public void Absorb_DoesNotLookAboveRoot_EvenWithArchiveRoot()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Absorb, false, [@"D:\A\[2024-06-15] 일상\모음"], WithZfc, archiveRoot: @"D:\A");

        Assert.Null(reader.Read(Group(@"D:\A\[2024-06-15] 일상\모음\a.jpg")));
    }

    [Fact]
    public void Reorganize_ArchiveIsDriveRoot_ReadsAboveRoot()
    {
        var root = @"F:\[2024-06-15] 일상\Zfc";

        Assert.Equal("일상", ReaderBelow(root, archive: @"F:\").Read(Group(root + @"\a.jpg")));
    }

    // ------------------------------------------------------------------
    // 프리셋이 만드는 이름
    // ------------------------------------------------------------------

    private static readonly Preset Owned = new("테스트", TimeOnly.MinValue,
        [
            new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new LevelTemplate("판단 필요"), new LevelTemplate("{사유}")]),
            new LayoutBranch("기본", BranchCondition.Always,
            [
                new LevelTemplate("사진첩"),
                new LevelTemplate("[{날짜}] {이벤트}", EmptyValue.Replace, "이름 없음"),
                new LevelTemplate("{포맷}"),
            ]),
        ],
        [],
        new Dictionary<string, IReadOnlyList<string>> { ["기타 이미지"] = [".tif"], ["보정"] = [".psd"] });

    [Theory]
    [InlineData("판단 필요")] // 고정 단계
    [InlineData("사진첩")]
    [InlineData("사진 첩")] // 정규화해서 비교
    [InlineData("촬영일 없음")] // 사유
    [InlineData("기기 모름")]
    [InlineData("짝 없는 사이드카")]
    [InlineData("촬영일 없음·기기 모름")] // 사유 묶음
    [InlineData("기기 모름·짝 없는 사이드카")]
    [InlineData("촬영일 없음·기기 모름·짝 없는 사이드카")]
    [InlineData("기기 모름·촬영일 없음")] // 순서는 가리지 않는다
    [InlineData("기타 이미지")] // 포맷 묶음 이름
    [InlineData("기타이미지")]
    [InlineData("보정")] // 뜻 있는 폴더여도 프리셋이 만든 이름이면 유지하지 않는다
    [InlineData("이름 없음")] // 빈 값 대신 쓰는 글자
    public void KeptFolders_DropsPresetOwnedNames(string folder)
    {
        var group = Group($@"D:\A\{folder}\a.jpg");

        Assert.Empty(new EventNameReader(Owned, JobKind.Reorganize, false, [@"D:\A"], Plain).KeptFolders(group));
    }

    [Theory]
    [InlineData("촬영일 없음·모음")] // 사유가 아닌 조각이 있으면 프리셋이 만든 이름이 아니다
    [InlineData("모음·기기 모름")]
    [InlineData("촬영일 없음·")]
    public void KeptFolders_ReasonMixedWithOtherPiece_IsNotOwned(string folder)
    {
        var group = Group($@"D:\A\{folder}\a.jpg");

        Assert.Equal([folder], new EventNameReader(Owned, JobKind.Reorganize, false, [@"D:\A"], Plain).KeptFolders(group));
    }

    [Theory]
    [InlineData("사진첩")]
    [InlineData("기타 이미지")]
    [InlineData("촬영일 없음")]
    [InlineData("이름 없음")]
    public void Read_DoesNotPickPresetOwnedNames(string folder)
    {
        var group = Group($@"D:\Downloads\{folder}\a.jpg");

        Assert.Null(new EventNameReader(Owned, JobKind.Reorganize, true, [@"D:\Downloads"], Plain).Read(group));
    }

    [Fact]
    public void Read_SkipsPresetOwned_AndPicksFartherFolder()
    {
        var group = Group(@"D:\A\데이트\기타 이미지\a.tif", new DateTime(2025, 1, 1, 10, 0, 0));
        var reader = new EventNameReader(Owned, JobKind.Reorganize, true, [@"D:\A"], Plain);

        Assert.Equal("데이트", reader.Read(group));
        Assert.Empty(reader.KeptFolders(group));
    }

    // ------------------------------------------------------------------
    // 이벤트 토큰이 없는 분기(판단 필요)
    // ------------------------------------------------------------------

    private static readonly DateTime Day = new(2025, 1, 1, 10, 0, 0);

    [Fact]
    public void KeptFolders_BranchWithoutEvent_PiecesAfterFirstAreKept()
    {
        var reader = new EventNameReader(Owned, JobKind.Reorganize, false, [@"D:\A"], Plain);
        var path = @"D:\A\[2025-01-01] 일상\테이블1\라이트룸\n.jpg";

        Assert.Equal(@"일상\테이블1", reader.Read(Group(path)));
        Assert.Equal(["테이블1", "라이트룸"], reader.KeptFolders(Group(path))); // 판단 필요: 분기에 이름 자리가 없다
        Assert.Equal(["라이트룸"], reader.KeptFolders(Group(path, Day))); // 날짜가 있으면 이름이 그대로 쓰인다
    }

    [Fact]
    public void KeptFolders_BranchWithoutEvent_ClassifierNameDoesNotCut()
    {
        // 이름이 쓰이지 않는 분기에서 판별기가 고른 폴더 위를 버리면 다음 재정리에서 다른 폴더를 골라 파일이 또 옮겨진다.
        var reader = new EventNameReader(Owned, JobKind.Reorganize, true, [@"D:\A"], Plain);

        Assert.Equal(["가", "나"], reader.KeptFolders(Group(@"D:\A\가\나\n.jpg")));
        Assert.Empty(reader.KeptFolders(Group(@"D:\A\가\나\n.jpg", Day)));
    }

    [Fact]
    public void KeptFolders_ReviewBranchWithEvent_PiecesStayInName()
    {
        var preset = new Preset("테스트", TimeOnly.MinValue,
        [
            new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new LevelTemplate("판단 필요"), new LevelTemplate("{이벤트}")]),
            new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("[{날짜}] {이벤트}")]),
        ], [], new Dictionary<string, IReadOnlyList<string>>());
        var reader = new EventNameReader(preset, JobKind.Reorganize, false, [@"D:\A"], Plain);

        Assert.Equal(["라이트룸"], reader.KeptFolders(Group(@"D:\A\[2025-01-01] 일상\테이블1\라이트룸\n.jpg")));
    }

    [Fact]
    public void KeptFolders_OtherNamesStay()
    {
        var group = Group(@"D:\A\사진첩\모음\기타 이미지\라이트룸\a.jpg");

        Assert.Equal(["모음", "라이트룸"], new EventNameReader(Owned, JobKind.Reorganize, false, [@"D:\A"], Plain).KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_LiteralInsideTokenLevel_IsNotOwned()
    {
        // "날짜_{이벤트}"의 글자는 한 단계 전체가 아니다.
        var group = Group(@"D:\A\날짜\a.jpg");

        Assert.Equal(["날짜"], new EventNameReader(PresetOf("{연도}", "날짜_{이벤트}"), JobKind.Absorb, false, [@"D:\A"], Plain).KeptFolders(group));
    }

    [Fact]
    public void KeptFolders_FormatGroupName_OwnedOnlyWhenPresetUsesFormat()
    {
        var withoutFormat = Owned with
        {
            Branches = [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}"), new LevelTemplate("{날짜} {이벤트}")])],
        };
        var group = Group(@"D:\A\기타 이미지\보정\a.jpg");
        var reader = new EventNameReader(withoutFormat, JobKind.Reorganize, false, [@"D:\A"], Plain);

        Assert.Equal(["기타 이미지", "보정"], reader.KeptFolders(group));
    }

    [Fact]
    public void Read_FormatGroupName_CandidateWhenPresetDoesNotUseFormat()
    {
        var withoutFormat = Owned with
        {
            Branches = [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}"), new LevelTemplate("{날짜} {이벤트}")])],
        };

        Assert.Equal("기타 이미지", new EventNameReader(withoutFormat, JobKind.Reorganize, true, [@"D:\Downloads"], Plain)
            .Read(Group(@"D:\Downloads\기타 이미지\a.jpg")));
    }

    [Fact]
    public void KeptFolders_ExtensionToken_DropsOwnExtensionOnly()
    {
        var reader = new EventNameReader(PresetOf("{연도}", "{확장자}"), JobKind.Reorganize, false, [@"D:\A"], Plain);

        Assert.Empty(reader.KeptFolders(Group(@"D:\A\AVIX\g.avix")));
        Assert.Equal(["AVIX"], reader.KeptFolders(Group(@"D:\A\AVIX\g.avi")));
    }

    [Fact]
    public void KeptFolders_NoExtensionToken_ExtensionNameStays()
    {
        var reader = new EventNameReader(PresetOf("{연도}"), JobKind.Absorb, false, [@"D:\A"], Plain);

        Assert.Equal(["AVIX"], reader.KeptFolders(Group(@"D:\A\AVIX\g.avix")));
    }

    [Fact]
    public void Read_ExtensionToken_DoesNotPickOwnExtension()
    {
        var preset = PresetOf("{연도}", "{날짜} {이벤트}", "{확장자}");

        Assert.Null(new EventNameReader(preset, JobKind.Reorganize, true, [@"D:\Downloads"], Plain).Read(Group(@"D:\Downloads\AVIX\g.avix")));
    }

    // ------------------------------------------------------------------
    // 이름의 _ 나누기
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true, @"가족여행\제주 바닷가")]
    [InlineData(false, "가족여행_제주 바닷가")]
    public void SplitUnderscores_ReplacesInReadName(bool split, string expected)
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\A"], Plain, split);

        Assert.Equal(expected, reader.Read(Group(@"D:\A\가족여행_제주 바닷가\a.jpg")));
    }

    [Fact]
    public void SplitUnderscores_FormatName_TrimsEnds()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Plain, splitUnderscores: true);

        Assert.Equal(@"가\나", reader.Read(Group(@"D:\A\[2025-01-01] _가_나_\a.jpg")));
    }

    [Theory]
    [InlineData("가족여행__제주", @"가족여행\제주")]
    [InlineData("a___b_c", @"a\b\c")]
    [InlineData("__a__", "a")]
    public void SplitUnderscores_CollapsesRuns(string folderName, string expected)
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Plain, splitUnderscores: true);

        Assert.Equal(expected, reader.Read(Group($@"D:\A\[2025-01-01] {folderName}\a.jpg")));
    }

    [Fact]
    public void KeptFolders_NameEmptiedByTrimming_StillCutsAtNamedFolder()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Plain, splitUnderscores: true);
        var group = Group(@"D:\A\제주도\[2025-01-01] ___\라이트룸\a.jpg");

        Assert.Null(reader.Read(group));
        Assert.Equal(["라이트룸"], reader.KeptFolders(group));
    }

    [Fact]
    public void SplitUnderscores_OnlyUnderscores_Null()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Plain, splitUnderscores: true);

        Assert.Null(reader.Read(Group(@"D:\A\[2025-01-01] ___\a.jpg")));
    }

    [Fact]
    public void Reorganize_FormatName_InIgnoredFile_IsStillRead()
    {
        var fs = new FakeFileSystem();
        const string ignoredPath = @"C:\data\ignored-folder-names.txt";
        IgnoredFolderNames.Add(fs, ignoredPath, "데이트");
        var context = new FolderNameContext(DeviceVocabulary.Empty, IgnoredFolderNames.Load(fs, ignoredPath));
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], context);

        // 제외 목록은 판별기가 읽는 이름에만 적용된다. 형식에서 읽은 이름은 그대로 남는다.
        Assert.Equal("데이트", reader.Read(Group(@"D:\A\2025\[2025-01-01] 데이트\Z8\a.jpg")));
        Assert.Equal("여행", reader.Read(Group(@"D:\A\2025\[2025-01-02] 여행\Z8\b.jpg")));
    }

    [Fact]
    public void Reader_RepeatedFolderNames_GiveSameResults()
    {
        // 같은 폴더 이름의 판별은 한 번만 하지만 결과는 파일마다 따로 할 때와 같아야 한다.
        var group = Group(@"D:\A\[2025-01-01] 가족여행\제주\Zfc\라이트룸\테이블1\a.jpg");
        var named = Group(@"D:\A\모음\데이트\Zfc\b.jpg");
        var reader = ReaderOf(JobKind.Reorganize, true, @"D:\A");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(@"가족여행\제주", reader.Read(group));
            Assert.Equal(["라이트룸", "테이블1"], reader.KeptFolders(group));
            Assert.True(reader.IsFormatName(group));
            Assert.Equal("데이트", reader.Read(named));
            Assert.Empty(reader.KeptFolders(named));
            Assert.False(reader.IsFormatName(named));
        }

        Assert.Equal(FolderKind.Disposable, FolderNameClassifier.KindOf("Zfc", WithZfc));
        Assert.Equal(FolderKind.Meaningful, FolderNameClassifier.KindOf("라이트룸", WithZfc));
        Assert.Equal("테이블1", FolderNameClassifier.Classify("테이블1", WithZfc)?.Name);
        Assert.Equal("데이트", FolderNameClassifier.PickName(["Zfc", "데이트", "모음"], WithZfc));
    }

    [Fact]
    public void Reader_NewInstance_SeesNewContext()
    {
        // 기억은 판독기 하나에만 있다. 이름을 제외한 뒤 다시 묶을 때 만드는 새 판독기는 새 목록을 따른다.
        var group = Group(@"D:\Downloads\데이트\a.jpg");

        Assert.Equal("데이트", new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\Downloads"], Plain).Read(group));
        Assert.Null(new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\Downloads"], Excluding("데이트")).Read(group));
    }

    private static FolderNameContext Excluding(string name)
    {
        var fs = new FakeFileSystem();
        const string ignoredPath = @"C:\data\ignored-folder-names.txt";
        IgnoredFolderNames.Add(fs, ignoredPath, name);
        return new FolderNameContext(DeviceVocabulary.Empty, IgnoredFolderNames.Load(fs, ignoredPath));
    }

    [Fact]
    public void Reorganize_ExcludedClassifierName_IsNotNameButFolderStays()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\A"], Excluding("테이블1"));
        var group = Group(@"D:\A\데이트\테이블1\a.jpg");

        Assert.Equal("데이트", reader.Read(group));
        Assert.Equal(["테이블1"], reader.KeptFolders(group));
    }

    [Fact]
    public void Reorganize_ExcludedWordBelowFormatFolder_IsKeptNotPiece()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Excluding("테이블1"));
        var group = Group(@"D:\A\[2025-01-01] 데이트\테이블1\a.jpg");

        Assert.Equal("데이트", reader.Read(group));
        Assert.Equal(["테이블1"], reader.KeptFolders(group));
    }

    [Fact]
    public void IsFormatName_TrueOnlyForFormatRead()
    {
        var reorganize = new EventNameReader(DatedEvent, JobKind.Reorganize, true, [@"D:\A"], Plain);
        var absorb = new EventNameReader(DatedEvent, JobKind.Absorb, true, [@"D:\A"], Plain);
        var format = Group(@"D:\A\[2025-01-01] 데이트\a.jpg");
        var classifier = Group(@"D:\A\데이트\a.jpg");
        var nameless = Group(@"D:\A\DCIM\a.jpg");

        Assert.True(reorganize.IsFormatName(format));
        Assert.False(reorganize.IsFormatName(classifier));
        Assert.Equal("데이트", reorganize.Read(classifier));
        Assert.False(absorb.IsFormatName(format));
        Assert.False(new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\Downloads"], Plain).IsFormatName(Group(@"D:\Downloads\a.jpg")));
        Assert.False(reorganize.IsFormatName(nameless));
    }

    [Fact]
    public void IsFormatName_FalseWhenTrimmingEmptiesName()
    {
        var reader = new EventNameReader(DatedEvent, JobKind.Reorganize, false, [@"D:\A"], Plain, splitUnderscores: true);

        Assert.False(reader.IsFormatName(Group(@"D:\A\[2025-01-01] ___\a.jpg")));
    }
}
