using System.Globalization;
using System.Text.RegularExpressions;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Layout;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Planning;

/// <summary>
/// 하루 단위 촬영 묶음을 아카이브 정리 작업 목록으로 바꾼다. 디스크는 읽기만 한다.
/// <list type="bullet">
/// <item>어떤 작업도 다른 파일 자리에 덮어쓰지 않는다. 이름이 겹치고 내용이 다르면 그룹 전체에 같은 순번(<c>_k</c>)을 붙인다.</item>
/// <item>다른 드라이브에서는 이동을 요청해도 복사한다. 재정리는 복사하지 않고 그 묶음을 제자리에 둔다.</item>
/// <item>이동으로 비워질 자리에 다른 파일이 들어오면, 비우는 파일을 먼저 임시 이름으로 옮겨 둔다.</item>
/// <item>제자리에 두기로 한 파일은 작업이 없고, 그 자리는 다른 파일에게 빈 자리가 아니다.</item>
/// </list>
/// </summary>
public sealed class Planner(IFileSystem fileSystem, IHasher hasher)
{
    private const string TempMarker = ".photook-tmp-";

    // [2026-07-08~07-09] 소풍, [2026-07-08~2026-07-09]
    private static readonly Regex MultiDayFolder = new(@"^\[\s*\d{4}[-.]\d{1,2}[-.]\d{1,2}\s*[~～–]\s*[\d\-.]+\s*\]", RegexOptions.CultureInvariant);

