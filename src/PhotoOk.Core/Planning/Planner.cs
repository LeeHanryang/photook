using System.Globalization;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Layout;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Presets;

namespace PhotoOk.Core.Planning;

/// <summary>
/// 하루 단위 촬영 묶음을 아카이브 정리 작업 목록으로 바꾼다. 디스크는 읽기만 한다.
/// <list type="bullet">
/// <item>어떤 작업도 다른 파일 자리에 덮어쓰지 않는다. 이름이 겹치고 내용이 다르면 그룹 전체에 같은 순번(<c>_k</c>)을 붙인다.</item>
/// <item>다른 드라이브에서는 이동을 요청해도 복사한다.</item>
/// <item>이동으로 비워질 자리에 다른 파일이 들어오면, 비우는 파일을 먼저 임시 이름으로 옮겨 둔다.</item>
/// </list>
/// </summary>
public sealed class Planner(IFileSystem fileSystem, IHasher hasher)
{
    private const string TempMarker = ".photook-tmp-";

    public Plan Create(PlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ArchiveRoot));
        var archiveVolume = fileSystem.GetVolumeRoot(root);
        var renderer = new LayoutRenderer(request.Preset);
        var aliases = new DeviceAliasResolver(request.Preset.DeviceAliases);

        var groups = new List<GroupPlan>();
        foreach (var session in request.Sessions)
        {
            foreach (var group in session.Groups)
            {
                var reasons = ReviewDetector.Detect(group, request.Preset, aliases).Distinct().Order().ToList();
                var relative = renderer.RenderDirectory(new LayoutInput(session, group, reasons, request.Zone,
                    request.EventNames?.GetValueOrDefault(session.Id)));
                var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, relative)));
                if (!SamePath(directory, root) && !IsUnder(directory, root))
                {
                    throw new InvalidOperationException($"대상 폴더가 아카이브 밖입니다: {directory}");
                }

                var files = group.AllFiles.Select(file =>
                {
                    var source = Path.GetFullPath(file.Path);
                    var crossVolume = !string.Equals(fileSystem.GetVolumeRoot(source), archiveVolume,
                        StringComparison.OrdinalIgnoreCase);
                    var forced = request.Mode == TransferMode.Move && crossVolume;
                    var kind = request.Mode == TransferMode.Move && !crossVolume ? OperationKind.Move : OperationKind.Copy;
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

        var sources = groups.SelectMany(g => g.Files).Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        return Assemble(root, resolution);
    }

    private Resolution Resolve(List<GroupPlan> groups, HashSet<string> vacating, HashSet<string> sources,
        Dictionary<string, string> hashes)
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
        HashSet<string> vacating, HashSet<string> sources, Dictionary<string, string> hashes)
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

    /// <summary>크기가 같을 때만 해시를 비교한다.</summary>
    private bool SameContent(string a, long aLength, string b, long bLength, Dictionary<string, string> hashes)
    {
        if (aLength != bLength)
        {
            return false;
        }

        return string.Equals(HashOf(a, hashes), HashOf(b, hashes), StringComparison.Ordinal);
    }

    private string HashOf(string path, Dictionary<string, string> hashes)
    {
        if (!hashes.TryGetValue(path, out var hash))
        {
            hash = hasher.Hash(path);
            hashes[path] = hash;
        }

        return hash;
    }

    private Plan Assemble(string root, Resolution resolution)
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

        notes.AddRange(resolution.Notes);

        return new Plan(root, operations, resolution.Skipped, notes);
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

    private enum Slot { Empty, InPlace, SameOnDisk, SameInPlan, Different }

    private sealed record FilePlan(PhotoInfo Info, string Source, string Name, OperationKind Kind, bool Forced);

    private sealed record GroupPlan(string Key, IReadOnlyList<ReviewReason> Reasons, string Directory, List<FilePlan> Files);

    private sealed record DraftOp(OperationKind Kind, string Source, string Destination, long Length,
        IReadOnlyList<ReviewReason> Reasons, bool Forced);

    private sealed record Resolution(List<DraftOp> FileOps, List<PlannedSkip> Skipped, List<string> Notes);
}
