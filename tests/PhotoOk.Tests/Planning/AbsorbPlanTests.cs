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
using PhotoOk.Tests.Workflow;

namespace PhotoOk.Tests.Planning;

public class AbsorbPlanTests
{
    private const string Archive = @"D:\Archive";
    private const string Inbox = @"D:\Inbox";
    private const string Day14 = @"D:\Archive\2026\2026-10-14";
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime FileTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Roots = [Inbox];

    private readonly FakeFileSystem _fs = new();

    public AbsorbPlanTests()
    {
        _fs.AddDirectory(Archive);
        _fs.AddDirectory(Inbox);
    }

    /// <summary>스캔 결과로 쓸 사진 정보를 만들며 파일도 가짜 디스크에 둔다.</summary>
    private PhotoInfo Add(string path, byte[] content)
    {
        _fs.AddFile(path, content);
        return new PhotoInfo(path, content.Length, FileTime, FileKinds.Default.Classify(path), Oct14, null, null, null);
    }

    private static FileGroup Group(PhotoInfo primary, params PhotoInfo[] companions) => new(primary, companions);

    /// <summary>색인은 파일을 모두 둔 뒤에 만든다. <paramref name="indexed"/>가 false이면 색인을 넘기지 않는다.</summary>
    private Plan Create(FileGroup[] groups, IHasher? hasher = null, JobKind kind = JobKind.Absorb,
        TransferMode mode = TransferMode.Move, bool indexed = true)
    {
        var sessions = SessionGrouper.GroupByDay(groups, BuiltInPresets.DateOnly.DayStart, TimeZoneInfo.Utc, null);
        var index = indexed ? ArchiveIndex.Build(_fs, Archive, null, CancellationToken.None) : null;
        return new Planner(_fs, hasher ?? new XxHasher(_fs)).Create(new PlanRequest(sessions, BuiltInPresets.DateOnly, Archive,
            mode, TimeZoneInfo.Utc, null, kind, Roots, index));
    }

    [Fact]
    public void Absorb_FileElsewhereInArchive_IsSkippedWithExistingPath()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var source = Add($@"{Inbox}\b.jpg", [1, 2, 3]);

