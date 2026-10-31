using System.IO;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Planning;

public class ReorganizePlanTests
{
    private const string Archive = @"D:\Archive";
    private const string Day14 = @"D:\Archive\2026\2026-10-14";
    private const string Day15 = @"D:\Archive\2026\2026-10-15";
    private const string Old = @"D:\Archive\모음";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FileTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Roots = [Archive];

    private readonly FakeFileSystem _fs = new();

    public ReorganizePlanTests()
    {
        _fs.AddDirectory(Archive);
    }

    private PhotoInfo Add(string path, byte[] content, DateTime? capture, string? model = null)
    {
        _fs.AddFile(path, content);
        return new PhotoInfo(path, content.Length, FileTime, FileKinds.Default.Classify(path), capture, null, model, null);
    }

    private static FileGroup Group(PhotoInfo primary, params PhotoInfo[] companions) => new(primary, companions);

    private Plan Create(Preset preset, FileGroup[] groups, JobKind kind = JobKind.Reorganize, TransferMode mode = TransferMode.Move,
        bool keepReview = false, SubfolderPolicy subfolders = SubfolderPolicy.Keep,
        Func<FileGroup, IReadOnlyList<string>>? keptOf = null, IReadOnlyList<string>? roots = null,
        IReadOnlyDictionary<string, string>? names = null, Func<FileGroup, string?>? nameOf = null)
    {
        var sessions = SessionGrouper.GroupByDay(groups, preset.DayStart, TimeZoneInfo.Utc, nameOf);
        return new Planner(_fs, new XxHasher(_fs)).Create(new PlanRequest(sessions, preset, Archive, mode, TimeZoneInfo.Utc, names,
            kind, roots ?? Roots, null, keepReview, subfolders, keptOf));
    }

    /// <summary>주 파일 경로별로 유지할 폴더를 돌려준다. 없는 경로는 빈 목록이다.</summary>
    private static Func<FileGroup, IReadOnlyList<string>> Kept(params (string Path, string[] Folders)[] entries)
    {
        var map = entries.ToDictionary(e => e.Path, e => (IReadOnlyList<string>)e.Folders, StringComparer.OrdinalIgnoreCase);
        return group => map.GetValueOrDefault(group.Primary.Path) ?? [];
    }

