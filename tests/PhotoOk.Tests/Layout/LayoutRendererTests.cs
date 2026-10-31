using PhotoOk.Core.Layout;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Tests.Layout;

public class LayoutRendererTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);

    private static FileGroup Group(string path, DateTime? capture = null, string? make = null, string? model = null,
        DateTime? lastWriteUtc = null, MediaKind kind = MediaKind.Image) =>
        new(new PhotoInfo(path, 100, lastWriteUtc ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            kind, capture, make, model, null), []);

    private static Session SessionOf(FileGroup group, DateOnly? start = null, DateOnly? end = null)
    {
        var day = DateOnly.FromDateTime(Oct14);
        return new Session((start ?? day).ToString("yyyy-MM-dd"), start ?? day, end ?? day, [group]);
    }

    private static string Render(Preset preset, FileGroup group, IReadOnlyList<ReviewReason>? reasons = null,
        Session? session = null, string? eventName = null) =>
        new LayoutRenderer(preset).RenderDirectory(
            new LayoutInput(session ?? SessionOf(group), group, reasons ?? [], Utc, eventName));

    private static Preset OneBranch(params LevelTemplate[] levels) =>
        new("t", TimeOnly.MinValue, [new LayoutBranch("기본", BranchCondition.Always, levels)], [],
            BuiltInPresets.DateDeviceFormat.FormatGroups);

    [Fact]
    public void Render_DateDeviceFormat()
    {
        var group = Group(@"D:\S\a.RAF", Oct14, "FUJIFILM", "X100VI");

        Assert.Equal(@"2026\2026-10-14\X100VI\RAW", Render(BuiltInPresets.DateDeviceFormat, group));
    }

    [Fact]
    public void Render_OmitsLevel_WhenAllTokensEmpty()
    {
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"2026\2026-10-14\JPG", Render(BuiltInPresets.DateDeviceFormat, group));
    }

    [Fact]
    public void Render_ReplacesLevel_WhenConfigured()
    {
        var preset = OneBranch(new LevelTemplate("{연도}"), new LevelTemplate("{날짜}"),
            new LevelTemplate("{기기}", EmptyValue.Replace, "기기 모름"), new LevelTemplate("{포맷}"));
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"2026\2026-10-14\기기 모름\JPG", Render(preset, group));
    }

    [Fact]
    public void Render_EventName_AppendsToDate()
    {
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"2026\2026-10-14 결혼식", Render(BuiltInPresets.DateEvent, group, eventName: "결혼식"));
    }

    [Fact]
    public void Render_EventName_IsSanitized_AndBlankIsOmitted()
    {
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"2026\2026-10-14 a_b_c_", Render(BuiltInPresets.DateEvent, group, eventName: "a/b:c?"));
        Assert.Equal(@"2026\2026-10-14", Render(BuiltInPresets.DateEvent, group, eventName: "   "));
        Assert.Equal(@"2026\2026-10-14", Render(BuiltInPresets.DateEvent, group, eventName: null));
    }

    [Fact]
    public void Event_Backslash_SplitsAfterFirstPiece()
    {
        var preset = OneBranch(new LevelTemplate("[{기간}] {이벤트}"));
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"[2026-10-14] 가족여행\제주 바닷가", Render(preset, group, eventName: @"가족여행\제주 바닷가"));
        Assert.Equal(@"2026\2026-10-14 여행\제주", Render(BuiltInPresets.DateEvent, group, eventName: @"여행\제주"));
    }

    [Fact]
    public void Event_Backslash_ThenLevels()
    {
        var preset = OneBranch(new LevelTemplate("[{기간}] {이벤트}"), new LevelTemplate("{기기}"));
        var group = Group(@"D:\S\a.jpg", Oct14, "FUJIFILM", "X100VI");

        Assert.Equal(@"[2026-10-14] 가족여행\제주 바닷가\X100VI", Render(preset, group, eventName: @"가족여행\제주 바닷가"));
    }

    [Fact]
    public void Event_Backslash_PiecesAreSanitized()
    {
        var preset = OneBranch(new LevelTemplate("[{기간}] {이벤트}"));
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"[2026-10-14] a_b\c_\NUL_", Render(preset, group, eventName: @"a/b\c?\NUL"));
        Assert.Equal(@"[2026-10-14] 가\나", Render(preset, group, eventName: @"가 \ 나 "));
    }

    [Fact]
    public void Event_Backslash_LiteralAfterTokenJoinsLastPiece()
    {
        var preset = OneBranch(new LevelTemplate("{이벤트} 사진"));
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"가\나 사진", Render(preset, group, eventName: @"가\나"));
    }

    [Fact]
    public void Event_Backslash_InNeedsReviewBranch_Splits()
    {
        var preset = new Preset("t", TimeOnly.MinValue,
        [
            new LayoutBranch("검토", BranchCondition.NeedsReview, [new LevelTemplate("판단 필요"), new LevelTemplate("{이벤트}")]),
            new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{날짜}")]),
        ], [], BuiltInPresets.DateDeviceFormat.FormatGroups);
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal(@"판단 필요\가\나", Render(preset, group, [ReviewReason.NoCaptureDate], eventName: @"가\나"));
        Assert.Equal(@"판단 필요\x", Render(preset, group, [ReviewReason.NoCaptureDate], eventName: @"..\..\x"));
        Assert.Equal(@"판단 필요", Render(preset, group, [ReviewReason.NoCaptureDate], eventName: @"\"));
    }

    [Theory]
    [InlineData(@"..\..\x", @"2026\2026-10-14 x")]
    [InlineData(@".\x", @"2026\2026-10-14 x")]
    [InlineData(@"a\\b", @"2026\2026-10-14 a\b")]
    [InlineData(@"\", @"2026\2026-10-14")]
    [InlineData(@"..", @"2026\2026-10-14")]
    [InlineData(@"x\..\y", @"2026\2026-10-14 x\y")]
    [InlineData(@"\a\", @"2026\2026-10-14 a")]
    public void Event_DotSegments_Dropped(string eventName, string expected)
    {
        var group = Group(@"D:\S\a.jpg", Oct14);

        var result = Render(BuiltInPresets.DateEvent, group, eventName: eventName);

        Assert.Equal(expected, result);
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain(@"\\", result);
        Assert.DoesNotContain(@"\.\", result);
        Assert.False(result.StartsWith('\\') || result.EndsWith('\\'));
    }

    [Fact]
    public void Render_ReviewBranch_IgnoresEventName()
    {
        var group = Group(@"D:\Card\DCIM\a.jpg", null, null, null, new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = Render(BuiltInPresets.DateEvent, group, [ReviewReason.NoCaptureDate], eventName: "결혼식");

        Assert.Equal(@"판단 필요\촬영일 없음\DCIM", result);
    }

    [Fact]
    public void Render_BracketPeriodAndEvent()
    {
        var preset = OneBranch(new LevelTemplate("[{기간}] {이벤트}"));
        var group = Group(@"D:\S\a.jpg", Oct14);

        Assert.Equal("[2026-10-14]", Render(preset, group));
        Assert.Equal("[2026-10-14] 일상", Render(preset, group, eventName: "일상"));
    }

    [Fact]
    public void Render_UsesReviewBranch_WhenReasonsExist()
    {
        var group = Group(@"D:\Card\DCIM\a.jpg", null, null, null, new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));

        var result = Render(BuiltInPresets.DateDeviceFormat, group, [ReviewReason.NoCaptureDate]);

        Assert.Equal(@"판단 필요\촬영일 없음\DCIM", result);
    }

    [Fact]
    public void Render_JoinsMultipleReasonLabels()
    {
        var group = Group(@"D:\Card\DCIM\a.jpg");

        var result = Render(BuiltInPresets.DateOnly, group, [ReviewReason.NoCaptureDate, ReviewReason.UnknownDevice]);

        Assert.Equal(@"판단 필요\촬영일 없음·기기 모름\DCIM", result);
    }

    [Fact]
    public void SelectBranch_PicksFirstMatching()
    {
        var renderer = new LayoutRenderer(BuiltInPresets.DateOnly);

        Assert.Equal("기본", renderer.SelectBranch([]).Name);
        Assert.Equal("판단 필요", renderer.SelectBranch([ReviewReason.OrphanSidecar]).Name);
    }

    [Fact]
    public void Render_FormatLookup_IsCaseInsensitive_AndExtensionTokenIsUpperCase()
    {
        var preset = OneBranch(new LevelTemplate("{포맷}"), new LevelTemplate("{확장자}"));

        Assert.Equal(@"RAW\RAF", Render(preset, Group(@"D:\S\A.raf", Oct14)));
        Assert.Equal(@"JPG\JPEG", Render(preset, Group(@"D:\S\A.JpEg", Oct14)));
    }

    [Fact]
    public void Render_UnknownFormat_IsEmptyAndOmitted()
    {
        var preset = OneBranch(new LevelTemplate("{연도}"), new LevelTemplate("{포맷}"));

        Assert.Equal("2026", Render(preset, Group(@"D:\S\a.xyz", Oct14)));
    }

    [Fact]
    public void Render_DateTokensAndFormats()
    {
        var preset = OneBranch(new LevelTemplate("{연도}-{월}-{일}"), new LevelTemplate("{날짜:yyyyMMdd}"),
            new LevelTemplate("{월:M월}"));

        Assert.Equal(@"2026-10-14\20261014\10월", Render(preset, Group(@"D:\S\a.jpg", Oct14)));
    }

    [Fact]
    public void Render_UsesDayStart()
    {
        var preset = new Preset("t", new TimeOnly(4, 0),
            [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{날짜}")])], [], new Dictionary<string, IReadOnlyList<string>>());
        var group = Group(@"D:\S\a.jpg", new DateTime(2026, 10, 15, 2, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal("2026-10-14", Render(preset, group, session: SessionOf(group)));
    }

    [Fact]
    public void Render_OriginalFolder_RenderedByDefault()
    {
        var group = Group(@"D:\S\라이트룸\a.jpg", Oct14);

        Assert.Equal(@"판단 필요\라이트룸", Render(OneBranch(new LevelTemplate("판단 필요"), new LevelTemplate("{원래폴더}")), group));
    }

    [Fact]
    public void Render_OmitOriginalFolder_OmitsLevel()
    {
        var group = Group(@"D:\S\라이트룸\a.jpg", Oct14);
        var preset = OneBranch(new LevelTemplate("판단 필요"), new LevelTemplate("{원래폴더}"));

        var result = new LayoutRenderer(preset).RenderDirectory(
            new LayoutInput(SessionOf(group), group, [], Utc, OmitOriginalFolder: true));

        Assert.Equal("판단 필요", result);
    }

    [Fact]
    public void Render_OmitOriginalFolder_KeepsOtherTokensInLevel()
    {
        var group = Group(@"D:\S\라이트룸\a.jpg", Oct14);
        var preset = OneBranch(new LevelTemplate("{날짜} {원래폴더}"));

        var result = new LayoutRenderer(preset).RenderDirectory(
            new LayoutInput(SessionOf(group), group, [], Utc, OmitOriginalFolder: true));

        Assert.Equal("2026-10-14", result);
    }

    [Fact]
    public void Render_FallsBackToLastWriteTime_InGivenZone()
    {
        var kst = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var group = Group(@"D:\S\a.jpg", null, lastWriteUtc: new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc));
        var preset = OneBranch(new LevelTemplate("{날짜}"));

        var result = new LayoutRenderer(preset).RenderDirectory(new LayoutInput(SessionOf(group), group, [], kst));

        Assert.Equal("2026-10-15", result);
    }

    [Fact]
    public void Render_PeriodToken_UsesSessionRange()
    {
        var preset = OneBranch(new LevelTemplate("[{기간}] 여행"));
        var group = Group(@"D:\S\a.jpg", Oct14);
        var session = SessionOf(group, new DateOnly(2026, 7, 8), new DateOnly(2026, 7, 9));

        Assert.Equal("[2026-07-08~07-09] 여행", Render(preset, group, session: session));
    }

    [Fact]
    public void Render_DeviceUsesAliasesFromPreset()
    {
        var preset = new Preset("t", TimeOnly.MinValue,
            [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{기기}"), new LevelTemplate("{제조사}")])],
            [new DeviceAlias("후지", Model: "X100VI")], new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal(@"후지\FUJIFILM", Render(preset, Group(@"D:\S\a.jpg", Oct14, "FUJIFILM", "X100VI")));
    }

    [Fact]
    public void Render_LiteralOnlyLevel_IsAlwaysKept_AndLevelWithOnlyEmptyTokensIsOmittedEvenWithLiterals()
    {
        var preset = OneBranch(new LevelTemplate("기타"), new LevelTemplate("카메라-{모델}"), new LevelTemplate("{모델}"));

        Assert.Equal("기타", Render(preset, Group(@"D:\S\a.jpg", Oct14)));
    }

    [Fact]
    public void Render_SanitizesRenderedValuesAndReplacement()
    {
        var preset = OneBranch(new LevelTemplate("{모델}", EmptyValue.Replace, "a/b"), new LevelTemplate("{제조사}"));

        Assert.Equal(@"X_1\_", Render(preset, Group(@"D:\S\a.jpg", Oct14, "..", "X/1")));
        Assert.Equal(@"a_b", Render(preset, Group(@"D:\S\a.jpg", Oct14)));
    }

    [Fact]
    public void Render_OriginalFolder_ForSidecarPrimaryWithoutFormat()
    {
        var group = Group(@"D:\Card\DCIM\100\IMG_1.RW2.xmp", null, kind: MediaKind.Sidecar);

        var result = Render(BuiltInPresets.DateOnly, group, [ReviewReason.OrphanSidecar]);

        Assert.Equal(@"판단 필요\짝 없는 사이드카\100", result);
    }
}
