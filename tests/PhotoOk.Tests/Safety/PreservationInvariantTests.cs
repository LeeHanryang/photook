using System.IO;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Tests.Fakes;
using Xunit.Abstractions;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.Safety;

/// <summary>
/// 파일 보존 불변식. 변경 작업마다 강제 종료를 넣어도 원본이 보존되고, 이어 하기·되돌리기 뒤에도 그대로인지 확인한다.
/// <list type="bullet">
/// <item>I1: 모든 원본 경로가 원래 바이트 그대로 있다.</item>
/// <item>I2: 아카이브의 파일은 <c>.photook</c> 안의 것과 임시 이름(<c>.photook-tmp</c>)을 빼면 모두 어떤 원본이나 미리 있던 파일과 바이트가 같다.</item>
/// <item>I3(이동): 원본 내용 각각이 원본 자리, 대상 자리, 되돌림 폴더, 임시 이름 중 적어도 한 곳에 있다.</item>
/// </list>
/// 각 시나리오는 먼저 충돌 없이 실행해 변경 작업 수 N을 재고, k = 0..N-1마다 새 가짜 디스크에서 k번째 변경 작업 직전에 강제 종료한다.
/// 이어 하기 중 강제 종료도 같은 방식이다: 실행을 작업 도중의 정해진 시점 k에서 끊은 뒤, 이어 하기의 변경 작업 j = 0..M-1마다 끊는다.
/// </summary>
public class PreservationInvariantTests(ITestOutputHelper output)
{
    private const string Archive = @"D:\Archive";
    private const string AppData = @"C:\Users\test\AppData\Local\PhotoOk";
    private const string TempMarker = ".photook-tmp";
    private static readonly DateTime Now = new(2026, 10, 27, 1, 30, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    /// <summary>원본 JPG 하나(IMG_0001, EOS R6)와 내용이 같은 파일이 그 대상 경로에 미리 들어 있다.</summary>
    private const string PreexistingPath = @"D:\Archive\2026\2026-10-02\EOS R6\JPG\IMG_0001.JPG";

    // ------------------------------------------------------------------
    // 시나리오
    // ------------------------------------------------------------------

    private sealed class Scenario
    {
        public required FakeFileSystem Fs { get; init; }

        /// <summary>원본 경로 → 원래 바이트. 아카이브 안을 재정리하는 시나리오에서는 아카이브 안의 원래 파일도 원본이다.</summary>
        public required Dictionary<string, byte[]> Originals { get; init; }

        /// <summary>아카이브에 미리 있던(계획이 건드리지 않는) 파일 경로 → 바이트.</summary>
        public required Dictionary<string, byte[]> Preexisting { get; init; }

        public required Plan Plan { get; init; }

        public required bool MoveRun { get; init; }

        /// <summary>계획이 건드리지 않고 끝까지 그대로여야 하는 파일 경로 → 바이트(폴더에 남은 문서 등).</summary>
        public Dictionary<string, byte[]> Bystanders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>실행을 끝까지 마치면 치워져 있어야 하는 폴더.</summary>
        public IReadOnlyList<string> RemovedFolders { get; set; } = [];

        /// <summary>실행을 끝까지 마쳐도 남아 있어야 하는 폴더(고른 폴더, 문서가 남은 폴더).</summary>
        public IReadOnlyList<string> KeptFolders { get; set; } = [];

        public ExecutionContext Context => new(Fs, new XxHasher(Fs), AppData, () => Now);

        public Executor Executor => new(Context);

        public Undoer Undoer => new(Context);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private sealed class Setup
    {
        public FakeFileSystem Fs { get; } = new();

        public FakeMetadataReader Meta { get; } = new();

        public Dictionary<string, byte[]> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, byte[]> Preexisting { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, byte[]> Bystanders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Setup()
        {
            Fs.AddDirectory(Archive);
            Fs.AddDirectory(AppData);
        }

        public void Original(string path, byte[] content, DateTime? capture = null, string? make = null, string? model = null)
        {
            Fs.AddFile(path, content);
            Originals[path] = content;
            if (capture is not null)
            {
                Meta.Set(path, capture, make, model);
            }
        }

        /// <summary>스캔 뒤에 두는 파일(문서 등)이라 계획에 들어가지 않는다. 아카이브 안이면 미리 있던 파일이기도 하다.</summary>
        public void Bystander(string path, byte[] content)
        {
            Fs.AddFile(path, content);
            Bystanders[path] = content;
            if (path.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase))
            {
                Preexisting[path] = content;
            }
        }

        public Scenario Plan(IReadOnlyList<string> sources, Preset preset, TransferMode mode, JobKind kind = JobKind.Import,
            IReadOnlyList<string>? roots = null)
        {
            var scan = new Scanner(Fs, Meta, FileKinds.Default).Scan(sources, recursive: true);
            Assert.Empty(scan.Errors);
            var sessions = SessionGrouper.GroupByDay(scan.Groups, preset.DayStart, Zone);
            var plan = new Planner(Fs, new XxHasher(Fs)).Create(new PlanRequest(sessions, preset, Archive, mode, Zone,
                Kind: kind, Roots: roots));
            return new Scenario
            {
                Fs = Fs, Originals = Originals, Preexisting = Preexisting, Plan = plan, MoveRun = mode == TransferMode.Move,
                Bystanders = Bystanders,
            };
        }
    }

    /// <summary>
    /// 가져오기: <paramref name="sourceRoot"/>에 서로 다른 내용의 파일 7개를 둔다.
    /// RAW+JPG+xmp 한 쌍(RAW는 버퍼 두 번에 걸쳐 쓰이는 크기), 같은 이름·다른 기기 JPG 두 개, 날짜 없는 파일 하나, Thumbs.db.
    /// 아카이브의 대상 경로 하나에는 원본 JPG 하나와 같은 내용의 파일이 미리 있다. 프리셋은 날짜·기기·포맷.
    /// </summary>
    private static Scenario Import(string sourceRoot, TransferMode mode)
    {
        var setup = new Setup();
        string At(string relative) => Path.Combine(sourceRoot, relative);

        var day1 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Unspecified);
        var day2 = new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Unspecified);
        var canon = Bytes(1200, 4);
        setup.Original(At(@"100CAM\P1000001.RW2"), Bytes(1024 * 1024 + 300, 1), day1, "Panasonic", "DC-S5");
        setup.Original(At(@"100CAM\P1000001.JPG"), Bytes(1500, 2), day1, "Panasonic", "DC-S5");
        setup.Original(At(@"100CAM\P1000001.RW2.xmp"), Bytes(200, 3));
        setup.Original(At(@"101A\IMG_0001.JPG"), canon, day2, "Canon", "EOS R6");
        setup.Original(At(@"102B\IMG_0001.JPG"), Bytes(1300, 5), day2.AddMinutes(5), "FUJIFILM", "X-T5");
        setup.Original(At(@"100CAM\SCAN0001.JPG"), Bytes(800, 6));
        setup.Original(At("Thumbs.db"), Bytes(100, 7));
        setup.Fs.AddFile(PreexistingPath, canon);
        setup.Preexisting[PreexistingPath] = canon;

        var s = setup.Plan([sourceRoot], BuiltInPresets.DateDeviceFormat, mode);

        // 시나리오가 의도대로 만들어졌는지: 미리 있던 파일은 중복으로 건너뛰고, 나머지 사진 5개는 작업이 된다.
        Assert.Contains(s.Plan.Skipped, k => k.Reason == SkipReason.DuplicateAtDestination
                                             && string.Equals(k.Destination, PreexistingPath, StringComparison.OrdinalIgnoreCase));
        var expectedKind = mode == TransferMode.Move ? OperationKind.Move : OperationKind.Copy;
        Assert.Equal(5, s.Plan.Operations.Count(o => o.Kind == expectedKind));
        Assert.DoesNotContain(s.Plan.Operations, o => o.Kind != expectedKind && o.Kind != OperationKind.CreateDirectory);
        Assert.Contains(s.Plan.Operations, o => o.Destination.Contains(@"\판단 필요\", StringComparison.Ordinal));
        return s;
    }

    private const string Day14 = @"D:\Archive\2026\2026-10-14";
    private const string Day15 = @"D:\Archive\2026\2026-10-15";

    /// <summary>
    /// 재정리(이동 모드, 날짜만 프리셋): 아카이브 안의 두 파일 S가 서로 자리를 바꾸고(둘 다 임시 이름을 거친다),
    /// 14일 폴더의 P(15일 사진)는 15일로 옮기며, SD 카드의 P(14일 사진)는 다른 드라이브라 복사로 P가 비우는 자리에 들어간다.
    /// </summary>
    private static Scenario SwapAndCopyIntoVacatedSpot()
    {
        var setup = new Setup();
        var oct14 = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Unspecified);
        var oct15 = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Unspecified);
        setup.Original($@"{Day14}\S.JPG", Bytes(700, 11), oct15);
        setup.Original($@"{Day15}\S.JPG", Bytes(900, 12), oct14);
        setup.Original($@"{Day14}\P.JPG", Bytes(1100, 13), oct15);
        setup.Original(@"E:\DCIM\P.JPG", Bytes(1024 * 1024 + 77, 14), oct14);

        var s = setup.Plan([@"E:\DCIM", @"D:\Archive\2026"], BuiltInPresets.DateOnly, TransferMode.Move);

        Assert.Equal(3, s.Plan.Operations.Count(o => o.Kind == OperationKind.Move && IsTempName(o.Destination)));
        Assert.Single(s.Plan.Operations, o => o.Kind == OperationKind.Copy && o.Destination == $@"{Day14}\P.JPG");
        return s;
    }

