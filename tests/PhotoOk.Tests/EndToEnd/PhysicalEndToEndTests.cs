using System.IO;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;
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
        var plan = new Planner(fs, new XxHasher(fs)).Create(new PlanRequest(sessions, preset, archive, TransferMode.Copy, zone));

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

    private static List<string> FilesOutsidePhotook(string archive) =>
        Directory.EnumerateFiles(archive, "*", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(Path.Combine(archive, ".photook") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
