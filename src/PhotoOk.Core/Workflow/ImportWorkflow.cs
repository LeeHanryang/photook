using PhotoOk.Core.Execution;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Sessions;

namespace PhotoOk.Core.Workflow;

/// <summary>
/// 스캔 결과: 날짜별 세션과 스캔 중 생긴 오류, 무시한 파일. <see cref="SubfoldersNotScanned"/>는 하위 폴더를 포함하지 않았는데
/// 소스 폴더에 하위 폴더가 있었다(또는 확인하지 못했다)는 뜻이다(<see cref="ScanResult.SubfoldersNotScanned"/>).
/// <see cref="Index"/>는 흡수 스캔에서만 만든 아카이브 색인(메모리에만 둔다), <see cref="Groups"/>는 사진 정보를 다시 읽지 않고
/// 세션을 다시 만들 때 쓰는 스캔한 파일 묶음이다(없으면 세션 안의 묶음을 쓴다).
/// </summary>
public sealed record ScanOutcome(
    IReadOnlyList<Session> Sessions,
    IReadOnlyList<ScanError> Errors,
    IReadOnlyList<string> Ignored,
    int FileCount,
    bool SubfoldersNotScanned,
    ArchiveIndex? Index = null,
    IReadOnlyList<FileGroup>? Groups = null);

/// <summary>계획과 실행 전 점검 결과.</summary>
public sealed record PlanOutcome(Plan Plan, IReadOnlyList<PreflightIssue> Issues)
{
    public bool HasBlockingIssue => Issues.Any(i => i.Severity == Severity.Error);
}

/// <summary>
/// 화면이 부르는 가져오기 흐름: 스캔, 날짜별 묶기, 계획. 이벤트 이름을 고칠 때마다 다시 스캔하거나 다시 해시하지 않고
/// <see cref="BuildPlan"/>만 다시 부르면 된다(해시는 이 인스턴스가 기억한다). 디스크는 읽기만 한다.
/// </summary>
public sealed class ImportWorkflow(IFileSystem fileSystem, IMetadataReader reader, IHasher hasher, TimeZoneInfo zone,
    string? ignoredNamesPath = null)
{
    private readonly CachingHasher _hasher = new(hasher, fileSystem);

    /// <param name="checkingArchive">흡수에서 파일을 다 읽고 아카이브 색인을 만들기 시작할 때 true를 알린다(그동안 읽은 파일 수는 멈춰 있다).</param>
    public Task<ScanOutcome> ScanAsync(ImportSettings settings, IProgress<int>? filesRead, CancellationToken ct,
        IProgress<bool>? checkingArchive = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Task.Run(() =>
        {
            var scanner = new Scanner(fileSystem, reader, FileKinds.Default);
            var result = scanner.Scan(settings.Sources, settings.IncludeSubfolders, ct, filesRead);
            var sessions = GroupSessions(settings, result.Groups);
            var fileCount = result.Groups.Sum(g => g.AllFiles.Count());

            ArchiveIndex? index = null;
            IReadOnlyList<ScanError> errors = result.Errors;
            if (settings.Kind == JobKind.Absorb)
            {
                checkingArchive?.Report(true);
                var indexErrors = new List<ScanError>();
                index = ArchiveIndex.Build(fileSystem, settings.ArchiveRoot,
                    (path, error) => indexErrors.Add(IndexError(path, error)), ct);
                if (indexErrors.Count > 0)
                {
                    errors = [.. result.Errors, .. indexErrors];
                }
            }

            return new ScanOutcome(sessions, errors, result.Ignored, fileCount, result.SubfoldersNotScanned, index, result.Groups);
        }, ct);
    }

    /// <summary>
    /// 사진 정보를 다시 읽지 않고 <paramref name="settings"/>대로 촬영 묶기(하루 시작 시각, 폴더 이름 읽기, <c>_</c> 나누기)만 다시 한다.
    /// 색인·오류·무시한 파일은 그대로다. 제외 단어 파일은 다시 읽으므로 방금 제외한 이름도 제외된 채로 남는다.
    /// </summary>
    public ScanOutcome Regroup(ImportSettings settings, ScanOutcome scan)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scan);

        return scan with { Sessions = GroupSessions(settings, GroupsOf(scan)) };
    }

    private static IReadOnlyList<FileGroup> GroupsOf(ScanOutcome scan) =>
        scan.Groups ?? [.. scan.Sessions.SelectMany(s => s.Groups)];

    // 색인을 만들다 알린 경로는 폴더(열 수 없음, 연결 폴더, 열거가 끊김)이거나 크기를 읽지 못한 파일이다.
    private ScanError IndexError(string path, Exception error) =>
        fileSystem.FileExists(path) ? new ScanError(path, "파일을 읽지 못했습니다: " + path) : Scanner.FolderError(path, error);

    // 기기 이름은 스캔 전체의 사진에서 모은다: 어느 폴더의 사진에 EXIF가 없어도 다른 폴더의 사진 모델로 그 폴더 이름이 기기 이름임을 안다.
    private IReadOnlyList<Session> GroupSessions(ImportSettings settings, IReadOnlyList<FileGroup> groups)
    {
        if (settings.Kind == JobKind.Import)
        {
            // 가져오기는 이름을 읽지 않으므로 제외 단어 파일도 기기 어휘도 필요 없다.
            return SessionGrouper.GroupByDay(groups, settings.Preset.DayStart, zone);
        }

        var reader = NameReader(settings, groups);
        return SessionGrouper.GroupByDay(groups, settings.Preset.DayStart, zone, reader.Read, reader.IsFormatName);
    }

    private EventNameReader NameReader(ImportSettings settings, IReadOnlyList<FileGroup> groups)
    {
        var ignored = ignoredNamesPath is null ? IgnoredFolderNames.Empty : IgnoredFolderNames.Load(fileSystem, ignoredNamesPath);
        var devices = DeviceVocabulary.From(groups.SelectMany(g => g.AllFiles), settings.Preset.DeviceAliases);
        return new EventNameReader(settings.Preset, settings.Kind, settings.UseFolderNames, settings.Sources,
            new FolderNameContext(devices, ignored), settings.SplitUnderscores, settings.ArchiveRoot);
    }

    public PlanOutcome BuildPlan(ImportSettings settings, ScanOutcome scan, IReadOnlyDictionary<string, string> eventNames)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(eventNames);

        // 판단 필요 파일을 제자리에 두는 것은 재정리뿐이다(설정의 기본값은 켬이므로 여기서 가른다).
        var plan = new Planner(fileSystem, _hasher).Create(
            new PlanRequest(scan.Sessions, settings.Preset, settings.ArchiveRoot, settings.Mode, zone, eventNames,
                settings.Kind, settings.Sources, scan.Index,
                settings.Kind == JobKind.Reorganize && settings.KeepReviewInPlace,
                settings.Subfolders, KeptFoldersOf(settings, scan)));
        return new PlanOutcome(plan, Preflight.Check(plan, fileSystem));
    }

    // 묶을 때와 같은 어휘로 판별해야 이름으로 고른 폴더와 유지할 폴더가 어긋나지 않는다.
    private Func<FileGroup, IReadOnlyList<string>>? KeptFoldersOf(ImportSettings settings, ScanOutcome scan) =>
        settings.Kind == JobKind.Import || settings.Subfolders == SubfolderPolicy.Merge
            ? null
            : NameReader(settings, GroupsOf(scan)).KeptFolders;
}