    public Plan Create(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ArchiveRoot));
        var archiveVolume = fileSystem.GetVolumeRoot(root);
        var renderer = new LayoutRenderer(request.Preset);
        var aliases = new DeviceAliasResolver(request.Preset.DeviceAliases);
        var mode = request.Kind == JobKind.Reorganize ? TransferMode.Move : request.Mode; // 재정리는 이동 고정

        var groups = new List<GroupPlan>();
        var stays = new List<PlannedSkip>();
        var hashes = new HashCache();
        foreach (var session in request.Sessions)
        {
            foreach (var group in session.Groups)
            {
                var reasons = ReviewDetector.Detect(group, request.Preset, aliases).Distinct().Order().ToList();
                var kept = KeptFolders(request, group);
                if (StayReason(request, group, reasons, kept, archiveVolume) is { } stay)
                {
                    stays.AddRange(group.AllFiles.Select(file =>
                    {
                        var source = Path.GetFullPath(file.Path);
                        return new PlannedSkip(source, source, stay);
                    }));
                    continue;
                }

                if (DuplicatesInArchive(request, group, hashes) is { } duplicates)
                {
                    stays.AddRange(duplicates);
                    continue;
                }

                var relative = renderer.RenderDirectory(new LayoutInput(session, group, reasons, request.Zone,
                    request.EventNames?.GetValueOrDefault(session.Id), OmitOriginalFolder: kept.Count > 0));
                var directory = TargetDirectory(request, group, root, relative, kept);
                if (!SamePath(directory, root) && !IsUnder(directory, root))
                {
                    throw new InvalidOperationException($"대상 폴더가 아카이브 밖입니다: {directory}");
                }

                var files = group.AllFiles.Select(file =>
                {
                    var source = Path.GetFullPath(file.Path);
                    var crossVolume = !string.Equals(fileSystem.GetVolumeRoot(source), archiveVolume,
                        StringComparison.OrdinalIgnoreCase);
                    var forced = mode == TransferMode.Move && crossVolume;
                    var kind = mode == TransferMode.Move && !crossVolume ? OperationKind.Move : OperationKind.Copy;
                    return new FilePlan(file, source, Path.GetFileName(source), kind, forced);
                }).ToList();

                groups.Add(new GroupPlan(group.Key, reasons, directory, files));
            }
        }

        // 이동으로 비워질 자리를 가정하고 계획을 세운 뒤, 실제로 이동 작업이 나온 원본만 남기며 다시 세운다.
        // 가정한 자리가 모두 실제로 비워질 때까지 반복한다(집합이 줄기만 하므로 반드시 끝난다).
        var vacating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                if (file.Kind == OperationKind.Move && !SamePath(file.Source, Path.Combine(group.Directory, file.Name)))
                {
                    vacating.Add(file.Source);
                }
            }
        }

        // 제자리에 남는 파일은 이동 후보(vacating)가 아니지만 그 자리는 차지한다.
        var sources = groups.SelectMany(g => g.Files).Select(f => f.Source)
            .Concat(stays.Select(s => s.Source))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Resolution resolution;
        while (true)
        {
            resolution = Resolve(groups, vacating, sources, hashes);
            var moved = resolution.FileOps
                .Where(op => op.Kind == OperationKind.Move)
                .Select(op => op.Source)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (vacating.IsSubsetOf(moved))
            {
                break;
            }

            vacating.IntersectWith(moved);
        }

        var multiDayNote = request.Kind == JobKind.Reorganize ? MultiDayNote(root, resolution.FileOps) : null;
        return Assemble(root, resolution, hashes.Unreadable, stays, CleanupRootsOf(request), multiDayNote);
    }

    /// <summary>
    /// 재정리에서 파일이 빠져나가는 <c>[시작~종료]</c> 폴더를 센다. 여러 날 묶기가 들어오기 전에는 이 폴더들이 날짜별로 나뉘므로 미리 알린다. 없으면 null.
    /// </summary>
    private static string? MultiDayNote(string root, IReadOnlyList<DraftOp> fileOps)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in fileOps.Where(op => op.Kind == OperationKind.Move))
        {
            for (var dir = Path.GetDirectoryName(op.Source); dir is not null && IsUnder(dir, root); dir = Path.GetDirectoryName(dir))
            {
                if (MultiDayFolder.IsMatch(Path.GetFileName(dir)))
                {
                    folders.Add(dir);
                }
            }
        }

        return folders.Count == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"여러 날을 묶은 폴더 {folders.Count}개가 날짜별로 나뉩니다. 여러 날 묶기가 들어오기 전에는 재정리에서 빼 주세요");
    }

    /// <summary>프리셋 폴더 아래에 붙일 하위 폴더 이름들. 가져오기와 합치기에는 없다.</summary>
    private static IReadOnlyList<string> KeptFolders(PlanRequest request, FileGroup group) =>
        request.Kind == JobKind.Import || request.Subfolders == SubfolderPolicy.Merge || request.KeptFoldersOf is null
            ? []
            : request.KeptFoldersOf(group);

    /// <summary>
    /// 묶음이 들어갈 폴더: 프리셋 폴더 아래에 유지할 폴더를 붙인다. 유지하는 작업에서 묶음이 이미 프리셋 폴더 안에 있으면 지금 폴더 그대로다
    /// (다시 정리해도 폴더가 깊어지지 않는다). 유지할 폴더가 있으면 <paramref name="relative"/>에 <c>{원래폴더}</c>가 빠져 있어 겹치지 않는다.
    /// </summary>
    private static string TargetDirectory(PlanRequest request, FileGroup group, string root, string relative, IReadOnlyList<string> kept)
    {
        var rendered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relative)));
        if (request.Kind != JobKind.Import && request.Subfolders == SubfolderPolicy.Keep
            && Path.GetDirectoryName(Path.GetFullPath(group.Primary.Path)) is { } current
            && (SamePath(current, rendered) || IsUnder(current, rendered)))
        {
            return current;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine([rendered, .. kept.Select(PathSanitizer.SanitizeSegment)])));
    }

    /// <summary>묶음 전체를 제자리에 둘 이유. 묶음이 갈라지지 않도록 파일 하나가 아니라 묶음 단위로 정한다.</summary>
    private SkipReason? StayReason(PlanRequest request, FileGroup group, IReadOnlyList<ReviewReason> reasons,
        IReadOnlyList<string> kept, string archiveVolume)
    {
        if (request.KeepReviewInPlace && reasons.Count > 0)
        {
            return SkipReason.ReviewKeptInPlace;
        }

        if (request.Subfolders == SubfolderPolicy.InPlace && kept.Count > 0)
        {
            return SkipReason.SubfolderKeptInPlace;
        }

        // 재정리는 이름 바꾸기만 한다. 다른 볼륨의 파일은 복사로 바꾸지 않고 건너뛴다.
        if (request.Kind == JobKind.Reorganize && group.AllFiles.Any(file => !string.Equals(
                fileSystem.GetVolumeRoot(Path.GetFullPath(file.Path)), archiveVolume, StringComparison.OrdinalIgnoreCase)))
        {
            return SkipReason.OtherVolume;
        }

        return null;
    }

    /// <summary>
    /// 흡수할 때 묶음의 파일이 모두 아카이브 어딘가에 같은 내용으로 있으면 파일별 건너뜀 기록을 돌려준다. 하나라도 없으면 null이다
    /// (일부만 중복이면 묶음이 갈라지지 않도록 전체를 정리한다). 건너뛴 파일은 출처 폴더에 그대로 남고 이동 후보가 아니다.
    /// 색인은 흡수에서만 쓴다.
    /// </summary>
    private List<PlannedSkip>? DuplicatesInArchive(PlanRequest request, FileGroup group, HashCache hashes)
    {
        if (request.Kind != JobKind.Absorb || request.Index is not { } index)
        {
            return null;
        }

        var skips = new List<PlannedSkip>();
        foreach (var file in group.AllFiles)
        {
            var source = Path.GetFullPath(file.Path);
            if (FindExisting(index, source, file.Length, hashes) is not { } existing)
            {
                return null;
            }

            skips.Add(new PlannedSkip(source, source, SkipReason.DuplicateInArchive, existing));
        }

        return skips;
    }

    /// <summary>크기가 같은 아카이브 파일만 해시로 비교해 첫 번째로 같은 내용인 경로를 돌려준다. 없으면 null.</summary>
    private string? FindExisting(ArchiveIndex index, string source, long length, HashCache hashes)
    {
        // 빈 파일은 내용이 모두 같아 사진이 이미 있다는 뜻이 아니므로 찾지 않는다.
        var candidates = length == 0 ? [] : index.WithLength(length);
        if (candidates.Count == 0)
        {
            return null;
        }

        var hash = HashOf(source, hashes);
        if (hash is null)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (!SamePath(candidate, source)
                && HashOf(candidate, hashes) is { } other && string.Equals(hash, other, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? CleanupRootsOf(PlanRequest request) =>
        request.Kind == JobKind.Reorganize || (request.Kind == JobKind.Absorb && request.Mode == TransferMode.Move)
            ? request.Roots
            : null;

    private Resolution Resolve(List<GroupPlan> groups, HashSet<string> vacating, HashSet<string> sources,
        HashCache hashes)
    {
        var reserved = new Dictionary<string, FilePlan>(StringComparer.OrdinalIgnoreCase);
        var fileOps = new List<DraftOp>();
        var skipped = new List<PlannedSkip>();
        var notes = new List<string>();

        foreach (var group in groups)
        {
            for (var k = 0; ; k++)
            {
                if (k > 100_000)
                {
                    throw new InvalidOperationException($"빈 이름을 찾지 못했습니다: {group.Files[0].Source}");
                }

                var local = new Dictionary<string, FilePlan>(StringComparer.OrdinalIgnoreCase);
                var decisions = new List<(FilePlan File, string Destination, Slot Slot, bool Fallback)>();
                var conflict = false;
                foreach (var file in group.Files)
                {
                    var (name, fallback) = NameFor(file.Name, group.Key, k);
                    var destination = Path.Combine(group.Directory, name);
                    var slot = Evaluate(file, destination, k, reserved, local, vacating, sources, hashes);
                    if (slot == Slot.Different)
                    {
                        conflict = true;
                        break;
                    }

                    if (slot == Slot.Empty)
                    {
                        local[destination] = file;
                    }

                    decisions.Add((file, destination, slot, fallback));
                }

                if (conflict)
                {
                    continue;
                }

                foreach (var (file, destination, slot, fallback) in decisions)
                {
                    switch (slot)
                    {
                        case Slot.InPlace:
                            skipped.Add(new PlannedSkip(file.Source, destination, SkipReason.AlreadyInPlace));
                            break;
                        case Slot.SameOnDisk:
                            skipped.Add(new PlannedSkip(file.Source, destination, SkipReason.DuplicateAtDestination));
                            break;
                        case Slot.SameInPlan:
                            skipped.Add(new PlannedSkip(file.Source, destination, SkipReason.DuplicateInPlan));
                            break;
                        case Slot.Empty:
                            reserved[destination] = file;
                            fileOps.Add(new DraftOp(file.Kind, file.Source, destination, file.Info.Length, group.Reasons,
                                file.Forced));
                            if (fallback)
                            {
                                notes.Add($"그룹 이름으로 시작하지 않는 파일이라 확장자 앞에 순번을 붙임: {file.Name} → {Path.GetFileName(destination)}");
                            }

                            break;
                        default:
                            throw new InvalidOperationException($"처리하지 않은 상태: {slot}");
                    }
                }

                break;
            }
        }

        return new Resolution(fileOps, skipped, notes);
    }

    /// <summary>
    /// 대상 경로의 상태. 이 계획이 예약한 자리는 예약한 원본과, 디스크의 파일은 그 파일과 비교한다.
    /// 이 계획에서 이동으로 비워질 자리는 비어 있는 것으로 본다. 그 밖에 이 계획의 원본 자리는
    /// 디스크에서 보이지 않더라도(스캔 뒤 사라짐) 비어 있다고 보지 않는다.
    /// </summary>
    private Slot Evaluate(FilePlan file, string destination, int k,
        Dictionary<string, FilePlan> reserved, Dictionary<string, FilePlan> local,
        HashSet<string> vacating, HashSet<string> sources, HashCache hashes)
    {
        if (k == 0 && SamePath(file.Source, destination))
        {
            return Slot.InPlace;
        }

        if (local.TryGetValue(destination, out var planned) || reserved.TryGetValue(destination, out planned))
        {
            return SameContent(file.Source, file.Info.Length, planned.Source, planned.Info.Length, hashes)
                ? Slot.SameInPlan
                : Slot.Different;
        }

        if (fileSystem.DirectoryExists(destination))
        {
            return Slot.Different;
        }

        if (vacating.Contains(destination))
        {
            return Slot.Empty;
        }

        if (fileSystem.FileExists(destination))
        {
            var existing = fileSystem.GetFile(destination);
            return SameContent(file.Source, file.Info.Length, destination, existing.Length, hashes)
                ? Slot.SameOnDisk
                : Slot.Different;
        }

        return sources.Contains(destination) ? Slot.Different : Slot.Empty;
    }

    /// <summary>
    /// 크기가 같을 때만 해시를 비교한다. 읽지 못한 파일(입출력 오류·권한)이 있으면 같은 파일이라고 확신할 수 없으므로
    /// "다른 파일"로 본다(덮어쓰지 않고 순번을 붙인다).
    /// </summary>
    private bool SameContent(string a, long aLength, string b, long bLength, HashCache hashes)
    {
        if (aLength != bLength)
        {
            return false;
        }

        var hashA = HashOf(a, hashes);
        var hashB = HashOf(b, hashes);
        return hashA is not null && hashB is not null && string.Equals(hashA, hashB, StringComparison.Ordinal);
    }

    /// <summary>해시를 돌려준다. 파일을 읽지 못하면 null(경로는 <see cref="HashCache.Unreadable"/>에 한 번만 남는다).</summary>
    private string? HashOf(string path, HashCache hashes)
    {
        if (!hashes.Values.TryGetValue(path, out var hash))
        {
            try
            {
                hash = hasher.Hash(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                hash = null;
                hashes.Unreadable.Add(path);
            }

            hashes.Values[path] = hash;
        }

        return hash;
    }

    private Plan Assemble(string root, Resolution resolution, IReadOnlyList<string> unreadable,
        IReadOnlyList<PlannedSkip> stays, IReadOnlyList<string>? cleanupRoots, string? multiDayNote)
    {
        var fileOps = resolution.FileOps;

        var destinationOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < fileOps.Count; i++)
        {
            destinationOwner[fileOps[i].Destination] = i;
        }

        // 다른 작업의 대상 자리를 비우는 이동은 임시 이름으로 먼저 옮겨 둔다.
        var needsTemp = Enumerable.Range(0, fileOps.Count)
            .Where(i => fileOps[i].Kind == OperationKind.Move
                && destinationOwner.TryGetValue(fileOps[i].Source, out var owner) && owner != i)
            .ToList();

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in fileOps)
        {
            for (var dir = Path.GetDirectoryName(op.Destination); dir is not null && IsUnder(dir, root);
                 dir = Path.GetDirectoryName(dir))
            {
                if (!fileSystem.DirectoryExists(dir))
                {
                    directories.Add(dir);
                }
            }
        }

        var operations = new List<PlannedOperation>();
        foreach (var dir in directories.Order(StringComparer.OrdinalIgnoreCase))
        {
            operations.Add(new PlannedOperation(NextId(operations), OperationKind.CreateDirectory, null, dir, 0, []));
        }

        var tempOf = new Dictionary<int, string>();
        foreach (var i in needsTemp)
        {
            var id = NextId(operations);
            var temp = fileOps[i].Source + TempMarker + id;
            tempOf[i] = temp;
            operations.Add(new PlannedOperation(id, OperationKind.Move, fileOps[i].Source, temp, fileOps[i].Length, []));
        }

        for (var i = 0; i < fileOps.Count; i++)
        {
            var op = fileOps[i];
            var source = tempOf.TryGetValue(i, out var temp) ? temp : op.Source;
            operations.Add(new PlannedOperation(NextId(operations), op.Kind, source, op.Destination, op.Length, op.Reasons));
        }

        var notes = new List<string>();
        var forced = fileOps.Count(op => op.Forced);
        if (forced > 0)
        {
            notes.Add($"다른 드라이브라 복사로 처리: {forced}개");
        }

        if (multiDayNote is not null)
        {
            notes.Add(multiDayNote);
        }

        notes.AddRange(unreadable.Select(path => "파일을 읽지 못해 다른 파일로 처리했습니다: " + path));
        notes.AddRange(resolution.Notes);

        return new Plan(root, operations, [.. stays, .. resolution.Skipped], notes, cleanupRoots);
    }

    private static string NextId(List<PlannedOperation> operations) =>
        "op-" + (operations.Count + 1).ToString("000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// k번째 후보 이름. k = 0이면 원래 이름, 아니면 그룹 키 바로 뒤에 <c>_k</c>를 넣는다.
    /// 이름이 그룹 키로 시작하지 않으면 마지막 확장자 앞에 붙이고 <c>Fallback</c>을 true로 돌려준다.
    /// </summary>
    internal static (string Name, bool Fallback) NameFor(string fileName, string key, int k)
    {
        if (k == 0)
        {
            return (fileName, false);
        }

        var suffix = "_" + k.ToString(CultureInfo.InvariantCulture);
        if (key.Length > 0 && fileName.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return (fileName[..key.Length] + suffix + fileName[key.Length..], false);
        }

        return (Path.GetFileNameWithoutExtension(fileName) + suffix + Path.GetExtension(fileName), true);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/>가 <paramref name="directory"/>보다 아래(같지 않음)에 있는가.</summary>
    private static bool IsUnder(string path, string directory)
    {
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>계획을 세우는 동안 계산한 해시. 읽지 못한 파일은 null로 기억해 다시 읽지 않는다.</summary>
    private sealed class HashCache
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Unreadable { get; } = [];
    }

    private enum Slot { Empty, InPlace, SameOnDisk, SameInPlan, Different }

    private sealed record FilePlan(PhotoInfo Info, string Source, string Name, OperationKind Kind, bool Forced);

    private sealed record GroupPlan(string Key, IReadOnlyList<ReviewReason> Reasons, string Directory, List<FilePlan> Files);

    private sealed record DraftOp(OperationKind Kind, string Source, string Destination, long Length,
        IReadOnlyList<ReviewReason> Reasons, bool Forced);

    private sealed record Resolution(List<DraftOp> FileOps, List<PlannedSkip> Skipped, List<string> Notes);
}
