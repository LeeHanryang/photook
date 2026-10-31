using System.IO;
using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;
using ExecutionContext = PhotoOk.Core.Execution.ExecutionContext;

namespace PhotoOk.Tests.EndToEnd;

/// <summary>
/// Windows의 폴더에는 읽기 전용 표시가 흔히 붙어 있다(폴더에서는 대개 표시일 뿐이고 탐색기는 그런 폴더도 지운다). 가짜 파일 시스템은 이것을 흉내 내지 않으므로
/// 실제 임시 폴더에서 스캔 → 계획 → 실행 → 되돌리기를 돌려 본다.
/// </summary>
public sealed class PhysicalReadOnlyFolderTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly DateTime Shot = new(2026, 10, 14, 9, 30, 0);
    private static readonly Preset EventPreset = new("테스트", TimeOnly.MinValue,
        [
            new LayoutBranch("판단 필요", BranchCondition.NeedsReview, [new LevelTemplate("판단 필요"), new LevelTemplate("{사유}"), new LevelTemplate("{원래폴더}")]),
            new LayoutBranch("기본", BranchCondition.Always, [new LevelTemplate("{연도}"), new LevelTemplate("[{기간}] {이벤트}"), new LevelTemplate("{기기}"), new LevelTemplate("{포맷}")]),
        ],
        [], BuiltInPresets.DateOnly.FormatGroups);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "photook-tests", Guid.NewGuid().ToString("N"));
    private readonly string _archive;
    private readonly string _picked;
    private readonly PhysicalFileSystem _fs = new();
    private readonly ExecutionContext _context;
    private readonly Dictionary<string, (byte[] Content, DateTime Time)> _photos = new(StringComparer.OrdinalIgnoreCase);

    public PhysicalReadOnlyFolderTests()
    {
        _archive = Path.Combine(_root, "archive");
        _picked = Path.Combine(_archive, "모음");
        Directory.CreateDirectory(_picked);
        _context = new ExecutionContext(_fs, new XxHasher(_fs), Path.Combine(_root, "appdata"), () => DateTime.UtcNow);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 임시 폴더 정리 실패는 테스트 결과에 영향을 주지 않는다.
        }
    }

    private static void SetReadOnly(string path) =>
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

    private static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

    private string AddPhoto(string folder, string name, int minute)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        var content = TestImages.Jpeg(Shot.AddMinutes(minute), "Canon", "EOS R6");
        var time = new DateTime(2020, 5, 1, 3, 4, 5, DateTimeKind.Utc).AddMinutes(minute);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, time);
        _photos[path] = (content, time);
        return path;
    }

    // 고른 폴더 아래의 모든 폴더(와 고른 폴더, 아카이브)에 읽기 전용 표시를 붙인다.
    private void MarkFoldersReadOnly(string under)
    {
        foreach (var folder in Directory.EnumerateDirectories(under, "*", SearchOption.AllDirectories))
        {
            SetReadOnly(folder);
        }

        SetReadOnly(under);
    }

    private async Task<PlanOutcome> PlanAsync(ImportSettings settings)
    {
        var workflow = new ImportWorkflow(_fs, new MetadataExtractorReader(_fs, FileKinds.Default, Zone), new XxHasher(_fs), Zone);
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        Assert.Empty(scan.Errors);
        var names = scan.Sessions.Where(s => s.SuggestedEventName is not null).ToDictionary(s => s.Id, s => s.SuggestedEventName!);
        return workflow.BuildPlan(settings, scan, names);
    }

    private ImportSettings Reorganize() =>
        new([_picked], true, _archive, EventPreset, TransferMode.Move, JobKind.Reorganize, UseFolderNames: true);

    private void AssertPhotosAreBack()
    {
        foreach (var (path, (content, time)) in _photos)
        {
            Assert.True(File.Exists(path), $"돌아오지 않았습니다: {path}");
            Assert.Equal(content, File.ReadAllBytes(path));
            Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public async Task Reorganize_ReadOnlyFolders_AreCleaned_AndUndoRestoresFiles()
    {
        var family = Path.Combine(_picked, "가족");
        var deep = Path.Combine(_picked, "소풍", "공원");
        var withMemo = Path.Combine(_picked, "모임");
        var withHiddenFile = Path.Combine(_picked, "나들이");
        AddPhoto(family, "a.jpg", 0);
        AddPhoto(deep, "b.jpg", 1);
        AddPhoto(withMemo, "c.jpg", 2);
        AddPhoto(withHiddenFile, "d.jpg", 3);
        var memo = Path.Combine(withMemo, "memo.txt");
        var hidden = Path.Combine(withHiddenFile, "desktop.ini");
        File.WriteAllText(memo, "메모");
        File.WriteAllText(hidden, "꾸미기");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);
        MarkFoldersReadOnly(_picked);
        SetReadOnly(_archive);

        var outcome = await PlanAsync(Reorganize());
        Assert.False(outcome.HasBlockingIssue);
        var result = new Executor(_context).Execute(outcome.Plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        Assert.Empty(result.Warnings);

        // 빈 폴더는 위로 올라가며 치우고, 고른 폴더와 파일이 남은 폴더는 표시까지 그대로 둔다.
        Assert.False(Directory.Exists(family));
        Assert.False(Directory.Exists(Path.Combine(_picked, "소풍")));
        Assert.True(IsReadOnly(_picked));
        Assert.True(IsReadOnly(withMemo));
        Assert.True(IsReadOnly(withHiddenFile));
        Assert.Equal("메모", File.ReadAllText(memo));
        Assert.True(File.GetAttributes(hidden).HasFlag(FileAttributes.Hidden));
        var moved = Path.Combine(_archive, "2026", "[2026-10-14] 가족", "EOS R6", "JPG", "a.jpg");
        Assert.Equal(_photos[Path.Combine(family, "a.jpg")].Content, File.ReadAllBytes(moved));
        Assert.Equal(_photos[Path.Combine(family, "a.jpg")].Time, File.GetLastWriteTimeUtc(moved));

        var undo = new Undoer(_context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        AssertPhotosAreBack();
        Assert.False(Directory.Exists(Path.Combine(_archive, "2026"))); // 이번 실행이 만든 폴더는 치웠다
        Assert.Equal("메모", File.ReadAllText(memo));

        // 치웠다가 되살린 폴더는 새 폴더라 표시가 돌아오지 않는다(파일에는 영향이 없다). 남아 있던 폴더는 그대로다.
        Assert.False(IsReadOnly(family));
        Assert.True(IsReadOnly(withMemo));
    }

    [Fact]
    public async Task Reorganize_ExcludedCardAndMultiDayFolder_StayUntouched_OthersMove_UndoRestores()
    {
        // 뺄 촬영: 사이드카가 딸린 묶음과 다른 묶음을 한 폴더에, 옮길 파일은 다른 폴더에 둔다.
        var excludedFolder = Path.Combine(_picked, "가족");
        var excludedOne = AddPhoto(excludedFolder, "keep1.jpg", 0);
        var excludedTwo = AddPhoto(excludedFolder, "keep2.jpg", 1);
        var excludedSidecar = Path.Combine(excludedFolder, "keep1.xmp");
        File.WriteAllText(excludedSidecar, "<x/>");
        var moving = AddPhoto(Path.Combine(_picked, "소풍", "공원"), "move1.jpg", 2);
        var movingTwo = AddPhoto(Path.Combine(_picked, "모임"), "move2.jpg", 3);

        // 고른 폴더 아래의 여러 날 폴더(직속·하위 파일)
        var multiDay = Path.Combine(_picked, "[2026-10-14~10-15] 나들이");
        var multiDayDirect = AddPhoto(multiDay, "day1.jpg", 4);
        var multiDayDeep = AddPhoto(Path.Combine(multiDay, "오후"), "day2.jpg", 5);
        MarkFoldersReadOnly(_picked);
        SetReadOnly(_archive);

        // 파일과 표시, 시각까지 그대로인지 볼 사진들
        var stayed = new[] { excludedOne, excludedTwo, multiDayDirect, multiDayDeep };
        var settings = Reorganize();
        var workflow = new ImportWorkflow(_fs, new MetadataExtractorReader(_fs, FileKinds.Default, Zone), new XxHasher(_fs), Zone);
        var scan = await workflow.ScanAsync(settings, null, CancellationToken.None);
        Assert.Empty(scan.Errors);
        var names = scan.Sessions.Where(s => s.SuggestedEventName is not null).ToDictionary(s => s.Id, s => s.SuggestedEventName!);
        var excludedCard = scan.Sessions.Single(s => s.SuggestedEventName == "가족");
        var excluded = excludedCard.Groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, excluded.Count);

        var outcome = workflow.BuildPlan(settings, scan, names, excluded);

        Assert.False(outcome.HasBlockingIssue);
        Assert.Equal(3, outcome.Plan.Skipped.Count(s => s.Reason == SkipReason.ExcludedByUser));
        Assert.Equal(2, outcome.Plan.Skipped.Count(s => s.Reason == SkipReason.MultiDayFolderKept));
        Assert.Equal("여러 날 폴더 1개는 옮기지 않습니다", outcome.Plan.MultiDayNotice);
        var touched = stayed.Append(excludedSidecar).ToList();
        Assert.DoesNotContain(outcome.Plan.Operations, op => touched.Contains(op.Source ?? "", StringComparer.OrdinalIgnoreCase)
                                                             || touched.Contains(op.Destination, StringComparer.OrdinalIgnoreCase));

        var result = new Executor(_context).Execute(outcome.Plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        Assert.Empty(result.Warnings);
        foreach (var path in stayed)
        {
            Assert.Equal(_photos[path].Content, File.ReadAllBytes(path));
            Assert.Equal(_photos[path].Time, File.GetLastWriteTimeUtc(path));
        }

        Assert.Equal("<x/>", File.ReadAllText(excludedSidecar));
        Assert.False(File.Exists(moving));
        Assert.False(File.Exists(movingTwo));
        Assert.Equal(_photos[moving].Content, File.ReadAllBytes(Assert.Single(Directory.EnumerateFiles(_archive, "move1.jpg", SearchOption.AllDirectories))));
        Assert.Equal(_photos[movingTwo].Content, File.ReadAllBytes(Assert.Single(Directory.EnumerateFiles(_archive, "move2.jpg", SearchOption.AllDirectories))));

        // 빠져나간 폴더만 치우고, 뺀 파일과 여러 날 폴더의 폴더는 표시까지 그대로 남는다.
        Assert.False(Directory.Exists(Path.Combine(_picked, "모임")));
        Assert.False(Directory.Exists(Path.Combine(_picked, "소풍")));
        Assert.True(IsReadOnly(excludedFolder));
        Assert.True(IsReadOnly(multiDay));
        Assert.True(IsReadOnly(Path.Combine(multiDay, "오후")));

        var undo = new Undoer(_context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        AssertPhotosAreBack();
        Assert.Equal("<x/>", File.ReadAllText(excludedSidecar));
        Assert.True(IsReadOnly(excludedFolder));
        Assert.True(IsReadOnly(multiDay));
        Assert.False(Directory.Exists(Path.Combine(_archive, "2026")));
    }

    [Fact]
    public async Task Absorb_Copy_ReadOnlySource_UndoSetsCopiesAside()
    {
        var inbox = Path.Combine(_root, "흩어짐");
        var photo = AddPhoto(Path.Combine(inbox, "가족"), "a.jpg", 0);
        SetReadOnly(photo);
        MarkFoldersReadOnly(inbox);

        var outcome = await PlanAsync(new ImportSettings([inbox], true, _archive, EventPreset, TransferMode.Copy, JobKind.Absorb, UseFolderNames: true));
        var result = new Executor(_context).Execute(outcome.Plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Failures);
        Assert.Empty(result.Warnings);
        var copy = Path.Combine(_archive, "2026", "[2026-10-14] 가족", "EOS R6", "JPG", "a.jpg");
        Assert.Equal(_photos[photo].Content, File.ReadAllBytes(copy));
        Assert.Equal(_photos[photo].Time, File.GetLastWriteTimeUtc(copy));

        SetReadOnly(copy); // 복사본에 표시가 붙어도 되돌리기가 치운다
        var undo = new Undoer(_context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        AssertPhotosAreBack();
        Assert.True(IsReadOnly(photo));
        Assert.False(File.Exists(copy));
        Assert.False(Directory.Exists(Path.Combine(_archive, "2026")));
    }

    [Fact]
    public async Task Undo_ReadOnlyFoldersCreatedByRun_AreRemoved()
    {
        // 실행이 만든 폴더에 나중에 표시가 붙어도(동기화 프로그램 등) 되돌리기는 같은 입구로 치운다.
        AddPhoto(Path.Combine(_picked, "가족"), "a.jpg", 0);
        var outcome = await PlanAsync(Reorganize());
        var result = new Executor(_context).Execute(outcome.Plan);
        Assert.Empty(result.Warnings);
        MarkFoldersReadOnly(Path.Combine(_archive, "2026"));

        var undo = new Undoer(_context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        AssertPhotosAreBack();
        Assert.False(Directory.Exists(Path.Combine(_archive, "2026")));
    }

    [Fact]
    public async Task Reorganize_ReadOnlyFile_MovesAndComesBack_WithItsAttribute()
    {
        // 파일의 표시는 건드리지 않는다.
        var photo = AddPhoto(Path.Combine(_picked, "가족"), "a.jpg", 0);
        SetReadOnly(photo);
        MarkFoldersReadOnly(_picked);

        var outcome = await PlanAsync(Reorganize());
        var result = new Executor(_context).Execute(outcome.Plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.True(IsReadOnly(Path.Combine(_archive, "2026", "[2026-10-14] 가족", "EOS R6", "JPG", "a.jpg")));

        var undo = new Undoer(_context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        AssertPhotosAreBack();
        Assert.True(IsReadOnly(photo));
    }

    [Fact]
    public async Task Absorb_ReadOnlyFolders_AreCleaned()
    {
        var inbox = Path.Combine(_root, "흩어짐");
        var family = Path.Combine(inbox, "가족");
        AddPhoto(family, "a.jpg", 0);
        MarkFoldersReadOnly(inbox);

        var outcome = await PlanAsync(new ImportSettings([inbox], true, _archive, EventPreset, TransferMode.Move, JobKind.Absorb, UseFolderNames: true));
        var result = new Executor(_context).Execute(outcome.Plan);

        Assert.Equal(RunOutcome.Completed, result.Outcome);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(family));
        Assert.True(IsReadOnly(inbox));

        var undo = new Undoer(_context).Undo(result.JournalPath!);

        Assert.Empty(undo.Skipped);
        AssertPhotosAreBack();
    }
}