    private static List<PlannedOperation> FileOps(Plan plan) =>
        plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory).ToList();

    private static List<(OperationKind, string?, string)> Shape(IEnumerable<PlannedOperation> ops) =>
        ops.Select(o => (o.Kind, o.Source, o.Destination)).ToList();

    [Fact]
    public void Reorganize_MovesOnlyMisplaced()
    {
        var inPlace = Add($@"{Day14}\a.jpg", [1], Oct14);
        var misplaced = Add($@"{Old}\b.jpg", [2], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(inPlace), Group(misplaced)]);

        Assert.Equal([(OperationKind.Move, $@"{Old}\b.jpg", $@"{Day14}\b.jpg")], Shape(plan.Operations));
        Assert.Equal([new PlannedSkip($@"{Day14}\a.jpg", $@"{Day14}\a.jpg", SkipReason.AlreadyInPlace)], plan.Skipped);
    }

    [Fact]
    public void Reorganize_ReviewFileStaysInPlace()
    {
        var unknown = Add($@"{Old}\x.jpg", [1], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(unknown)], keepReview: true);

        Assert.Empty(plan.Operations);
        Assert.Equal([new PlannedSkip($@"{Old}\x.jpg", $@"{Old}\x.jpg", SkipReason.ReviewKeptInPlace)], plan.Skipped);
    }

    [Fact]
    public void Reorganize_ReviewOptionOff_GoesToReviewBranch()
    {
        var unknown = Add($@"{Old}\x.jpg", [1], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(unknown)], keepReview: false);

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\모음\x.jpg", op.Destination);
        Assert.Equal([ReviewReason.NoCaptureDate], op.Reasons);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Reorganize_ReviewGroupKeepsCompanions()
    {
        var raw = Add($@"{Old}\IMG.NEF", [1], null);
        var xmp = Add($@"{Old}\IMG.xmp", [2], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(raw, xmp)], keepReview: true);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [
                new PlannedSkip($@"{Old}\IMG.NEF", $@"{Old}\IMG.NEF", SkipReason.ReviewKeptInPlace),
                new PlannedSkip($@"{Old}\IMG.xmp", $@"{Old}\IMG.xmp", SkipReason.ReviewKeptInPlace),
            ],
            plan.Skipped);
    }

    [Fact]
    public void Subfolders_Keep_AppendsBelowPresetFolders()
    {
        var source = $@"{Old}\Zfc\라이트룸\테이블1\a.jpg";
        var photo = Add(source, [1], Oct14, "Zfc");

        var plan = Create(BuiltInPresets.DateDeviceFormat, [Group(photo)], keptOf: Kept((source, ["라이트룸", "테이블1"])));

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.Equal($@"{Day14}\Zfc\JPG\라이트룸\테이블1\a.jpg", op.Destination);
        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.CreateDirectory
            && o.Destination == $@"{Day14}\Zfc\JPG\라이트룸\테이블1");
    }

    [Fact]
    public void Subfolders_Keep_AlreadyThere_StaysInPlace()
    {
        var source = $@"{Day14}\Zfc\JPG\라이트룸\a.jpg";
        var photo = Add(source, [1], Oct14, "Zfc");

        var plan = Create(BuiltInPresets.DateDeviceFormat, [Group(photo)], keptOf: Kept((source, ["라이트룸"])));

        Assert.Empty(plan.Operations);
        Assert.Equal([new PlannedSkip(source, source, SkipReason.AlreadyInPlace)], plan.Skipped);
    }

    [Fact]
    public void Subfolders_Keep_AppliesToReviewBranch()
    {
        var source = $@"{Old}\라이트룸\x.jpg";
        var photo = Add(source, [1], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], keptOf: Kept((source, ["라이트룸"])));

        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\라이트룸\x.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Subfolders_InPlace_SkipsFile()
    {
        var keptSource = $@"{Old}\라이트룸\a.jpg";
        var kept = Add(keptSource, [1], Oct14);
        var plain = Add($@"{Old}\b.jpg", [2], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(kept), Group(plain)], subfolders: SubfolderPolicy.InPlace,
            keptOf: Kept((keptSource, ["라이트룸"])));

        Assert.Equal([(OperationKind.Move, $@"{Old}\b.jpg", $@"{Day14}\b.jpg")], Shape(FileOps(plan)));
        Assert.Equal([new PlannedSkip(keptSource, keptSource, SkipReason.SubfolderKeptInPlace)], plan.Skipped);
    }

    [Fact]
    public void Subfolders_InPlace_KeepsWholeGroup()
    {
        var rawSource = $@"{Old}\라이트룸\IMG.NEF";
        var raw = Add(rawSource, [1], Oct14);
        var xmp = Add($@"{Old}\라이트룸\IMG.xmp", [2], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(raw, xmp)], subfolders: SubfolderPolicy.InPlace,
            keptOf: Kept((rawSource, ["라이트룸"])));

        Assert.Empty(plan.Operations);
        Assert.Equal([SkipReason.SubfolderKeptInPlace, SkipReason.SubfolderKeptInPlace], plan.Skipped.Select(s => s.Reason));
        Assert.All(plan.Skipped, s => Assert.Equal(s.Source, s.Destination));
    }

    [Fact]
    public void Subfolders_Merge_Ignores()
    {
        var source = $@"{Old}\Zfc\라이트룸\a.jpg";
        var photo = Add(source, [1], Oct14, "Zfc");

        var plan = Create(BuiltInPresets.DateDeviceFormat, [Group(photo)], subfolders: SubfolderPolicy.Merge,
            keptOf: Kept((source, ["라이트룸"])));

        Assert.Equal($@"{Day14}\Zfc\JPG\a.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Subfolders_NameConflictOnlyWithinKeptFolder()
    {
        var edited = $@"{Old}\라이트룸\a.jpg";
        var first = Add($@"{Old}\a.jpg", [1, 1], Oct14);
        var second = Add(edited, [2, 2], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(first), Group(second)], keptOf: Kept((edited, ["라이트룸"])));

        Assert.Equal(
            [$@"{Day14}\a.jpg", $@"{Day14}\라이트룸\a.jpg"],
            FileOps(plan).Select(o => o.Destination).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Subfolders_NameConflictWithinSameKeptFolder_GetsNumber()
    {
        var first = $@"{Old}\가\라이트룸\a.jpg";
        var second = $@"{Old}\나\라이트룸\a.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(first, [1, 1], Oct14)), Group(Add(second, [2, 2], Oct14))],
            keptOf: Kept((first, ["라이트룸"]), (second, ["라이트룸"])));

        Assert.Equal([$@"{Day14}\라이트룸\a.jpg", $@"{Day14}\라이트룸\a_1.jpg"], FileOps(plan).Select(o => o.Destination));
    }

    [Theory]
    [InlineData("..", "_")]
    [InlineData(".", "_")]
    [InlineData(@"..\..\x", ".._.._x")]
    [InlineData(@"C:\Windows", "C__Windows")]
    [InlineData("CON", "CON_")]
    [InlineData("끝점. ", "끝점")]
    public void Subfolders_KeptSegmentsAreSanitized(string segment, string expected)
    {
        var source = $@"{Old}\a.jpg";
        var photo = Add(source, [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], keptOf: Kept((source, [segment])));

        Assert.Equal($@"{Day14}\{expected}\a.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Reorganize_DestinationInUnselectedFolder_GetsNumberAndLeavesItAlone()
    {
        _fs.AddFile($@"{Day14}\a.jpg", [9, 9, 9]); // 고르지 않은 폴더의 파일: 스캔에 없다
        var photo = Add($@"{Old}\a.jpg", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], roots: [Old]);

        Assert.Equal([(OperationKind.Move, $@"{Old}\a.jpg", $@"{Day14}\a_1.jpg")], Shape(plan.Operations));
        Assert.DoesNotContain(plan.Operations, o => o.Source == $@"{Day14}\a.jpg");
        Assert.DoesNotContain(plan.Operations, o => o.Destination == $@"{Day14}\a.jpg");
    }

    [Fact]
    public void Reorganize_SameContentAtDestination_StaysWithoutOperation()
    {
        _fs.AddFile($@"{Day14}\a.jpg", [1, 2, 3]);
        var photo = Add($@"{Old}\a.jpg", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)]);

        Assert.Empty(plan.Operations);
        Assert.Equal([new PlannedSkip($@"{Old}\a.jpg", $@"{Day14}\a.jpg", SkipReason.DuplicateAtDestination)], plan.Skipped);
    }

    [Fact]
    public void Reorganize_Swap_UsesTemp()
    {
        var a = Add($@"{Day15}\P.JPG", [1, 1, 1], Oct14);
        var b = Add($@"{Day14}\P.JPG", [2, 2, 2], Oct15);

        var plan = Create(BuiltInPresets.DateOnly, [Group(a), Group(b)]);

        var tempA = $@"{Day15}\P.JPG.photook-tmp-op-000001";
        var tempB = $@"{Day14}\P.JPG.photook-tmp-op-000002";
        Assert.Equal(
            [
                (OperationKind.Move, $@"{Day15}\P.JPG", tempA),
                (OperationKind.Move, $@"{Day14}\P.JPG", tempB),
                (OperationKind.Move, tempA, $@"{Day14}\P.JPG"),
                (OperationKind.Move, tempB, $@"{Day15}\P.JPG"),
            ],
            Shape(plan.Operations));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Reorganize_OtherVolume_SkipsWithoutCopy()
    {
        var photo = Add(@"E:\연결\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)]);

        Assert.Empty(plan.Operations);
        Assert.Equal([new PlannedSkip(@"E:\연결\a.jpg", @"E:\연결\a.jpg", SkipReason.OtherVolume)], plan.Skipped);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("복사", StringComparison.Ordinal));
    }

    [Fact]
    public void Reorganize_OtherVolume_SkipsWholeGroupWhenCompanionIsElsewhere()
    {
        var raw = Add($@"{Old}\IMG.NEF", [1], Oct14);
        var xmp = Add(@"E:\연결\IMG.xmp", [2], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(raw, xmp)]);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [
                new PlannedSkip($@"{Old}\IMG.NEF", $@"{Old}\IMG.NEF", SkipReason.OtherVolume),
                new PlannedSkip(@"E:\연결\IMG.xmp", @"E:\연결\IMG.xmp", SkipReason.OtherVolume),
            ],
            plan.Skipped);
    }

    [Fact]
    public void Absorb_Move_AcrossDrives_StillForcedCopy()
    {
        var photo = Add(@"E:\흩어짐\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], JobKind.Absorb, roots: [@"E:\흩어짐"]);

        Assert.Equal([(OperationKind.Copy, @"E:\흩어짐\a.jpg", $@"{Day14}\a.jpg")], Shape(FileOps(plan)));
        Assert.Contains("다른 드라이브라 복사로 처리: 1개", plan.Notes);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Reorganize_FileKeptInPlaceBlocksDestination()
    {
        // 판단 필요 파일은 제자리에 남으므로, 그 자리로 오려는 다른 파일이 빈 자리로 보면 안 된다.
        var stays = Add($@"{Day14}\a.jpg", [9, 9, 9], null);
        var incoming = Add($@"{Old}\a.jpg", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(stays), Group(incoming)], keepReview: true);

        Assert.Equal([(OperationKind.Move, $@"{Old}\a.jpg", $@"{Day14}\a_1.jpg")], Shape(plan.Operations));
        Assert.Equal([new PlannedSkip($@"{Day14}\a.jpg", $@"{Day14}\a.jpg", SkipReason.ReviewKeptInPlace)], plan.Skipped);
    }

    [Fact]
    public void Reorganize_SubfolderKeptInPlaceBlocksDestination()
    {
        var staying = $@"{Day14}\a.jpg";
        var stays = Add(staying, [9, 9, 9], Oct14);
        var incoming = Add($@"{Old}\a.jpg", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(stays), Group(incoming)], subfolders: SubfolderPolicy.InPlace,
            keptOf: Kept((staying, ["라이트룸"])));

        Assert.Equal([(OperationKind.Move, $@"{Old}\a.jpg", $@"{Day14}\a_1.jpg")], Shape(plan.Operations));
        Assert.Equal([new PlannedSkip(staying, staying, SkipReason.SubfolderKeptInPlace)], plan.Skipped);
    }

    [Fact]
    public void Reorganize_SameContentAsKeptFile_LeavesBothWhereTheyAre()
    {
        var stays = Add($@"{Day14}\a.jpg", [1, 2, 3], null);
        var incoming = Add($@"{Old}\a.jpg", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(stays), Group(incoming)], keepReview: true);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [
                new PlannedSkip($@"{Day14}\a.jpg", $@"{Day14}\a.jpg", SkipReason.ReviewKeptInPlace),
                new PlannedSkip($@"{Old}\a.jpg", $@"{Day14}\a.jpg", SkipReason.DuplicateAtDestination),
            ],
            plan.Skipped);
    }

    [Fact]
    public void Reorganize_KeptFileMissingFromDisk_StillBlocksDestination()
    {
        // 스캔 뒤 디스크에서 보이지 않아도 제자리에 두기로 한 파일의 자리는 비어 있다고 보지 않는다.
        var stays = new PhotoInfo($@"{Day14}\a.jpg", 3, FileTime, MediaKind.Image, null, null, null, null);
        var incoming = Add($@"{Old}\a.jpg", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(stays), Group(incoming)], keepReview: true);

        Assert.Equal([(OperationKind.Move, $@"{Old}\a.jpg", $@"{Day14}\a_1.jpg")], Shape(FileOps(plan)));
    }

    [Fact]
    public void Reorganize_KeptFileNeverBecomesAnOperation()
    {
        // 제자리 파일이 이동 후보로 들어가면 임시 이름 단계에서 옮겨질 수 있다.
        var stays = Add($@"{Day14}\a.jpg", [9, 9, 9], null);
        var mover = Add($@"{Day15}\a.jpg", [1, 1, 1], Oct14);
        var other = Add($@"{Old}\a.jpg", [2, 2, 2], Oct15);

        var plan = Create(BuiltInPresets.DateOnly, [Group(stays), Group(mover), Group(other)], keepReview: true);

        Assert.DoesNotContain(plan.Operations, o => o.Source == $@"{Day14}\a.jpg");
        Assert.DoesNotContain(plan.Operations, o => o.Destination == $@"{Day14}\a.jpg");
        Assert.Equal($@"{Day14}\a_1.jpg", FileOps(plan).Last(o => o.Destination.StartsWith(Day14, StringComparison.Ordinal)).Destination);
    }

    [Fact]
    public void Reorganize_SetsCleanupRoots()
    {
        string[] roots = [Old, @"D:\Archive\다른"];
        var photo = Add($@"{Old}\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], roots: roots);

        Assert.Equal(roots, plan.CleanupRoots);
    }

    [Fact]
    public void Import_NoCleanupRoots()
    {
        var photo = Add(@"E:\DCIM\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], JobKind.Import, roots: [@"E:\DCIM"]);

        Assert.Null(plan.CleanupRoots);
    }

    [Fact]
    public void Absorb_Move_SetsCleanupRoots()
    {
        string[] roots = [@"D:\흩어짐"];
        var photo = Add(@"D:\흩어짐\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], JobKind.Absorb, TransferMode.Move, roots: roots);

        Assert.Equal(roots, plan.CleanupRoots);
    }

    [Fact]
    public void Absorb_Copy_NoCleanupRoots()
    {
        var photo = Add(@"D:\흩어짐\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], JobKind.Absorb, TransferMode.Copy, roots: [@"D:\흩어짐"]);

        Assert.Null(plan.CleanupRoots);
    }

    [Fact]
    public void Absorb_Subfolders_Apply()
    {
        var source = @"D:\흩어짐\라이트룸\a.jpg";

        var keep = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], Oct14))], JobKind.Absorb,
            keptOf: Kept((source, ["라이트룸"])));
        var inPlace = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], Oct14))], JobKind.Absorb,
            subfolders: SubfolderPolicy.InPlace, keptOf: Kept((source, ["라이트룸"])));

        Assert.Equal($@"{Day14}\라이트룸\a.jpg", Assert.Single(FileOps(keep)).Destination);
        Assert.Equal([new PlannedSkip(source, source, SkipReason.SubfolderKeptInPlace)], inPlace.Skipped);
    }

    [Theory]
    [InlineData(SubfolderPolicy.Keep)]
    [InlineData(SubfolderPolicy.InPlace)]
    public void Import_IgnoresKeptFolders(SubfolderPolicy policy)
    {
        var source = @"E:\DCIM\라이트룸\a.jpg";
        var photo = Add(source, [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], JobKind.Import, TransferMode.Copy, subfolders: policy,
            keptOf: Kept((source, ["라이트룸"])));

        Assert.Equal([(OperationKind.Copy, source, $@"{Day14}\a.jpg")], Shape(FileOps(plan)));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Reorganize_SameDayTwoNames_KeepsSeparateFolders()
    {
        var preset = new Preset("테스트", TimeOnly.MinValue,
            [new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}"), new LevelTemplate("[{날짜}] {이벤트}")])],
            [], new Dictionary<string, IReadOnlyList<string>>());
        var date = Add(@"D:\Archive\2026\[2026-10-14] 데이트\a.jpg", [1], Oct14);
        var outing = Add(@"D:\Archive\2026\[2026-10-14] 출사\b.jpg", [2], Oct14);
        var reader = new EventNameReader(preset, JobKind.Reorganize, false, Roots,
            new FolderNameContext(DeviceVocabulary.Empty, IgnoredFolderNames.Empty));
        var names = SessionGrouper.GroupByDay([Group(date), Group(outing)], preset.DayStart, TimeZoneInfo.Utc, reader.Read)
            .ToDictionary(s => s.Id, s => s.SuggestedEventName!);

        var plan = Create(preset, [Group(date), Group(outing)], names: names, nameOf: reader.Read, keptOf: reader.KeptFolders);

        Assert.Empty(plan.Operations);
        Assert.Equal([SkipReason.AlreadyInPlace, SkipReason.AlreadyInPlace], plan.Skipped.Select(s => s.Reason));
    }

    // ---- 다시 해도 그대로인가 ----

    [Fact]
    public void Subfolders_Keep_CurrentUnderRenderedFolder_StaysEvenWithoutKeptFolders()
    {
        var source = $@"{Day14}\DCIM\a.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], Oct14))]);

        Assert.Empty(plan.Operations);
        Assert.Equal([new PlannedSkip(source, source, SkipReason.AlreadyInPlace)], plan.Skipped);
    }

    [Fact]
    public void Subfolders_Keep_SiblingWithSamePrefix_IsNotUnderRenderedFolder()
    {
        var source = $@"{Day14} 데이트\a.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], Oct14))]);

        Assert.Equal($@"{Day14}\a.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Subfolders_Merge_CurrentUnderRenderedFolder_StillFlattens()
    {
        var source = $@"{Day14}\라이트룸\a.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], Oct14))], subfolders: SubfolderPolicy.Merge,
            keptOf: Kept((source, ["라이트룸"])));

        Assert.Equal($@"{Day14}\a.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Import_CurrentUnderRenderedFolder_BehavesAsBefore()
    {
        var source = $@"{Day14}\하위\a.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], Oct14))], JobKind.Import, TransferMode.Move);

        Assert.Equal([(OperationKind.Move, source, $@"{Day14}\a.jpg")], Shape(FileOps(plan)));
    }

    [Fact]
    public void Subfolders_Keep_NoKeptFolders_ReviewBranchRendersOriginalFolder()
    {
        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(@"D:\Archive\DCIM\x.jpg", [1], null))], keptOf: Kept());

        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\DCIM\x.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Subfolders_Merge_ReviewBranchRendersOriginalFolder()
    {
        var source = @"D:\Archive\라이트룸\테이블1\x.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], null))], subfolders: SubfolderPolicy.Merge,
            keptOf: Kept((source, ["라이트룸", "테이블1"])));

        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\테이블1\x.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public void Import_ReviewBranchRendersOriginalFolder_EvenWithKeptFolders()
    {
        var source = @"E:\DCIM\라이트룸\x.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(source, [1], null))], JobKind.Import, TransferMode.Copy,
            keptOf: Kept((source, ["라이트룸"])));

        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\라이트룸\x.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Theory]
    [InlineData(TransferMode.Copy)]
    [InlineData(TransferMode.Move)]
    public void Reorganize_AlwaysMoves(TransferMode mode)
    {
        var photo = Add($@"{Old}\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], mode: mode);

        Assert.Equal([(OperationKind.Move, $@"{Old}\a.jpg", $@"{Day14}\a.jpg")], Shape(FileOps(plan)));
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Subfolders_SegmentsCollidingAfterSanitizing_NoOverwrite()
    {
        var first = $@"{Old}\가\x.jpg";
        var second = $@"{Old}\나\x.jpg";

        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(first, [1, 1], Oct14)), Group(Add(second, [2, 2], Oct14))],
            keptOf: Kept((first, ["a:b"]), (second, ["a_b"])));

        Assert.Equal([$@"{Day14}\a_b\x.jpg", $@"{Day14}\a_b\x_1.jpg"], FileOps(plan).Select(o => o.Destination));
        Apply(plan);
        Assert.Equal([1, 1], _fs.ReadAllBytes($@"{Day14}\a_b\x.jpg"));
        Assert.Equal([2, 2], _fs.ReadAllBytes($@"{Day14}\a_b\x_1.jpg"));
    }

    [Fact]
    public void Reorganize_OnlyRawAlreadyAtDestination_NothingLost()
    {
        _fs.AddFile($@"{Day14}\IMG.NEF", [1, 2, 3]);
        var raw = Add($@"{Old}\IMG.NEF", [1, 2, 3], Oct14);
        var xmp = Add($@"{Old}\IMG.xmp", [5], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(raw, xmp)]);

        Assert.Equal([(OperationKind.Move, $@"{Old}\IMG.xmp", $@"{Day14}\IMG.xmp")], Shape(FileOps(plan)));
        Assert.Equal([new PlannedSkip($@"{Old}\IMG.NEF", $@"{Day14}\IMG.NEF", SkipReason.DuplicateAtDestination)], plan.Skipped);

        Apply(plan);

        Assert.Equal([1, 2, 3], _fs.ReadAllBytes($@"{Old}\IMG.NEF")); // 원본은 제자리에 남는다
        Assert.Equal([1, 2, 3], _fs.ReadAllBytes($@"{Day14}\IMG.NEF"));
        Assert.Equal([5], _fs.ReadAllBytes($@"{Day14}\IMG.xmp"));
    }

    private readonly Dictionary<string, (DateTime? Capture, string? Model)> _seeds = new(StringComparer.OrdinalIgnoreCase);

    private void Seed(string path, byte[] content, DateTime? capture, string? model = null)
    {
        _fs.AddFile(path, content);
        _seeds[path] = (capture, model);
        _reader.Set(path, capture, model: model);
    }

    // 계획을 가짜 파일 시스템에 그대로 적용한다. 사진 정보는 파일을 따라간다.
    private void Apply(Plan plan)
    {
        foreach (var op in plan.Operations)
        {
            switch (op.Kind)
            {
                case OperationKind.CreateDirectory:
                    _fs.CreateDirectory(op.Destination);
                    break;
                case OperationKind.Move:
                    Assert.False(_fs.FileExists(op.Destination), $"덮어쓰기: {op.Destination}");
                    _fs.Move(op.Source!, op.Destination);
                    if (_seeds.Remove(op.Source!, out var meta))
                    {
                        _seeds[op.Destination] = meta;
                        _reader.Set(op.Destination, meta.Capture, model: meta.Model);
                    }

                    break;
                default:
                    Assert.Fail($"재정리에 나오면 안 되는 작업: {op.Kind} {op.Source}");
                    break;
            }
        }
    }

    private List<string> AllFiles() =>
        _fs.EnumerateFiles(Archive, true).Order(StringComparer.Ordinal).ToList();

    private List<string> Contents() =>
        AllFiles().Select(path => Convert.ToHexString(_fs.ReadAllBytes(path))).Order(StringComparer.Ordinal).ToList();

    // 카드에 채워지는 이름을 그대로 쓴다. 해시 기억이 넘어가지 않도록 차례마다 새 흐름을 쓴다.
    private async Task<Plan> PlanWithSuggestedNamesAsync(ImportSettings settings)
    {
        var workflow = Workflow();
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        var names = scan.Sessions.Where(s => s.SuggestedEventName is not null).ToDictionary(s => s.Id, s => s.SuggestedEventName!);
        return workflow.BuildPlan(settings, scan, names).Plan;
    }

    // 첫 번째만 파일을 옮길 수 있다. 두 번째와 세 번째는 작업이 없어야 하고, 어느 차례에도 파일 내용이 그대로여야 한다.
    private async Task AssertStableAsync(ImportSettings settings)
    {
        var before = Contents();
        for (var pass = 1; pass <= 3; pass++)
        {
            var plan = await PlanWithSuggestedNamesAsync(settings);
            if (pass > 1)
            {
                Assert.Empty(Shape(plan.Operations));
            }

            Apply(plan);
            Assert.Equal(before, Contents());
        }
    }

    private static LevelTemplate L(string template) => new(template);

    private static Preset WithLevels(params LevelTemplate[] levels) =>
        new("테스트", TimeOnly.MinValue,
            [
                new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [L("판단 필요"), L("{사유}"), L("{원래폴더}")]),
                new LayoutBranch("기본", BranchCondition.Always, levels),
            ],
            [], BuiltInPresets.DateOnly.FormatGroups);

    private static readonly Dictionary<string, Preset> StablePresets = new()
    {
        ["날짜+이벤트"] = BuiltInPresets.DateEvent,
        ["날짜"] = BuiltInPresets.DateOnly,
        ["날짜+기기+포맷"] = BuiltInPresets.DateDeviceFormat,
        ["월"] = BuiltInPresets.Monthly,
        ["고정 단계"] = WithLevels(L("사진첩"), L("{연도}"), L("{날짜}")),
        ["고정 단계+이벤트"] = WithLevels(L("사진첩"), L("{연도}"), L("{날짜} {이벤트}")),
        ["확장자"] = WithLevels(L("{연도}"), L("{확장자}")),
        ["이벤트+확장자"] = WithLevels(L("{연도}"), L("{날짜} {이벤트}"), L("{확장자}")),
        ["기간+이벤트+기기+포맷"] = WithLevels(L("{연도}"), L("[{기간}] {이벤트}"), L("{기기}"), L("{포맷}")),
        ["대체 글자"] = WithLevels(L("{연도}"), L("{날짜}"), new LevelTemplate("{이벤트}", EmptyValue.Replace, "이름 없음")),
    };

    public static TheoryData<string, bool, bool, SubfolderPolicy> StableCases()
    {
        var data = new TheoryData<string, bool, bool, SubfolderPolicy>();
        foreach (var preset in StablePresets.Keys)
        {
            foreach (var keepReview in new[] { true, false })
            {
                foreach (var useFolderNames in new[] { true, false })
                {
                    foreach (var subfolders in Enum.GetValues<SubfolderPolicy>())
                    {
                        data.Add(preset, keepReview, useFolderNames, subfolders);
                    }
                }
            }
        }

        return data;
    }

    private void SeedCorpus()
    {
        Seed($@"{Old}\a.jpg", [1], Oct14, "Zfc");
        Seed($@"{Old}\라이트룸\테이블1\b.jpg", [2], Oct14, "Zfc");
        Seed($@"{Old}\c.tif", [3], Oct14, "Zfc");
        Seed($@"{Old}\d.avi", [4], Oct15, "Zfc");
        Seed($@"{Old}\x.jpg", [5], null);
        Seed(@"D:\Archive\DCIM\e.jpg", [6], Oct14); // 기기 모름
        Seed(@"D:\Archive\판단 필요\기기 모름\DCIM\f.jpg", [7], Oct14);
        Seed(@"D:\Archive\2026-10-14 데이트\Zfc\라이트룸\g.jpg", [8], Oct14, "Zfc");
        Seed(@"D:\Archive\[2026-10-15] 출사\h.jpg", [9], Oct15, "Zfc");
        Seed($@"{Old}\같은이름.jpg", [10], Oct14, "Zfc");
        Seed(@"D:\Archive\DCIM\같은이름.jpg", [11], Oct14, "Zfc");
        Seed($@"{Old}\IMG.NEF", [12], Oct14, "Zfc");
        _fs.AddFile($@"{Old}\IMG.xmp", [13]);
        Seed(@"D:\Archive\DCIM\기타 이미지\i.tif", [14], Oct14, "Zfc");
        Seed(@"D:\Archive\판단 필요\촬영일 없음\DCIM\j.jpg", [15], Oct14, "Zfc");

        // 촬영일 없는 파일: {원래폴더}와 유지할 폴더가 겹치거나 어긋나기 쉬운 모양
        Seed(@"D:\Archive\라이트룸\테이블1\k.jpg", [16], null);
        Seed(@"D:\Archive\라이트룸\DCIM\l.jpg", [17], null);
        Seed(@"D:\Archive\데이트\DCIM\m.jpg", [18], null);
        Seed(@"D:\Archive\라이트룸\테이블1\DCIM\n.jpg", [19], null);
        Seed(@"D:\Archive\DCIM\o.jpg", [20], null);
        Seed(@"D:\Archive\테이블2\라이트룸\p.jpg", [21], null);
    }

    // \로 나눈 이름이 만든 구조
    private void SeedBackslashNames()
    {
        Seed(@"D:\Archive\2026\[2026-10-14] 가족여행\제주\Zfc\JPG\q.jpg", [22], Oct14, "Zfc");
        Seed(@"D:\Archive\2026\2026-10-15 소풍\공원\r.jpg", [23], Oct15, "Zfc");
        Seed(@"D:\Archive\[2026-10-15] 모임\자리\라이트룸\s.jpg", [24], Oct15, "Zfc");
        Seed(@"D:\Archive\[2026-10-15] 모임\자리\t.jpg", [25], null);
    }

    [Theory]
    [MemberData(nameof(StableCases))]
    public async Task Reorganize_RerunIsStable(string preset, bool keepReview, bool useFolderNames, SubfolderPolicy subfolders)
    {
        SeedCorpus();

        // {이벤트}만 있는 단계는 형식으로 읽지 않으므로(어떤 이름에나 맞는다) 판별기가 가까운 폴더 하나를 고른다.
        // 이 프리셋에서 \로 나눈 이름은 한 번 더 옮겨진다. 파일은 그대로이고 미리보기에 보이는 알려진 한계다.
        if (preset != "대체 글자")
        {
            SeedBackslashNames();
        }

        await AssertStableAsync(new ImportSettings([Archive], true, Archive, StablePresets[preset], TransferMode.Move,
            JobKind.Reorganize, useFolderNames, false, subfolders, keepReview));

        // 떨어져 있어도 같은 폴더 이름이 두 번 나오면 안 된다(자료에는 그런 폴더가 없다).
        foreach (var path in AllFiles())
        {
            var folders = Path.GetDirectoryName(path)!.Split('\\');
            Assert.Equal(folders.Length, folders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Theory]
    [InlineData("날짜+기기+포맷", @"모음\a.tif", "Zfc", true, @"2026\2026-10-14\Zfc\기타 이미지\모음\a.tif")]
    [InlineData("날짜", @"모음\x.jpg", null, false, @"판단 필요\촬영일 없음\모음\x.jpg")]
    [InlineData("고정 단계", @"모음\c.jpg", null, true, @"사진첩\2026\2026-10-14\모음\c.jpg")]
    [InlineData("확장자", @"모음\g.avi", null, true, @"2026\AVI\모음\g.avi")]
    [InlineData("날짜", @"판단 필요\기기 모름\DCIM\e.jpg", null, true, @"2026\2026-10-14\e.jpg")]
    [InlineData("날짜+기기+포맷", @"판단 필요\기기 모름\DCIM\e.jpg", "Zfc", true, @"2026\2026-10-14\Zfc\JPG\e.jpg")]
    [InlineData("날짜", @"라이트룸\테이블1\x.jpg", null, false, @"판단 필요\촬영일 없음\라이트룸\테이블1\x.jpg")]
    [InlineData("날짜", @"라이트룸\x.jpg", null, false, @"판단 필요\촬영일 없음\라이트룸\x.jpg")]
    [InlineData("날짜", @"라이트룸\DCIM\x.jpg", null, false, @"판단 필요\촬영일 없음\라이트룸\x.jpg")]
    [InlineData("날짜", @"데이트\DCIM\x.jpg", null, false, @"판단 필요\촬영일 없음\데이트\x.jpg")]
    [InlineData("날짜", @"라이트룸\테이블1\DCIM\x.jpg", null, false, @"판단 필요\촬영일 없음\라이트룸\테이블1\x.jpg")]
    [InlineData("날짜", @"DCIM\x.jpg", null, false, @"판단 필요\촬영일 없음\DCIM\x.jpg")]
    public async Task Reorganize_RerunKeepsDepth(string preset, string source, string? model, bool dated, string expected)
    {
        Seed($@"{Archive}\{source}", [1], dated ? Oct14 : null, model);

        await AssertStableAsync(new ImportSettings([Archive], true, Archive, StablePresets[preset], TransferMode.Move,
            JobKind.Reorganize, KeepReviewInPlace: false));

        Assert.Equal([$@"{Archive}\{expected}"], AllFiles());
    }

    [Fact]
    public async Task Reorganize_RerunWithNarrowerRoot_IsStable()
    {
        SeedCorpus();
        var preset = StablePresets["기간+이벤트+기기+포맷"];
        Apply(await PlanWithSuggestedNamesAsync(new ImportSettings([Archive], true, Archive, preset, TransferMode.Move,
            JobKind.Reorganize, true, false, SubfolderPolicy.Keep, false)));

        await AssertStableAsync(new ImportSettings([@"D:\Archive\2026"], true, Archive, preset, TransferMode.Move,
            JobKind.Reorganize, true, false, SubfolderPolicy.Keep, false));
    }

    [Fact]
    public async Task Reorganize_RerunWithSplitNames_IsStable()
    {
        Seed(@"D:\Archive\2026-10-14_가족_속초\라이트룸\a.jpg", [1], Oct14);

        await AssertStableAsync(new ImportSettings([Archive], true, Archive, BuiltInPresets.DateEvent, TransferMode.Move,
            JobKind.Reorganize, true, true, SubfolderPolicy.Keep, false));

        Assert.Equal([@"D:\Archive\2026\2026-10-14 가족\속초\라이트룸\a.jpg"], AllFiles());
    }

    private static readonly Preset FullPreset = WithLevels(L("{연도}"), L("[{기간}] {이벤트}"), L("{기기}"), L("{포맷}"));

    private static ImportSettings ReorganizeSettings(Preset preset, string? root = null, bool useFolderNames = true, bool split = false) =>
        new([root ?? Archive], true, Archive, preset, TransferMode.Move, JobKind.Reorganize, useFolderNames, split, SubfolderPolicy.Keep, true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reorganize_BackslashStructure_NoMoves(bool useFolderNames)
    {
        Seed(@"D:\Archive\2026\[2026-10-14] 가족여행\제주\Zfc\JPG\a.jpg", [1], Oct14, "Zfc");
        Seed(@"D:\Archive\2026\[2026-10-14] 가족여행\제주\Zfc\JPG\라이트룸\b.jpg", [2], Oct14, "Zfc");

        var plan = await PlanWithSuggestedNamesAsync(ReorganizeSettings(FullPreset, useFolderNames: useFolderNames));

        Assert.Empty(plan.Operations);
        Assert.Equal([SkipReason.AlreadyInPlace, SkipReason.AlreadyInPlace], plan.Skipped.Select(s => s.Reason));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Reorganize_AbsorbedOutput_NoMoves(bool useFolderNames, bool split)
    {
        // 흡수가 _ 나누기로 만든 구조를 재정리해도 한 건도 옮기지 않는다.
        Seed(@"D:\흩어짐\2026-10-14 가족여행_제주\a.jpg", [1], Oct14, "Zfc");
        Seed(@"D:\흩어짐\소풍_공원\라이트룸\b.jpg", [2], Oct15, "Zfc");
        Seed(@"D:\흩어짐\c.jpg", [3], Oct15, "Zfc");
        var absorb = new ImportSettings([@"D:\흩어짐"], true, Archive, FullPreset, TransferMode.Move, JobKind.Absorb, true, true);
        Apply(await PlanWithSuggestedNamesAsync(absorb));
        Assert.Contains(@"D:\Archive\2026\[2026-10-14] 가족여행\제주\Zfc\JPG\a.jpg", AllFiles());
        Assert.Contains(@"D:\Archive\2026\[2026-10-15] 소풍\공원\Zfc\JPG\라이트룸\b.jpg", AllFiles());

        var plan = await PlanWithSuggestedNamesAsync(ReorganizeSettings(FullPreset, useFolderNames: useFolderNames, split: split));

        Assert.Empty(plan.Operations);
    }

    [Fact]
    public async Task Reorganize_TypedBackslashName_StaysAfterMove()
    {
        Seed($@"{Archive}\a.jpg", [1], Oct14, "Zfc");
        var settings = ReorganizeSettings(FullPreset, useFolderNames: false);
        var workflow = Workflow();
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        Apply(workflow.BuildPlan(settings, scan, new Dictionary<string, string> { [scan.Sessions[0].Id] = @"바다\해변" }).Plan);
        Assert.Equal([@"D:\Archive\2026\[2026-10-14] 바다\해변\Zfc\JPG\a.jpg"], AllFiles());

        await AssertStableAsync(settings);

        Assert.Equal([@"D:\Archive\2026\[2026-10-14] 바다\해변\Zfc\JPG\a.jpg"], AllFiles());
    }

    [Fact]
    public async Task Reorganize_FolderBelowEventWithoutDevice_MovesOnce_ThenStays()
    {
        Seed(@"D:\Archive\2026\[2026-10-14] 일상\테이블1\a.jpg", [1], Oct14, "Zfc");

        await AssertStableAsync(ReorganizeSettings(FullPreset));

        Assert.Equal([@"D:\Archive\2026\[2026-10-14] 일상\테이블1\Zfc\JPG\a.jpg"], AllFiles());
    }

    [Fact]
    public async Task Reorganize_OriginalFolderInSameBranch_IsStable()
    {
        Seed(@"D:\Archive\[2026-10-14] 일상\테이블1\a.jpg", [1], Oct14, "Zfc");
        var preset = WithLevels(L("{연도}"), L("[{날짜}] {이벤트}"), L("{원래폴더}"));

        await AssertStableAsync(ReorganizeSettings(preset));

        Assert.Equal([@"D:\Archive\2026\[2026-10-14] 일상\테이블1\a.jpg"], AllFiles());
    }

    [Fact]
    public async Task Reorganize_RootBelowEventFolder_KeepsNameAndFolder()
    {
        const string Event = @"D:\Archive\2024\[2024-06-15] 일상";
        var june15 = new DateTime(2024, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);
        Seed($@"{Event}\Zfc\fb.jpg", [1], june15, "Zfc");
        Seed($@"{Event}\Zfc\라이트룸\테이블1\fa.jpg", [2], june15, "Zfc");
        var settings = ReorganizeSettings(FullPreset, $@"{Event}\Zfc");

        await AssertStableAsync(settings);

        Assert.Equal(
            [$@"{Event}\Zfc\JPG\fb.jpg", $@"{Event}\Zfc\JPG\라이트룸\테이블1\fa.jpg"],
            AllFiles().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Reorganize_RootBelowEventFolder_NameAppearsOnCard()
    {
        Seed(@"D:\Archive\2024\[2024-06-15] 일상\Zfc\a.jpg", [1], Oct14, "Zfc");
        var settings = ReorganizeSettings(FullPreset, @"D:\Archive\2024\[2024-06-15] 일상\Zfc");

        var scan = await Workflow().ScanAsync(settings, null, CancellationToken.None);

        Assert.Equal("일상", Assert.Single(scan.Sessions).SuggestedEventName);
    }

    [Theory]
    [InlineData(SubfolderPolicy.Merge)]
    [InlineData(SubfolderPolicy.InPlace)]
    public async Task Reorganize_PieceBelowEvent_IsNameUnderEveryPolicy(SubfolderPolicy policy)
    {
        // 이벤트 폴더 바로 아래의 후보 폴더는 하위 폴더가 아니라 이름의 조각이므로 합쳐지거나 제자리에 남지 않는다. 카드에서 이름을 고치면 된다.
        Seed(@"D:\Archive\2026\[2026-10-14] 일상\테이블1\a.jpg", [1], Oct14, "Zfc");

        await AssertStableAsync(ReorganizeSettings(FullPreset) with { Subfolders = policy });

        Assert.Equal([@"D:\Archive\2026\[2026-10-14] 일상\테이블1\Zfc\JPG\a.jpg"], AllFiles());
    }

    private const string Daily = @"D:\Archive\2024\[2024-06-15] 일상";
    private static readonly DateTime June15 = new(2024, 6, 15, 10, 0, 0, DateTimeKind.Unspecified);

    [Theory]
    [InlineData(@"Zfc\라이트룸", @"Zfc\라이트룸\c.jpg", @"Zfc\JPG\라이트룸\c.jpg")]
    [InlineData(@"Zfc\라이트룸", @"Zfc\라이트룸\테이블1\a.jpg", @"Zfc\JPG\라이트룸\테이블1\a.jpg")]
    [InlineData(@"Zfc\라이트룸\테이블1", @"Zfc\라이트룸\테이블1\a.jpg", @"Zfc\JPG\라이트룸\테이블1\a.jpg")]
    public async Task Reorganize_RootBelowEvent_KeepsHandMadeFolders(string root, string source, string expected)
    {
        Seed($@"{Daily}\{source}", [1], June15, "Zfc");

        await AssertStableAsync(ReorganizeSettings(FullPreset, $@"{Daily}\{root}"));

        Assert.Equal([$@"{Daily}\{expected}"], AllFiles());

        // 아카이브 전체를 골라도 그 자리다.
        Assert.Empty((await PlanWithSuggestedNamesAsync(ReorganizeSettings(FullPreset))).Operations);
    }

    [Theory]
    [InlineData(@"Zfc\라이트룸", @"Zfc\라이트룸\c.jpg")]
    [InlineData(@"Zfc\라이트룸", @"Zfc\라이트룸\테이블1\a.jpg")]
    [InlineData(@"Zfc\라이트룸\테이블1", @"Zfc\라이트룸\테이블1\a.jpg")]
    public async Task Reorganize_RootBelowEvent_InPlace_Stays(string root, string source)
    {
        Seed($@"{Daily}\{source}", [1], June15, "Zfc");

        var plan = await PlanWithSuggestedNamesAsync(ReorganizeSettings(FullPreset, $@"{Daily}\{root}") with { Subfolders = SubfolderPolicy.InPlace });

        Assert.Empty(plan.Operations);
        Assert.Equal(SkipReason.SubfolderKeptInPlace, Assert.Single(plan.Skipped).Reason);
    }

    [Theory]
    [InlineData(true, @"일상\테이블1\라이트룸\n.jpg", @"테이블1\라이트룸\n.jpg")]
    [InlineData(false, @"일상\테이블1\라이트룸\n.jpg", @"테이블1\라이트룸\n.jpg")]
    [InlineData(true, @"일상\가\나\n.jpg", @"가\나\n.jpg")]
    [InlineData(false, @"일상\가\나\n.jpg", @"가\나\n.jpg")]
    [InlineData(true, @"일상\테이블1\n.jpg", @"테이블1\n.jpg")]
    public async Task Reorganize_ReviewBranch_KeepsNamePieces(bool useFolderNames, string source, string expected)
    {
        Seed($@"D:\Archive\2024\[2024-06-15] {source}", [1], null, "Zfc");

        await AssertStableAsync(ReorganizeSettings(FullPreset, useFolderNames: useFolderNames) with { KeepReviewInPlace = false });

        Assert.Equal([$@"D:\Archive\판단 필요\촬영일 없음\{expected}"], AllFiles());
    }

    // ---- 여러 날을 묶은 폴더 ----

    private static string MultiDayNote(int count) =>
        $"여러 날을 묶은 폴더 {count}개가 날짜별로 나뉩니다. 여러 날 묶기가 들어오기 전에는 재정리에서 빼 주세요";

    [Fact]
    public void Reorganize_MultiDayFolders_AddNoteWithFolderCount()
    {
        var a = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-14\a.jpg", [1], Oct14);
        var b = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\2026-10-15\b.jpg", [2], Oct15);
        var c = Add(@"D:\Archive\2026\[2026-10-14~2026-10-15]\c.jpg", [3], Oct14);
        var d = Add($@"{Old}\d.jpg", [4], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(a), Group(b), Group(c), Group(d)]);

        Assert.Single(plan.Notes, MultiDayNote(2));
    }

    [Theory]
    [InlineData(@"D:\Archive\[2026-10-14] 소풍\a.jpg")] // 하루 폴더
    [InlineData(@"D:\Archive\2026-10-14~10-15 소풍\a.jpg")] // 대괄호가 없는 이름
    [InlineData(@"D:\Archive\[소풍~나들이]\a.jpg")] // 날짜가 아닌 글자
    public void Reorganize_OtherFolders_NoNote(string path)
    {
        var plan = Create(BuiltInPresets.DateOnly, [Group(Add(path, [1], Oct14))]);

        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Reorganize_MultiDayFolderFilesNotMoved_NoNote()
    {
        var kept = Add(@"D:\Archive\[2026-10-14~10-15] 소풍\a.jpg", [1], null);

        var plan = Create(BuiltInPresets.DateOnly, [Group(kept)], keepReview: true);

        Assert.Empty(plan.Notes);
    }

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    public void OtherJobs_MultiDayFolder_NoNote(JobKind kind)
    {
        var photo = Add(@"D:\흩어짐\[2026-10-14~10-15] 소풍\a.jpg", [1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, [Group(photo)], kind, roots: [@"D:\흩어짐"]);

        Assert.Empty(plan.Notes);
    }

    // ---- 화면이 부르는 흐름의 연결 ----

    private readonly FakeMetadataReader _reader = new();

    private ImportWorkflow Workflow() => new(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc);

    private void AddScanned(string path, byte[] content, DateTime? capture)
    {
        _fs.AddFile(path, content);
        _reader.Set(path, capture);
    }

    private static readonly IReadOnlyDictionary<string, string> NoNames = new Dictionary<string, string>();

    private async Task<Plan> BuildAsync(ImportSettings settings)
    {
        var workflow = Workflow();
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        return workflow.BuildPlan(settings, scan, NoNames).Plan;
    }

    [Fact]
    public async Task Workflow_Reorganize_PassesRootsAndKeptFolders()
    {
        AddScanned($@"{Old}\라이트룸\a.jpg", [1], Oct14);

        var plan = await BuildAsync(new ImportSettings([Old], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move,
            JobKind.Reorganize));

        Assert.Equal([(OperationKind.Move, $@"{Old}\라이트룸\a.jpg", $@"{Day14}\라이트룸\a.jpg")], Shape(FileOps(plan)));
        Assert.Equal([Old], plan.CleanupRoots);
    }

    [Fact]
    public async Task Workflow_Reorganize_MergeDropsSubfolders()
    {
        AddScanned($@"{Old}\라이트룸\a.jpg", [1], Oct14);

        var plan = await BuildAsync(new ImportSettings([Old], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move,
            JobKind.Reorganize, Subfolders: SubfolderPolicy.Merge));

        Assert.Equal($@"{Day14}\a.jpg", Assert.Single(FileOps(plan)).Destination);
    }

    [Fact]
    public async Task Workflow_Reorganize_InPlaceKeepsSubfolderFile()
    {
        AddScanned($@"{Old}\라이트룸\a.jpg", [1], Oct14);

        var plan = await BuildAsync(new ImportSettings([Old], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move,
            JobKind.Reorganize, Subfolders: SubfolderPolicy.InPlace));

        Assert.Empty(plan.Operations);
        Assert.Equal(SkipReason.SubfolderKeptInPlace, Assert.Single(plan.Skipped).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_Reorganize_UsesKeepReviewSetting(bool keep)
    {
        AddScanned($@"{Old}\x.jpg", [1], null);

        var plan = await BuildAsync(new ImportSettings([Old], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move,
            JobKind.Reorganize, KeepReviewInPlace: keep));

        Assert.Equal(keep, plan.Skipped.Any(s => s.Reason == SkipReason.ReviewKeptInPlace));
        Assert.Equal(!keep, FileOps(plan).Any(o => o.Destination == @"D:\Archive\판단 필요\촬영일 없음\모음\x.jpg"));
    }

    [Fact]
    public async Task Workflow_Absorb_IgnoresKeepReviewSetting()
    {
        AddScanned(@"D:\흩어짐\x.jpg", [1], null);

        var plan = await BuildAsync(new ImportSettings([@"D:\흩어짐"], true, Archive, BuiltInPresets.DateOnly, TransferMode.Move,
            JobKind.Absorb, KeepReviewInPlace: true));

        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\흩어짐\x.jpg", Assert.Single(FileOps(plan)).Destination);
        Assert.Equal([@"D:\흩어짐"], plan.CleanupRoots);
    }

    [Fact]
    public async Task Workflow_Import_BehavesAsBefore()
    {
        AddScanned(@"E:\DCIM\라이트룸\x.jpg", [1], null);
        AddScanned(@"E:\DCIM\라이트룸\a.jpg", [2], Oct14);

        var plan = await BuildAsync(new ImportSettings([@"E:\DCIM"], true, Archive, BuiltInPresets.DateOnly, TransferMode.Copy));

        Assert.Equal(
            [$@"{Day14}\a.jpg", @"D:\Archive\판단 필요\촬영일 없음\라이트룸\x.jpg"],
            FileOps(plan).Select(o => o.Destination).Order(StringComparer.Ordinal));
        Assert.Empty(plan.Skipped);
        Assert.Null(plan.CleanupRoots);
    }
}
