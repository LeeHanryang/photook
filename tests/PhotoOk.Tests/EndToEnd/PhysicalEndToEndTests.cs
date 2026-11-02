using System.IO;
using System.Security.Cryptography;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.EndToEnd;

/// <summary>실제 임시 폴더에서 스캔 → 묶기 → 계획 → 실행 → 되돌리기까지 진짜 구성 요소만으로 돌려 본다.</summary>
public sealed class PhysicalEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photook-e2e", Guid.NewGuid().ToString("N"));

    public PhysicalEndToEndTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 테스트 결과에 영향을 주지 않는다.
        }
    }

    [Fact]
    public void RealDisk_MoveCleanupThenUndo_RestoresFolders()
    {
        var archive = Path.Combine(_root, "archive");
        var picked = Path.Combine(archive, "picked");
        var appData = Path.Combine(_root, "appdata");
        var deep = Path.Combine(picked, "old", "sub");
        var kept = Path.Combine(picked, "kept");
        var target = Path.Combine(archive, "2026");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(kept);
        Directory.CreateDirectory(target);
        var photo = Path.Combine(deep, "a.jpg");
        var other = Path.Combine(kept, "b.jpg");
        var memo = Path.Combine(kept, "memo.txt");
        byte[] photoBytes = [1, 2, 3];
        byte[] otherBytes = [4, 5];
        File.WriteAllBytes(photo, photoBytes);
        File.WriteAllBytes(other, otherBytes);
        File.WriteAllText(memo, "memo");
        var photoTime = new DateTime(2020, 5, 1, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(photo, photoTime);

        var fs = new PhysicalFileSystem();
        var context = new ExecutionContext(fs, new XxHasher(fs), appData, () => DateTime.UtcNow);
        var plan = new Plan(archive,
            [
                new PlannedOperation("op1", OperationKind.Move, photo, Path.Combine(target, "a.jpg"), photoBytes.Length, []),
                new PlannedOperation("op2", OperationKind.Move, other, Path.Combine(target, "b.jpg"), otherBytes.Length, []),
            ],
            [], [], [picked]);

        var result = new Executor(context).Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(Path.Combine(picked, "old")));
        Assert.True(Directory.Exists(picked));
        Assert.True(File.Exists(memo)); // 문서가 남은 폴더는 그대로다
        Assert.Equal(photoBytes, File.ReadAllBytes(Path.Combine(target, "a.jpg")));

        var undo = new Undoer(context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        Assert.Equal(photoBytes, File.ReadAllBytes(photo));
        Assert.Equal(photoTime, File.GetLastWriteTimeUtc(photo));
        Assert.Equal(otherBytes, File.ReadAllBytes(other));
    }

    [Fact]
    public void RealDisk_ImportThenUndo()
    {
        var source = Path.Combine(_root, "card", "DCIM");
        var archive = Path.Combine(_root, "archive");
        var appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(archive);

        // 두 날짜, 두 기기의 JPEG 3장. 내용(촬영 정보)이 모두 달라 바이트도 다르다.
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(source, "IMG_0001.JPG")] = TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0), "Canon", "EOS R6"),
            [Path.Combine(source, "DSCF0001.JPG")] = TestImages.Jpeg(new DateTime(2026, 10, 14, 11, 0, 0), "FUJIFILM", "X-T5"),
            [Path.Combine(source, "IMG_0002.JPG")] = TestImages.Jpeg(new DateTime(2026, 10, 15, 8, 15, 0), "Canon", "EOS R6"),
        };
        var fileTime = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (path, content) in originals)
        {
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, fileTime); // 지금과 다른 시각이라야 복사본이 원본 시각을 가졌는지 알 수 있다
            fileTime = fileTime.AddMinutes(7);
        }

        var times = originals.Keys.ToDictionary(p => p, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

        var fs = new PhysicalFileSystem();
        var zone = TimeZoneInfo.Utc;
        var preset = BuiltInPresets.DateDeviceFormat;
        var scan = new Scanner(fs, new MetadataExtractorReader(fs, FileKinds.Default, zone), FileKinds.Default)
            .Scan([source], recursive: true);
        Assert.Empty(scan.Errors);
        Assert.Equal(3, scan.Groups.Count);

        var sessions = SessionGrouper.GroupByDay(scan.Groups, preset.DayStart, zone);
        Assert.Equal(2, sessions.Count);
        var plan = new Planner(fs, new XxHasher(fs)).Create(new PlanRequest(sessions, preset, archive, TransferMode.Copy));

        var context = new ExecutionContext(fs, new XxHasher(fs), appData, () => DateTime.UtcNow);
        var result = new Executor(context).Execute(plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(archive, "2026", "2026-10-14", "EOS R6", "JPG", "IMG_0001.JPG")] = Path.Combine(source, "IMG_0001.JPG"),
            [Path.Combine(archive, "2026", "2026-10-14", "X-T5", "JPG", "DSCF0001.JPG")] = Path.Combine(source, "DSCF0001.JPG"),
            [Path.Combine(archive, "2026", "2026-10-15", "EOS R6", "JPG", "IMG_0002.JPG")] = Path.Combine(source, "IMG_0002.JPG"),
        };
        foreach (var (destination, original) in expected)
        {
            Assert.True(File.Exists(destination), $"대상이 없습니다: {destination}");
            Assert.Equal(originals[original], File.ReadAllBytes(destination));
            Assert.Equal(File.GetLastWriteTimeUtc(original), File.GetLastWriteTimeUtc(destination));
        }

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), FilesOutsidePhotook(archive));
        Assert.NotNull(result.JournalPath);
        Assert.True(File.Exists(JournalPaths.InArchive(archive, result.RunId)));
        Assert.True(File.Exists(JournalPaths.InAppData(appData, result.RunId)));

        var undo = new Undoer(context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        Assert.Empty(FilesOutsidePhotook(archive));
        Assert.Equal([Path.Combine(archive, ".photook")], Directory.EnumerateDirectories(archive)); // 이번 실행이 만든 폴더는 치웠다
        foreach (var (path, content) in originals)
        {
            Assert.Equal(content, File.ReadAllBytes(path));
            Assert.Equal(times[path], File.GetLastWriteTimeUtc(path));
        }

        Assert.Equal(originals.Keys.Order(StringComparer.OrdinalIgnoreCase),
            Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reorganize_FolderDate_ThenUndo_RestoresEverything()
    {
        var archive = Path.Combine(_root, "archive");
        var appData = Path.Combine(_root, "appdata");
        var day14 = Path.Combine(archive, "2026", "2026-10-14");
        var day15 = Path.Combine(archive, "2026", "2026-10-15");
        var day16 = Path.Combine(archive, "2026", "2026-10-16");
        var subA = Path.Combine(day15, "sub", "a.jpg"); // 폴더 날짜(15일)로 간다
        var otherA = Path.Combine(day15, "other", "a.jpg"); // 이름이 같고 내용이 달라 순번(a_1.jpg)이 붙는다
        var chosen = Path.Combine(day15, "keep", "b.jpg"); // 사진 날짜(14일)를 골랐다
        var deepC = Path.Combine(day16, "deep", "er", "c.jpg"); // 폴더 날짜와 사진 날짜가 같다
        var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [subA] = TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 0, 0), "Canon", "EOS R6"),
            [otherA] = TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 30, 0), "Canon", "EOS R6"),
            [chosen] = TestImages.Jpeg(new DateTime(2026, 10, 14, 10, 0, 0), "Canon", "EOS R6"),
            [deepC] = TestImages.Jpeg(new DateTime(2026, 10, 16, 11, 0, 0), "Canon", "EOS R6"),
        };
        var fileTime = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (path, content) in originals)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            File.SetLastWriteTimeUtc(path, fileTime);
            fileTime = fileTime.AddMinutes(7);
        }

        var before = Snapshot(archive);

        var fs = new PhysicalFileSystem();
        var zone = TimeZoneInfo.Utc;
        var workflow = new ImportWorkflow(fs, new MetadataExtractorReader(fs, FileKinds.Default, zone), new XxHasher(fs), zone);
        var settings = new ImportSettings([archive], true, archive, BuiltInPresets.DateOnly, TransferMode.Move, JobKind.Reorganize,
            Subfolders: SubfolderPolicy.Merge, PhotoDateGroups: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chosen });
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        Assert.Empty(scan.Errors);
        var plan = workflow.BuildPlan(settings, scan, new Dictionary<string, string>());
        Assert.False(plan.HasBlockingIssue);

        var context = new ExecutionContext(fs, new XxHasher(fs), appData, () => DateTime.UtcNow);
        var result = new Executor(context).Execute(plan.Plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(day15, "a.jpg")] = subA,
            [Path.Combine(day15, "a_1.jpg")] = otherA,
            [Path.Combine(day14, "b.jpg")] = chosen,
            [Path.Combine(day16, "c.jpg")] = deepC,
        };
        foreach (var (destination, original) in expected)
        {
            Assert.True(File.Exists(destination), $"대상이 없습니다: {destination}");
            Assert.Equal(originals[original], File.ReadAllBytes(destination));
            Assert.Equal(before[Path.GetRelativePath(archive, original)].Time, File.GetLastWriteTimeUtc(destination));
        }

        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), FilesOutsidePhotook(archive));
        Assert.False(Directory.Exists(Path.Combine(day15, "sub")));
        Assert.False(Directory.Exists(Path.Combine(day15, "other")));
        Assert.False(Directory.Exists(Path.Combine(day15, "keep")));
        Assert.False(Directory.Exists(Path.Combine(day16, "deep")));

        // 정리한 결과를 다시 계획하면 할 일이 없다(사진 날짜를 고른 사진도 이제 14일 폴더에 있다).
        var rerun = settings with { PhotoDateGroups = null };
        var rescan = await workflow.ScanAsync(rerun, null, CancellationToken.None);
        Assert.Empty(rescan.Errors);
        var replan = workflow.BuildPlan(rerun, rescan, new Dictionary<string, string>());
        Assert.Empty(replan.Plan.Operations);

        var undo = new Undoer(context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        var after = Snapshot(archive);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (relative, expectedFile) in before)
        {
            var actual = after[relative];
            Assert.Equal(expectedFile.Hash, actual.Hash);
            Assert.Equal(expectedFile.Time, actual.Time);
        }

        // 이번 실행이 만든 14일 폴더는 치웠고, 치웠던 폴더는 돌아왔다.
        Assert.False(Directory.Exists(day14));
        Assert.True(Directory.Exists(Path.Combine(day15, "sub")));
        Assert.True(Directory.Exists(Path.Combine(day15, "other")));
        Assert.True(Directory.Exists(Path.Combine(day15, "keep")));
        Assert.True(Directory.Exists(Path.Combine(day16, "deep", "er")));
    }

    /// <summary>아카이브 안(<c>.photook</c> 밖)의 파일마다 아카이브 기준 상대 경로 → 내용 해시와 수정 시각.</summary>
    private static Dictionary<string, (string Hash, DateTime Time)> Snapshot(string archive) =>
        Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories)
            .Where(p => IsOutsidePhotook(archive, p))
            .ToDictionary(p => Path.GetRelativePath(archive, p),
                p => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))), File.GetLastWriteTimeUtc(p)),
                StringComparer.OrdinalIgnoreCase);

    private static bool IsOutsidePhotook(string archive, string path) =>
        !path.StartsWith(Path.Combine(archive, ".photook") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static List<string> FilesOutsidePhotook(string archive) =>
        Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories)
            .Where(p => IsOutsidePhotook(archive, p))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