    private const string Picked = @"D:\Archive\촬영";
    private const string Inbox = @"D:\Inbox";

    /// <summary>
    /// 재정리: 2026 폴더의 두 S가 자리를 바꾸고(임시 이름을 거친다), 고른 폴더 촬영의 파일들은 날짜 폴더로 옮겨진다.
    /// old\sub와 old는 비어 치워지고, keep은 문서가 남아 그대로이며, 고른 폴더는 치우지 않는다.
    /// </summary>
    private static Scenario ReorganizeWithCleanup()
    {
        var setup = new Setup();
        var oct14 = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Unspecified);
        var oct15 = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Unspecified);
        setup.Original($@"{Day14}\S.JPG", Bytes(700, 21), oct15);
        setup.Original($@"{Day15}\S.JPG", Bytes(900, 22), oct14);
        setup.Original($@"{Picked}\old\sub\a.jpg", Bytes(600, 23), oct14);
        setup.Original($@"{Picked}\old\sub\b.jpg", Bytes(650, 24), oct15);
        setup.Original($@"{Picked}\old\c.jpg", Bytes(1024 * 1024 + 9, 25), oct14);
        setup.Original($@"{Picked}\keep\d.jpg", Bytes(500, 26), oct15);
        setup.Original($@"{Picked}\chain\mid\leaf\e.jpg", Bytes(550, 28), oct14); // 파일 없이 하위 폴더만 있던 chain, mid도 치운다
        setup.Original($@"{Picked}\side\gone\f.jpg", Bytes(450, 29), oct15);
        setup.Fs.AddDirectory($@"{Picked}\side\beside"); // 치우는 gone 옆의 빈 폴더: 옮긴 파일이 없었으므로 남고, 그래서 side도 남는다
        setup.Fs.AddDirectory($@"{Picked}\unrelated\empty"); // 옮긴 파일과 상관없는 빈 폴더

        string[] roots = [@"D:\Archive\2026", Picked];
        var s = setup.Plan(roots, BuiltInPresets.DateOnly, TransferMode.Move, JobKind.Reorganize, roots);
        setup.Bystander($@"{Picked}\keep\memo.txt", Bytes(50, 27));

        Assert.Equal(roots, s.Plan.CleanupRoots);
        Assert.Contains(s.Plan.Operations, o => o.Kind == OperationKind.Move && IsTempName(o.Destination));
        Assert.Equal(8, s.Plan.Operations.Count(o => o.Kind == OperationKind.Move && !IsTempName(o.Destination)));
        s.RemovedFolders =
        [
            $@"{Picked}\old\sub", $@"{Picked}\old", $@"{Picked}\chain\mid\leaf", $@"{Picked}\chain\mid", $@"{Picked}\chain",
            $@"{Picked}\side\gone",
        ];
        s.KeptFolders =
        [
            Picked, $@"{Picked}\keep", Day14, Day15, @"D:\Archive\2026",
            $@"{Picked}\side", $@"{Picked}\side\beside", $@"{Picked}\unrelated", $@"{Picked}\unrelated\empty",
        ];
        return s;
    }

    /// <summary>
    /// 흡수(같은 드라이브 이동): 아카이브 밖의 고른 폴더에서 옮긴다. trip\day1과 trip은 비어 치워지고,
    /// keep은 문서가 남아 그대로이며, 고른 폴더는 치우지 않는다.
    /// </summary>
    private static Scenario AbsorbMoveWithCleanup()
    {
        var setup = new Setup();
        var oct14 = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Unspecified);
        var oct15 = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Unspecified);
        setup.Original($@"{Inbox}\trip\day1\a.jpg", Bytes(600, 31), oct14);
        setup.Original($@"{Inbox}\trip\b.jpg", Bytes(1024 * 1024 + 7, 32), oct15);
        setup.Original($@"{Inbox}\c.jpg", Bytes(650, 33), oct14);
        setup.Original($@"{Inbox}\keep\d.jpg", Bytes(500, 34), oct15);
        setup.Original($@"{Inbox}\chain\mid\leaf\e.jpg", Bytes(550, 36), oct14); // 파일 없이 하위 폴더만 있던 chain, mid도 치운다
        setup.Original($@"{Inbox}\side\gone\f.jpg", Bytes(450, 37), oct15);
        setup.Fs.AddDirectory($@"{Inbox}\side\beside"); // 치우는 gone 옆의 빈 폴더: 옮긴 파일이 없었으므로 남고, 그래서 side도 남는다
        setup.Fs.AddDirectory($@"{Inbox}\unrelated\empty"); // 옮긴 파일과 상관없는 빈 폴더

        string[] roots = [Inbox];
        var s = setup.Plan(roots, BuiltInPresets.DateOnly, TransferMode.Move, JobKind.Absorb, roots);
        setup.Bystander($@"{Inbox}\keep\memo.txt", Bytes(50, 35));

        Assert.Equal(roots, s.Plan.CleanupRoots);
        Assert.Equal(6, s.Plan.Operations.Count(o => o.Kind == OperationKind.Move));
        s.RemovedFolders =
        [
            $@"{Inbox}\trip\day1", $@"{Inbox}\trip", $@"{Inbox}\chain\mid\leaf", $@"{Inbox}\chain\mid", $@"{Inbox}\chain",
            $@"{Inbox}\side\gone",
        ];
        s.KeptFolders =
        [
            Inbox, $@"{Inbox}\keep",
            $@"{Inbox}\side", $@"{Inbox}\side\beside", $@"{Inbox}\unrelated", $@"{Inbox}\unrelated\empty",
        ];
        return s;
    }

    // ------------------------------------------------------------------
    // 불변식
    // ------------------------------------------------------------------

    private static bool IsTempName(string path) =>
        Path.GetFileName(path).Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    private static bool IsInPhotookFolder(string path) =>
        path.StartsWith(Archive + @"\.photook\", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ArchiveFiles(Scenario s) =>
        s.Fs.AllFiles().Where(p => p.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase));

    private static bool Same(byte[] expected, byte[] actual) => expected.AsSpan().SequenceEqual(actual);

    /// <summary>실행 도중: 복사면 I1·I2, 이동이면 I3·I2.</summary>
    private static void AssertPreserved(Scenario s, string when)
    {
        if (s.MoveRun)
        {
            AssertI3(s, when);
        }
        else
        {
            AssertI1(s, when);
        }

        AssertI2(s, when);
        AssertBystanders(s, when);
        AssertKeptFolders(s, when);
    }

    private static void AssertBystanders(Scenario s, string when)
    {
        foreach (var (path, content) in s.Bystanders)
        {
            Assert.True(s.Fs.FileExists(path) && Same(content, s.Fs.ReadAllBytes(path)), $"[{when}] 건드리지 않아야 할 파일이 바뀌었습니다: {path}");
        }
    }

    /// <summary>고른 폴더, 문서가 남은 폴더, 옮긴 파일과 상관없는 빈 폴더는 어느 시점에도 있어야 한다.</summary>
    private static void AssertKeptFolders(Scenario s, string when)
    {
        foreach (var folder in s.KeptFolders)
        {
            Assert.True(s.Fs.DirectoryExists(folder), $"[{when}] 남아야 할 폴더가 없습니다: {folder}");
        }
    }

    /// <summary>실행을 끝까지 마친 뒤: 비게 된 폴더는 치워졌고, 남아야 할 폴더는 그대로다.</summary>
    private static void AssertCleaned(Scenario s, string when)
    {
        foreach (var folder in s.RemovedFolders)
        {
            Assert.False(s.Fs.DirectoryExists(folder), $"[{when}] 치워야 할 폴더가 남았습니다: {folder}");
        }

        AssertKeptFolders(s, when);
    }

    private static void AssertI1(Scenario s, string when)
    {
        foreach (var (path, content) in s.Originals)
        {
            Assert.True(s.Fs.FileExists(path), $"[{when}] I1: 원본이 없습니다: {path}");
            Assert.True(Same(content, s.Fs.ReadAllBytes(path)), $"[{when}] I1: 원본 내용이 바뀌었습니다: {path}");
        }
    }

    private static void AssertI2(Scenario s, string when)
    {
        var known = s.Originals.Values.Concat(s.Preexisting.Values).ToList();
        foreach (var path in ArchiveFiles(s).Where(p => !IsInPhotookFolder(p) && !IsTempName(p)))
        {
            var content = s.Fs.ReadAllBytes(path);
            Assert.True(known.Any(k => Same(k, content)),
                $"[{when}] I2: 원본과도 미리 있던 파일과도 다른 파일이 진짜 이름으로 있습니다: {path} ({content.Length} 바이트)");
        }
    }

    private static void AssertI3(Scenario s, string when)
    {
        var quarantine = Archive + @"\.photook\되돌림\";
        var destinations = s.Plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory).Select(o => o.Destination).ToList();
        var extra = s.Fs.AllFiles().Where(p => IsTempName(p) || p.StartsWith(quarantine, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (path, content) in s.Originals)
        {
            var places = new[] { path }.Concat(destinations).Concat(extra);
            Assert.True(places.Any(p => s.Fs.FileExists(p) && Same(content, s.Fs.ReadAllBytes(p))),
                $"[{when}] I3: 원본 내용을 원본 자리·대상 자리·되돌림 폴더·임시 이름 어디에서도 찾을 수 없습니다: {path}");
        }
    }

    /// <summary>
    /// 되돌린 뒤: 아카이브의 <c>.photook</c> 밖에는 원래 있던 파일(미리 있던 파일과 아카이브 안의 원본)만 원래 내용으로 있다.
    /// 임시 이름 파일도, 이름을 바꿔 돌려놓은 파일도 없어야 한다.
    /// </summary>
    private static void AssertArchiveAsBefore(Scenario s, string when)
    {
        var expected = s.Preexisting
            .Concat(s.Originals.Where(o => o.Key.StartsWith(Archive + @"\", StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var outside = ArchiveFiles(s).Where(p => !IsInPhotookFolder(p)).ToList();
        Assert.True(outside.Count == expected.Count && outside.All(expected.ContainsKey),
            $"[{when}] 아카이브에 원래 없던 파일이 있습니다: {string.Join(", ", outside.Where(p => !expected.ContainsKey(p)))}");
        foreach (var (path, content) in expected)
        {
            Assert.True(Same(content, s.Fs.ReadAllBytes(path)), $"[{when}] 원래 있던 파일이 바뀌었습니다: {path}");
        }

        AssertBystanders(s, when);
        AssertKeptFolders(s, when);
    }

    /// <summary>실행이 끝난 뒤: 계획대로 옮긴 결과(작업을 순서대로 따라가 계산)와 디스크가 같고, 임시 이름 파일이 남지 않았다.</summary>
    private static void AssertAllDestinationsPresent(Scenario s, string when)
    {
        var expected = new Dictionary<string, byte[]>(s.Originals, StringComparer.OrdinalIgnoreCase);
        foreach (var op in s.Plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory))
        {
            expected[op.Destination] = expected[op.Source!];
            if (op.Kind == OperationKind.Move)
            {
                expected.Remove(op.Source!);
            }
        }

        foreach (var op in s.Plan.Operations.Where(o => o.Kind != OperationKind.CreateDirectory && !IsTempName(o.Destination)))
        {
            Assert.True(s.Fs.FileExists(op.Destination), $"[{when}] 대상이 없습니다: {op.Destination}");
            Assert.True(Same(expected[op.Destination], s.Fs.ReadAllBytes(op.Destination)),
                $"[{when}] 대상 내용이 계획과 다릅니다: {op.Destination}");
        }

        Assert.True(!s.Fs.AllFiles().Any(p => IsTempName(p) && !IsInPhotookFolder(p)), $"[{when}] 임시 이름 파일이 남았습니다");
    }

    /// <summary>기록 파일(아카이브 사본 먼저). 기록을 한 줄도 남기기 전에 끊겼으면 null.</summary>
    private static string? FindJournal(Scenario s)
    {
        var archiveJournal = Path.Combine(Archive, ".photook", "journal") + @"\";
        var appDataJournal = Path.Combine(AppData, "journal") + @"\";
        var files = s.Fs.AllFiles().Where(p => p.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).ToList();
        return files.FirstOrDefault(p => p.StartsWith(archiveJournal, StringComparison.OrdinalIgnoreCase))
               ?? files.FirstOrDefault(p => p.StartsWith(appDataJournal, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>모든 시점을 끝까지 돌리고, 실패한 시점을 모아 한 번에 알린다.</summary>
    private void ForEachCrashPoint(string name, int count, Action<int> check)
    {
        output.WriteLine($"{name}: 변경 작업 N = {count}");
        var failures = new List<string>();
        for (var k = 0; k < count; k++)
        {
            try
            {
                check(k);
            }
            catch (Exception ex)
            {
                failures.Add($"k={k}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count}/{count}개 시점에서 실패:\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------------------
    // 실행 중 강제 종료 → 이어 하기 → 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void CopyRun_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashExecuteAtEveryStep("가져오기(복사)", () => Import(@"E:\DCIM", TransferMode.Copy), minimumOps: 40);

    [Fact]
    public void MoveRun_SameVolume_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashExecuteAtEveryStep("가져오기(같은 드라이브 이동)", () => Import(@"D:\Inbox", TransferMode.Move), minimumOps: 30);

    [Fact]
    public void MoveRun_SwapAndCopyIntoVacatedSpot_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashExecuteAtEveryStep("재정리(자리 바꾸기)", SwapAndCopyIntoVacatedSpot, minimumOps: 30);

    [Fact]
    public void ReorganizeRun_WithCleanup_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashExecuteAtEveryStep("재정리(빈 폴더 치우기)", ReorganizeWithCleanup, minimumOps: 40);

    [Fact]
    public void AbsorbMoveRun_WithCleanup_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashExecuteAtEveryStep("흡수 이동(빈 폴더 치우기)", AbsorbMoveWithCleanup, minimumOps: 30);

    private void CrashExecuteAtEveryStep(string name, Func<Scenario> build, int minimumOps)
    {
        var clean = build();
        var before = clean.Fs.MutatingOps;
        Assert.Equal(RunOutcome.Completed, clean.Executor.Execute(clean.Plan).Outcome);
        AssertAllDestinationsPresent(clean, "충돌 없는 실행");
        AssertCleaned(clean, "충돌 없는 실행");
        var n = clean.Fs.MutatingOps - before;
        Assert.True(n >= minimumOps, $"변경 작업 수가 너무 적습니다: {n}");

        ForEachCrashPoint(name, n, k =>
        {
            var s = build();
            s.Fs.CrashAfter(k);
            Assert.Throws<SimulatedCrashException>(() => s.Executor.Execute(s.Plan));
            AssertPreserved(s, "실행 중 종료");

            s.Fs.ClearFault();
            var journal = FindJournal(s);
            if (journal is null)
            {
                // 기록을 남기기 전에 끊겼으면 아무 파일도 건드리지 않았어야 한다.
                AssertI1(s, "기록 없음");
                AssertArchiveAsBefore(s, "기록 없음");
                return;
            }

            var resumed = s.Executor.Resume(journal);
            AssertPreserved(s, "이어 하기 후");
            if (resumed.Outcome == RunOutcome.PlanIncomplete)
            {
                // 계획을 기록하다 끊긴 실행은 아무것도 하지 않았다. 되돌리기도 아무것도 바꾸지 않아야 한다.
                AssertI1(s, "계획 기록 미완");
                AssertArchiveAsBefore(s, "계획 기록 미완");
            }
            else
            {
                Assert.Equal(RunOutcome.Completed, resumed.Outcome);
                AssertAllDestinationsPresent(s, "이어 하기 후");
                AssertCleaned(s, "이어 하기 후");
            }

            var undo = s.Undoer.Undo(journal);
            AssertI1(s, "되돌리기 후");
            AssertI2(s, "되돌리기 후");
            AssertArchiveAsBefore(s, "되돌리기 후");
            Assert.True(undo.Skipped.Count == 0, "[되돌리기 후] 남긴 작업: " + string.Join(", ", undo.Skipped.Select(x => x.Reason)));
        });
    }

    // ------------------------------------------------------------------
    // 실행 중 강제 종료 → 이어 하지 않고 바로 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void ReorganizeRun_WithCleanup_UndoRightAfterCrash_RestoresFiles_AtEveryStep() =>
        CrashThenUndoAtEveryStep("재정리(끊긴 뒤 바로 되돌리기)", ReorganizeWithCleanup);

    [Fact]
    public void AbsorbMoveRun_WithCleanup_UndoRightAfterCrash_RestoresFiles_AtEveryStep() =>
        CrashThenUndoAtEveryStep("흡수 이동(끊긴 뒤 바로 되돌리기)", AbsorbMoveWithCleanup);

    /// <summary>폴더를 일부만 치운 채 끊긴 경우를 포함해, 어느 시점에 끊겨도 이어 하지 않고 되돌리면 처음과 같아야 한다.</summary>
    private void CrashThenUndoAtEveryStep(string name, Func<Scenario> build)
    {
        var clean = build();
        var before = clean.Fs.MutatingOps;
        Assert.Equal(RunOutcome.Completed, clean.Executor.Execute(clean.Plan).Outcome);
        var n = clean.Fs.MutatingOps - before;

        ForEachCrashPoint(name, n, k =>
        {
            var s = build();
            s.Fs.CrashAfter(k);
            Assert.Throws<SimulatedCrashException>(() => s.Executor.Execute(s.Plan));
            AssertPreserved(s, "실행 중 종료");

            s.Fs.ClearFault();
            var journal = FindJournal(s);
            if (journal is null)
            {
                AssertI1(s, "기록 없음");
                AssertArchiveAsBefore(s, "기록 없음");
                return;
            }

            var undo = s.Undoer.Undo(journal);
            AssertI1(s, "바로 되돌리기 후");
            AssertI2(s, "바로 되돌리기 후");
            AssertArchiveAsBefore(s, "바로 되돌리기 후");

            // 폴더 만들기 도중 끊기면 원래 있던 폴더인지 알 수 없어 남긴다(이어 하기를 거치면 생기지 않는 기존 동작). 남겨도 되는 것은 폴더뿐이다.
            var leftFiles = undo.Skipped.Where(x => s.Plan.Operations.Single(o => o.Id == x.OpId).Kind != OperationKind.CreateDirectory).ToList();
            Assert.True(leftFiles.Count == 0, "[바로 되돌리기 후] 남긴 파일 작업: " + string.Join(", ", leftFiles.Select(x => x.Reason)));
        });
    }

    // ------------------------------------------------------------------
    // 실행 중 강제 종료 → 이어 하기 중 강제 종료 → 다시 이어 하기 → 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void Resume_CopyRun_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashResumeAtEveryStep("이어 하기(복사)", () => Import(@"E:\DCIM", TransferMode.Copy), copyTempRequired: true);

    [Fact]
    public void Resume_MoveRun_SameVolume_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashResumeAtEveryStep("이어 하기(같은 드라이브 이동)", () => Import(@"D:\Inbox", TransferMode.Move), copyTempRequired: false);

    [Fact]
    public void Resume_SwapAndCopyIntoVacatedSpot_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashResumeAtEveryStep("이어 하기(자리 바꾸기)", SwapAndCopyIntoVacatedSpot, copyTempRequired: true);

    [Fact]
    public void Resume_ReorganizeRun_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashResumeAtEveryStep("이어 하기(재정리, 빈 폴더 치우기)", ReorganizeWithCleanup, copyTempRequired: false);

    /// <summary>
    /// 실행을 작업 도중(기록에 Started로 남은 작업이 있고, <paramref name="copyTempRequired"/>면 쓰다 만 복사 임시 파일도 있는 시점)
    /// 고정된 k에서 끊는다. 그 상태에서 이어 하기의 변경 작업 수 M을 잰 뒤, j = 0..M-1마다 같은 실행 중단을 새로 재현하고
    /// 이어 하기를 j에서 끊는다. 그 뒤 다시 이어 하기 → 모든 대상이 있다, 되돌리기 → 아카이브가 처음과 같다.
    /// </summary>
    private void CrashResumeAtEveryStep(string name, Func<Scenario> build, bool copyTempRequired)
    {
        var clean = build();
        var before = clean.Fs.MutatingOps;
        Assert.Equal(RunOutcome.Completed, clean.Executor.Execute(clean.Plan).Outcome);
        var n = clean.Fs.MutatingOps - before;

        (Scenario Scenario, string Journal) CrashExecuteAt(int k)
        {
            var s = build();
            s.Fs.CrashAfter(k);
            Assert.Throws<SimulatedCrashException>(() => s.Executor.Execute(s.Plan));
            s.Fs.ClearFault();
            return (s, FindJournal(s)!);
        }

        bool IsMidOperation(int k)
        {
            var (s, journal) = CrashExecuteAt(k);
            var midOperation = JournalReader.Load(s.Fs, journal).Operations.Any(o => o.Status == OpStatus.Started);
            var copyTemp = s.Fs.AllFiles().Any(p => p.EndsWith(TempMarker, StringComparison.OrdinalIgnoreCase));
            return midOperation && (!copyTempRequired || copyTemp);
        }

        var k = Enumerable.Range(n / 2, n - n / 2).Concat(Enumerable.Range(0, n / 2)).First(IsMidOperation);

        var (measured, measuredJournal) = CrashExecuteAt(k);
        var resumeStart = measured.Fs.MutatingOps;
        Assert.Equal(RunOutcome.Completed, measured.Executor.Resume(measuredJournal).Outcome);
        var m = measured.Fs.MutatingOps - resumeStart;
        AssertAllDestinationsPresent(measured, "충돌 없는 이어 하기");
        AssertCleaned(measured, "충돌 없는 이어 하기");
        Assert.True(m >= 3, $"이어 하기의 변경 작업 수가 너무 적습니다: {m}");
        output.WriteLine($"{name}: 실행 중단 k = {k} / N = {n}");

        ForEachCrashPoint(name, m, j =>
        {
            var (s, journal) = CrashExecuteAt(k);
            s.Fs.CrashAfter(j);
            Assert.Throws<SimulatedCrashException>(() => s.Executor.Resume(journal));
            AssertPreserved(s, "이어 하기 중 종료");

            s.Fs.ClearFault();
            var resumed = s.Executor.Resume(journal);
            Assert.Equal(RunOutcome.Completed, resumed.Outcome);
            AssertPreserved(s, "다시 이어 하기 후");
            AssertAllDestinationsPresent(s, "다시 이어 하기 후");
            AssertCleaned(s, "다시 이어 하기 후");

            var undo = s.Undoer.Undo(journal);
            AssertI1(s, "되돌리기 후");
            AssertI2(s, "되돌리기 후");
            AssertArchiveAsBefore(s, "되돌리기 후");
            Assert.True(undo.Skipped.Count == 0, "[되돌리기 후] 남긴 작업: " + string.Join(", ", undo.Skipped.Select(x => x.Reason)));
        });
    }

    // ------------------------------------------------------------------
    // 되돌리기 중 강제 종료 → 다시 되돌리기
    // ------------------------------------------------------------------

    [Fact]
    public void Undo_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashUndoAtEveryStep("되돌리기(복사)", () => Import(@"E:\DCIM", TransferMode.Copy));

    [Fact]
    public void Undo_MoveRun_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashUndoAtEveryStep("되돌리기(같은 드라이브 이동)", () => Import(@"D:\Inbox", TransferMode.Move));

    [Fact]
    public void Undo_SwapAndCopyIntoVacatedSpot_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashUndoAtEveryStep("되돌리기(자리 바꾸기)", SwapAndCopyIntoVacatedSpot);

    [Fact]
    public void Undo_ReorganizeRun_WithCleanup_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashUndoAtEveryStep("되돌리기(재정리, 치운 폴더 복원)", ReorganizeWithCleanup);

    [Fact]
    public void Undo_AbsorbMoveRun_WithCleanup_PreservesFiles_WhenCrashingAtEveryStep() =>
        CrashUndoAtEveryStep("되돌리기(흡수 이동, 치운 폴더 복원)", AbsorbMoveWithCleanup);

    private void CrashUndoAtEveryStep(string name, Func<Scenario> build)
    {
        var (clean, cleanJournal) = Execute(build());
        var before = clean.Fs.MutatingOps;
        Assert.Empty(clean.Undoer.Undo(cleanJournal).Skipped);
        var n = clean.Fs.MutatingOps - before;
        AssertI1(clean, "충돌 없는 되돌리기");
        AssertArchiveAsBefore(clean, "충돌 없는 되돌리기");
        Assert.True(n >= 10, $"변경 작업 수가 너무 적습니다: {n}");

        ForEachCrashPoint(name, n, k =>
        {
            var (s, journal) = Execute(build());
            s.Fs.CrashAfter(k);
            Assert.Throws<SimulatedCrashException>(() => s.Undoer.Undo(journal));
            AssertPreserved(s, "되돌리기 중 종료");

            s.Fs.ClearFault();
            var undo = s.Undoer.Undo(journal);
            AssertI1(s, "다시 되돌리기 후");
            AssertI2(s, "다시 되돌리기 후");
            AssertArchiveAsBefore(s, "다시 되돌리기 후");
            Assert.True(undo.Skipped.Count == 0, "[다시 되돌리기 후] 남긴 작업: " + string.Join(", ", undo.Skipped.Select(x => x.Reason)));
        });
    }

    private static (Scenario Scenario, string Journal) Execute(Scenario s)
    {
        var result = s.Executor.Execute(s.Plan);
        Assert.Equal(RunOutcome.Completed, result.Outcome);
        return (s, result.JournalPath!);
    }
}
