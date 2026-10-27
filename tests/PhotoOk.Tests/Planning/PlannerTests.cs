using System.IO;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Planning;

public class PlannerTests
{
    private const string Archive = @"D:\Archive";
    private const string Day14 = @"D:\Archive\2026\2026-10-14";
    private const string Day15 = @"D:\Archive\2026\2026-10-15";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FileTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();

    public PlannerTests()
    {
        _fs.AddDirectory(Archive);
    }

    private PhotoInfo Add(string path, byte[] content, DateTime? capture, string? make = null, string? model = null)
    {
        _fs.AddFile(path, content);
        return new PhotoInfo(path, content.Length, FileTime, FileKinds.Default.Classify(path), capture, make, model, null);
    }

    private static FileGroup Group(PhotoInfo primary, params PhotoInfo[] companions) => new(primary, companions);

    private Plan Create(Preset preset, TransferMode mode, params FileGroup[] groups) =>
        Create(new XxHasher(_fs), preset, mode, groups);

    private Plan Create(IHasher hasher, Preset preset, TransferMode mode, params FileGroup[] groups)
    {
        var sessions = SessionGrouper.GroupByDay(groups, preset.DayStart, TimeZoneInfo.Utc);
        return new Planner(_fs, hasher).Create(new PlanRequest(sessions, preset, Archive, mode, TimeZoneInfo.Utc));
    }