        var plan = Create([Group(source)]);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [new PlannedSkip($@"{Inbox}\b.jpg", $@"{Inbox}\b.jpg", SkipReason.DuplicateInArchive, @"D:\Archive\03\a.jpg")],
            plan.Skipped);
    }

    [Fact]
    public void Absorb_SameLengthDifferentContent_IsMoved()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var source = Add($@"{Inbox}\b.jpg", [4, 5, 6]);

        var plan = Create([Group(source)]);

        Assert.Equal([(OperationKind.Move, $@"{Inbox}\b.jpg", $@"{Day14}\b.jpg")],
            plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory).Select(o => (o.Kind, o.Source!, o.Destination)));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Absorb_UnreadableCandidate_IsNotDuplicate()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        _fs.FailReadsOf(@"D:\Archive\03\a.jpg");
        var source = Add($@"{Inbox}\b.jpg", [1, 2, 3]);

        var plan = Create([Group(source)]);

        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Move && o.Destination == $@"{Day14}\b.jpg");
        Assert.Empty(plan.Skipped);
        Assert.Contains(@"파일을 읽지 못해 다른 파일로 처리했습니다: D:\Archive\03\a.jpg", plan.Notes);
    }

    [Fact]
    public void Absorb_PartiallyDuplicateGroup_KeepsGroupTogether()
    {
        _fs.AddFile(@"D:\Archive\03\IMG_1.NEF", [1, 1, 1]);
        var raw = Add($@"{Inbox}\IMG_1.NEF", [1, 1, 1]);
        var xmp = Add($@"{Inbox}\IMG_1.xmp", [9, 9]);

        var plan = Create([Group(raw, xmp)]);

        var destinations = plan.Operations.Where(o => o.Kind == OperationKind.Move).Select(o => o.Destination).Order().ToList();
        Assert.Equal([$@"{Day14}\IMG_1.NEF", $@"{Day14}\IMG_1.xmp"], destinations);
        Assert.DoesNotContain(plan.Skipped, s => s.Reason == SkipReason.DuplicateInArchive);
    }

    [Fact]
    public void Absorb_FullyDuplicateGroup_IsSkipped()
    {
        _fs.AddFile(@"D:\Archive\03\IMG_1.NEF", [1, 1, 1]);
        _fs.AddFile(@"D:\Archive\04\IMG_1.xmp", [9, 9]);
        var raw = Add($@"{Inbox}\IMG_1.NEF", [1, 1, 1]);
        var xmp = Add($@"{Inbox}\IMG_1.xmp", [9, 9]);

        var plan = Create([Group(raw, xmp)]);

        Assert.Empty(plan.Operations);
        Assert.Equal(
            [
                new PlannedSkip($@"{Inbox}\IMG_1.NEF", $@"{Inbox}\IMG_1.NEF", SkipReason.DuplicateInArchive, @"D:\Archive\03\IMG_1.NEF"),
                new PlannedSkip($@"{Inbox}\IMG_1.xmp", $@"{Inbox}\IMG_1.xmp", SkipReason.DuplicateInArchive, @"D:\Archive\04\IMG_1.xmp"),
            ],
            plan.Skipped);
    }

    [Fact]
    public void Absorb_HashesOnlySameLength()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        _fs.AddFile(@"D:\Archive\03\b.jpg", [4, 5, 6]);
        _fs.AddFile(@"D:\Archive\03\c.jpg", [7, 8, 9, 10, 11]);
        var source = Add($@"{Inbox}\x.jpg", [7, 7, 7]);
        var hasher = new CountingHasher(new XxHasher(_fs));

        var plan = Create([Group(source)], hasher);

        Assert.Single(plan.Operations, o => o.Kind == OperationKind.Move);
        Assert.Equal(3, hasher.TotalCalls); // 원본 1번 + 크기가 같은 아카이브 파일 2개
        Assert.DoesNotContain(@"D:\Archive\03\c.jpg", hasher.CallsByPath.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Absorb_CopyMode_AlsoChecks()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var source = Add($@"{Inbox}\b.jpg", [1, 2, 3]);

        var plan = Create([Group(source)], mode: TransferMode.Copy);

        Assert.Empty(plan.Operations);
        Assert.Equal(SkipReason.DuplicateInArchive, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Import_DoesNotCheckArchive()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var source = Add($@"{Inbox}\b.jpg", [1, 2, 3]);

        var withIndex = Create([Group(source)], kind: JobKind.Import);
        var withoutIndex = Create([Group(source)], indexed: false);

        Assert.Empty(withIndex.Skipped);
        Assert.Contains(withIndex.Operations, o => o.Kind == OperationKind.Move);
        Assert.Empty(withoutIndex.Skipped);
        Assert.Contains(withoutIndex.Operations, o => o.Kind == OperationKind.Move);
    }

    [Fact]
    public void Absorb_SkippedDuplicate_DoesNotVacateItsSource()
    {
        // 중복으로 건너뛴 파일은 제자리에 남으므로 그 자리는 다른 파일에게 빈 자리가 아니다.
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var duplicate = Add($@"{Day14}\a.jpg", [1, 2, 3]);
        var other = Add($@"{Inbox}\a.jpg", [4, 5, 6]);

        var plan = Create([Group(duplicate), Group(other)]);

        var move = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Move);
        Assert.Equal($@"{Inbox}\a.jpg", move.Source);
        Assert.Equal($@"{Day14}\a_1.jpg", move.Destination);
        Assert.Contains(plan.Skipped, s => s.Reason == SkipReason.DuplicateInArchive && s.Source == $@"{Day14}\a.jpg");
    }

    [Fact]
    public void Absorb_DuplicatesOfEachOtherNotInArchive_OneMovedOneSkipped()
    {
        var first = Add($@"{Inbox}\one\a.jpg", [1, 2, 3]);
        var second = Add($@"{Inbox}\two\a.jpg", [1, 2, 3]);

        var plan = Create([Group(first), Group(second)]);

        var move = Assert.Single(plan.Operations, o => o.Kind == OperationKind.Move);
        Assert.Equal($@"{Day14}\a.jpg", move.Destination);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal(SkipReason.DuplicateInPlan, skip.Reason);
        Assert.NotEqual(move.Source, skip.Source);
    }

    [Fact]
    public void Absorb_EmptyFile_IsNeverDuplicateInArchive()
    {
        _fs.AddFile(@"D:\Archive\03\empty.jpg", []);
        var source = Add($@"{Inbox}\b.jpg", []);

        var plan = Create([Group(source)]);

        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Move && o.Destination == $@"{Day14}\b.jpg");
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Absorb_SourceInIndexAsOnlyFileOfItsSize_IsNotItsOwnDuplicate()
    {
        var inside = Add($@"{Day14}\a.jpg", [1, 2, 3]);

        var plan = Create([Group(inside)]);

        Assert.Empty(plan.Operations);
        Assert.DoesNotContain(plan.Skipped, s => s.Reason == SkipReason.DuplicateInArchive);
        Assert.Equal(SkipReason.AlreadyInPlace, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void Absorb_CandidateDeletedAfterScan_IsMovedWithNote()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var source = Add($@"{Inbox}\b.jpg", [1, 2, 3]);
        var hasher = new ThrowingHasher(new XxHasher(_fs), @"D:\Archive\03\a.jpg");

        var plan = Create([Group(source)], hasher);

        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Move && o.Destination == $@"{Day14}\b.jpg");
        Assert.Empty(plan.Skipped);
        Assert.Contains(@"파일을 읽지 못해 다른 파일로 처리했습니다: D:\Archive\03\a.jpg", plan.Notes);
    }

    [Fact]
    public void Absorb_UnreadableSource_IsMovedAndNotReportedAsExisting()
    {
        _fs.AddFile(@"D:\Archive\03\a.jpg", [1, 2, 3]);
        var source = Add($@"{Inbox}\b.jpg", [1, 2, 3]);
        _fs.FailReadsOf($@"{Inbox}\b.jpg");

        var plan = Create([Group(source)]);

        Assert.Contains(plan.Operations, o => o.Kind == OperationKind.Move && o.Source == $@"{Inbox}\b.jpg");
        Assert.DoesNotContain(plan.Skipped, s => s.Reason == SkipReason.DuplicateInArchive);
        Assert.Contains($@"파일을 읽지 못해 다른 파일로 처리했습니다: {Inbox}\b.jpg", plan.Notes);
    }

    /// <summary>한 경로만 읽다가 파일이 사라진 것처럼 <see cref="FileNotFoundException"/>을 던진다.</summary>
    private sealed class ThrowingHasher(IHasher inner, string missing) : IHasher
    {
        public string Hash(string path) => string.Equals(path, missing, StringComparison.OrdinalIgnoreCase)
            ? throw new FileNotFoundException("사라진 파일", path)
            : inner.Hash(path);
    }
}
