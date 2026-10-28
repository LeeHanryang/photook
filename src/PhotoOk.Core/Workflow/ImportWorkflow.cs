using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Core.Workflow;

/// <summary>
/// 스캔 결과: 날짜별 세션과 스캔 중 생긴 오류, 무시한 파일. <see cref="SubfoldersNotScanned"/>는 하위 폴더를 포함하지 않았는데
/// 소스 폴더에 하위 폴더가 있었다(또는 확인하지 못했다)는 뜻이다(<see cref="ScanResult.SubfoldersNotScanned"/>).
/// </summary>
public sealed record ScanOutcome(
    IReadOnlyList<Session> Sessions,
    IReadOnlyList<ScanError> Errors,
    IReadOnlyList<string> Ignored,
    int FileCount,
    bool SubfoldersNotScanned);

/// <summary>계획과 실행 전 점검 결과.</summary>
public sealed record PlanOutcome(Plan Plan, IReadOnlyList<PreflightIssue> Issues)
{
    public bool HasBlockingIssue => Issues.Any(i => i.Severity == Severity.Error);
}

/// <summary>
/// 화면이 부르는 가져오기 흐름: 스캔, 날짜별 묶기, 계획. 이벤트 이름을 고칠 때마다 다시 스캔하거나 다시 해시하지 않고
/// <see cref="BuildPlan"/>만 다시 부르면 된다(해시는 이 인스턴스가 기억한다). 디스크는 읽기만 한다.
/// </summary>
public sealed class ImportWorkflow(IFileSystem fileSystem, IMetadataReader reader, IHasher hasher, TimeZoneInfo zone)
{
    private readonly CachingHasher _hasher = new(hasher, fileSystem);

    public Task<ScanOutcome> ScanAsync(ImportSettings settings, IProgress<int>? filesRead, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Task.Run(() =>
        {
            var scanner = new Scanner(fileSystem, reader, FileKinds.Default);
            var result = scanner.Scan(settings.Sources, settings.IncludeSubfolders, ct, filesRead);
            var sessions = SessionGrouper.GroupByDay(result.Groups, settings.Preset.DayStart, zone);
            var fileCount = result.Groups.Sum(g => g.AllFiles.Count());
            return new ScanOutcome(sessions, result.Errors, result.Ignored, fileCount, result.SubfoldersNotScanned);
        }, ct);
    }

    public PlanOutcome BuildPlan(ImportSettings settings, ScanOutcome scan, IReadOnlyDictionary<string, string> eventNames)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(eventNames);

        var plan = new Planner(fileSystem, _hasher).Create(
            new PlanRequest(scan.Sessions, settings.Preset, settings.ArchiveRoot, settings.Mode, zone, eventNames));
        return new PlanOutcome(plan, Preflight.Check(plan, fileSystem));
    }
}