    private static List<PlannedOperation> FileOps(Plan plan) =>
        plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory).ToList();

    private static List<(OperationKind, string?, string)> Shape(IEnumerable<PlannedOperation> ops) =>
        ops.Select(o => (o.Kind, o.Source, o.Destination)).ToList();

    private static void AssertSequentialIds(Plan plan) =>
        Assert.Equal(Enumerable.Range(1, plan.Operations.Count).Select(i => $"op-{i:000000}"),
            plan.Operations.Select(o => o.Id));

    [Fact]
    public void Plan_CopiesIntoTemplatePath_WithDirectoriesFirst()
    {
        var jpg = Add(@"E:\DCIM\DSCF1.JPG", [1, 2, 3, 4], Oct14, "FUJIFILM", "X100VI");

        var plan = Create(BuiltInPresets.DateDeviceFormat, TransferMode.Copy, Group(jpg));

        Assert.Equal(Archive, plan.ArchiveRoot);
        Assert.Equal(
            [
                (OperationKind.CreateDirectory, null, @"D:\Archive\2026"),
                (OperationKind.CreateDirectory, null, @"D:\Archive\2026\2026-10-14"),
                (OperationKind.CreateDirectory, null, @"D:\Archive\2026\2026-10-14\X100VI"),
                (OperationKind.CreateDirectory, null, @"D:\Archive\2026\2026-10-14\X100VI\JPG"),
                (OperationKind.Copy, @"E:\DCIM\DSCF1.JPG", @"D:\Archive\2026\2026-10-14\X100VI\JPG\DSCF1.JPG"),
            ],
            Shape(plan.Operations));
        AssertSequentialIds(plan);
        Assert.All(plan.Operations.Take(4), o => Assert.Equal(0, o.Length));
        Assert.Equal(4, plan.Operations[4].Length);
        Assert.Empty(plan.Operations[4].Reasons);
        Assert.Empty(plan.Skipped);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Plan_CreatesEachMissingFolderOnce_AndSkipsExistingFolders()
    {
        _fs.AddDirectory(@"D:\Archive\2026");
        var a = Add(@"E:\DCIM\A.JPG", [1], Oct14);
        var b = Add(@"E:\DCIM\B.JPG", [2], Oct14);
        var c = Add(@"E:\DCIM\C.JPG", [3], Oct15);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(a), Group(b), Group(c));

        Assert.Equal([Day14, Day15],
            plan.Operations.Where(o => o.Kind == OperationKind.CreateDirectory).Select(o => o.Destination));
        Assert.Equal(5, plan.Operations.Count);
        Assert.Equal(OperationKind.CreateDirectory, plan.Operations[1].Kind);
        AssertSequentialIds(plan);
    }

    [Fact]
    public void Plan_ForcesCopy_AcrossVolumes()
    {
        var jpg = Add(@"E:\DCIM\DSCF1.JPG", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(jpg));

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(OperationKind.Copy, op.Kind);
        Assert.Contains("다른 드라이브라 복사로 처리: 1개", plan.Notes);
    }

    [Fact]
    public void Plan_UsesMove_OnSameVolume()
    {
        var jpg = Add(@"d:\Inbox\DSCF1.JPG", [1, 2, 3], Oct14); // 드라이브 문자 대소문자가 달라도 같은 볼륨

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(jpg));

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.Equal($@"{Day14}\DSCF1.JPG", op.Destination);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Plan_SkipsIdenticalFileAtDestination()
    {
        _fs.AddFile($@"{Day14}\DSCF1.JPG", [1, 2, 3]);
        var jpg = Add(@"E:\DCIM\DSCF1.JPG", [1, 2, 3], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(jpg));

        Assert.Empty(plan.Operations);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal(new PlannedSkip(@"E:\DCIM\DSCF1.JPG", $@"{Day14}\DSCF1.JPG", SkipReason.DuplicateAtDestination), skip);
    }

    [Fact]
    public void Plan_SuffixesWholeGroup_OnDifferentContentCollision()
    {
        _fs.AddFile($@"{Day14}\DSC_0001.NEF", [9, 9, 9, 9]); // 같은 크기, 다른 내용
        var nef = Add(@"E:\DCIM\DSC_0001.NEF", [1, 2, 3, 4], Oct14);
        var xmp = Add(@"E:\DCIM\DSC_0001.xmp", [5], null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(nef, xmp));

        Assert.Equal(
            [
                (OperationKind.Copy, @"E:\DCIM\DSC_0001.NEF", $@"{Day14}\DSC_0001_1.NEF"),
                (OperationKind.Copy, @"E:\DCIM\DSC_0001.xmp", $@"{Day14}\DSC_0001_1.xmp"),
            ],
            Shape(FileOps(plan)));
        Assert.Empty(plan.Skipped);
        Assert.Equal([9, 9, 9, 9], _fs.ReadAllBytes($@"{Day14}\DSC_0001.NEF"));
    }

    [Fact]
    public void Plan_SuffixesWholeGroup_WhenOnlyCompanionCollides()
    {
        _fs.AddFile($@"{Day14}\IMG.RW2.xmp", [7, 7]);
        var raw = Add(@"E:\DCIM\IMG.RW2", [1, 2, 3], Oct14);
        var xmp = Add(@"E:\DCIM\IMG.RW2.xmp", [5], null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(raw, xmp));

        Assert.Equal([$@"{Day14}\IMG_1.RW2", $@"{Day14}\IMG_1.RW2.xmp"], FileOps(plan).Select(o => o.Destination));
    }

    [Fact]
    public void Plan_PicksSmallestSuffixWithoutDifferentFile_AndSkipsSameOnes()
    {
        _fs.AddFile($@"{Day14}\DSC_0001.NEF", [9]);
        _fs.AddFile($@"{Day14}\DSC_0001_1.xmp", [8]);
        _fs.AddFile($@"{Day14}\DSC_0001_2.NEF", [1, 2, 3]); // 같은 내용이 _2에 이미 있다
        var nef = Add(@"E:\DCIM\DSC_0001.NEF", [1, 2, 3], Oct14);
        var xmp = Add(@"E:\DCIM\DSC_0001.xmp", [5], null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(nef, xmp));

        Assert.Equal([(OperationKind.Copy, @"E:\DCIM\DSC_0001.xmp", $@"{Day14}\DSC_0001_2.xmp")], Shape(FileOps(plan)));
        Assert.Equal(
            [new PlannedSkip(@"E:\DCIM\DSC_0001.NEF", $@"{Day14}\DSC_0001_2.NEF", SkipReason.DuplicateAtDestination)],
            plan.Skipped);
    }

    [Fact]
    public void Plan_AppendsSuffixBeforeExtension_WhenCompanionLacksKeyPrefix()
    {
        _fs.AddFile($@"{Day14}\IMG.HEIC", [9]);
        var heic = Add(@"E:\DCIM\IMG.HEIC", [1, 2], Oct14);
        var odd = Add(@"E:\DCIM\clip.MOV", [3], null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(heic, odd));

        Assert.Equal([$@"{Day14}\IMG_1.HEIC", $@"{Day14}\clip_1.MOV"], FileOps(plan).Select(o => o.Destination));
        Assert.Contains(plan.Notes, n => n.Contains("clip.MOV") && n.Contains("clip_1.MOV"));
    }

    [Fact]
    public void Plan_SuffixesSecondFile_WhenTwoSourcesShareNameInPlan()
    {
        var first = Add(@"E:\A\IMG_0001.JPG", [1, 2, 3], Oct14);
        var second = Add(@"E:\B\IMG_0001.JPG", [4, 5, 6], Oct14.AddMinutes(1));

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(first), Group(second));

        Assert.Equal(
            [
                (OperationKind.Copy, @"E:\A\IMG_0001.JPG", $@"{Day14}\IMG_0001.JPG"),
                (OperationKind.Copy, @"E:\B\IMG_0001.JPG", $@"{Day14}\IMG_0001_1.JPG"),
            ],
            Shape(FileOps(plan)));
    }

    [Fact]
    public void Plan_SkipsDuplicateInPlan()
    {
        var first = Add(@"E:\A\IMG_0001.JPG", [1, 2, 3], Oct14);
        var second = Add(@"E:\B\IMG_0001.JPG", [1, 2, 3], Oct14.AddMinutes(1));

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(first), Group(second));

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(@"E:\A\IMG_0001.JPG", op.Source);
        Assert.Equal(
            [new PlannedSkip(@"E:\B\IMG_0001.JPG", $@"{Day14}\IMG_0001.JPG", SkipReason.DuplicateInPlan)],
            plan.Skipped);
    }

    [Fact]
    public void Plan_DoesNotHash_WhenSizesDiffer()
    {
        _fs.AddFile($@"{Day14}\IMG_0001.JPG", [9, 9]);
        var jpg = Add(@"E:\A\IMG_0001.JPG", [1, 2, 3], Oct14);
        var hasher = new CountingHasher(new XxHasher(_fs));

        var plan = Create(hasher, BuiltInPresets.DateOnly, TransferMode.Copy, Group(jpg));

        Assert.Equal($@"{Day14}\IMG_0001_1.JPG", Assert.Single(FileOps(plan)).Destination);
        Assert.Equal(0, hasher.Calls);
    }

    [Fact]
    public void Plan_SkipsAlreadyInPlace()
    {
        Add($@"{Day14}\A.JPG", [1, 2, 3], Oct14);
        var inPlace = new PhotoInfo(@"d:\archive\2026\2026-10-14\a.jpg", 3, FileTime, MediaKind.Image, Oct14, null, null, null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(inPlace));

        Assert.Empty(plan.Operations);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal(SkipReason.AlreadyInPlace, skip.Reason);
    }

    [Fact]
    public void Plan_SwapsViaTempName()
    {
        // A는 15일 폴더에 잘못 있고 14일로, B는 14일 폴더에 잘못 있고 15일로 가야 한다. 이름은 같다.
        var a = Add($@"{Day15}\P.JPG", [1, 1, 1], Oct14);
        var b = Add($@"{Day14}\P.JPG", [2, 2, 2], Oct15);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(a), Group(b));

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
        AssertSequentialIds(plan);
        Assert.Empty(plan.Skipped);
        Assert.Empty(plan.Operations[0].Reasons);
        Assert.Equal(3, plan.Operations[2].Length);
    }

    [Fact]
    public void Plan_ChainMove_MovesVacatedSourceToTempFirst()
    {
        // B는 14일 → 15일(빈 자리), A는 다른 폴더 → B가 비우는 14일 자리.
        var a = Add(@"D:\Inbox\P.JPG", [1, 1, 1], Oct14);
        var b = Add($@"{Day14}\P.JPG", [2, 2, 2], Oct15);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(a), Group(b));

        var tempB = $@"{Day14}\P.JPG.photook-tmp-op-000002";
        Assert.Equal(
            [
                (OperationKind.CreateDirectory, null, Day15),
                (OperationKind.Move, $@"{Day14}\P.JPG", tempB),
                (OperationKind.Move, @"D:\Inbox\P.JPG", $@"{Day14}\P.JPG"),
                (OperationKind.Move, tempB, $@"{Day15}\P.JPG"),
            ],
            Shape(plan.Operations));
    }

    [Fact]
    public void Plan_DoesNotTreatSpotAsFree_WhenItsFileStays()
    {
        // B는 15일로 가려 하지만 같은 내용이 이미 있어 건너뛴다 → B는 14일 자리에 그대로 남는다.
        // 그러므로 A는 B 자리를 빈 자리로 보면 안 되고 _1을 받아야 한다.
        _fs.AddFile($@"{Day15}\P.JPG", [2, 2, 2]);
        var a = Add(@"D:\Inbox\P.JPG", [1, 1, 1], Oct14);
        var b = Add($@"{Day14}\P.JPG", [2, 2, 2], Oct15);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(a), Group(b));

        Assert.Equal([(OperationKind.Move, @"D:\Inbox\P.JPG", $@"{Day14}\P_1.JPG")], Shape(plan.Operations));
        Assert.Equal(
            [new PlannedSkip($@"{Day14}\P.JPG", $@"{Day15}\P.JPG", SkipReason.DuplicateAtDestination)],
            plan.Skipped);
    }

    [Fact]
    public void Plan_CrossVolumeCopy_DoesNotFreeItsSourceSpot()
    {
        // 원본이 다른 드라이브면 복사라서 자리를 비우지 않는다(아카이브 쪽은 영향 없음).
        _fs.AddFile($@"{Day14}\P.JPG", [9, 9, 9]);
        var a = Add(@"E:\DCIM\P.JPG", [1, 1, 1], Oct14);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Move, Group(a));

        Assert.Equal([(OperationKind.Copy, @"E:\DCIM\P.JPG", $@"{Day14}\P_1.JPG")], Shape(plan.Operations));
    }

    [Fact]
    public void Plan_DoesNotTargetAnotherCopySourceSpot_EvenIfMissingOnDisk()
    {
        // 복사 모드에서 B는 자리를 비우지 않는다. 스캔 뒤 B가 디스크에서 사라졌어도 그 자리를 빈 자리로 보지 않는다.
        var a = Add(@"E:\DCIM\P.JPG", [1, 1, 1], Oct14);
        var b = new PhotoInfo($@"{Day14}\P.JPG", 3, FileTime, MediaKind.Image, Oct15, null, null, null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(a), Group(b));

        Assert.Equal($@"{Day14}\P_1.JPG", FileOps(plan)[0].Destination);
    }

    [Fact]
    public void Plan_UsesReviewBranch_AndCarriesReasons()
    {
        var jpg = Add(@"E:\DCIM\x.JPG", [1], null);

        var plan = Create(BuiltInPresets.DateOnly, TransferMode.Copy, Group(jpg));

        var op = Assert.Single(FileOps(plan));
        Assert.Equal(@"D:\Archive\판단 필요\촬영일 없음\DCIM\x.JPG", op.Destination);
        Assert.Equal([ReviewReason.NoCaptureDate], op.Reasons);
    }

    private sealed class CountingHasher(IHasher inner) : IHasher
    {
        public int Calls { get; private set; }

        public string Hash(string path)
        {
            Calls++;
            return inner.Hash(path);
        }
    }
}
