using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;

namespace PhotoOk.Core.Scanning;

/// <summary>
/// 소스 폴더에서 미디어 파일을 찾아, 사진마다 딸린 파일(RAW 사이드카, 라이브 포토 영상)을 묶는다.
/// 같은 폴더 안에서 이름(확장자 제외, 대소문자 무시)이 같은 파일끼리만 묶는다.
/// </summary>
public sealed class Scanner(IFileSystem fileSystem, IMetadataReader reader, FileKinds kinds)
{
    private const string PhotookFolder = ".photook";
    private const string TempMarker = ".photook-tmp";
    internal const string InaccessibleFolderMessage = "폴더를 열 수 없어 건너뛰었습니다: ";

    public ScanResult Scan(IReadOnlyList<string> sourceRoots, bool recursive, CancellationToken ct = default,
        IProgress<int>? filesRead = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRoots);

        var errors = new List<ScanError>();
        var ignored = new List<string>();
        var infos = new List<PhotoInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesDone = 0;
        var subfoldersNotScanned = false;

        foreach (var root in sourceRoots)
        {
            if (!recursive && !subfoldersNotScanned)
            {
                subfoldersNotScanned = HasSubfolders(root);
            }

            foreach (var path in Enumerate(root, recursive, errors, skippedFolders, ct))
            {
                ct.ThrowIfCancellationRequested();

                if (IsInternal(path) || !seen.Add(path))
                {
                    continue;
                }

                if (kinds.Classify(path) == MediaKind.Other)
                {
                    ignored.Add(path);
                    continue;
                }

                try
                {
                    infos.Add(reader.Read(fileSystem.GetFile(path)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(new ScanError(path, $"파일을 읽지 못했습니다: {ex.Message}"));
                }

                filesRead?.Report(++filesDone);
            }
        }

        ct.ThrowIfCancellationRequested();

        ignored.Sort(StringComparer.OrdinalIgnoreCase);
        return new ScanResult(BuildGroups(infos), ignored, errors, subfoldersNotScanned);
    }

    /// <summary>
    /// 소스 폴더 바로 아래에 폴더가 하나라도 있으면 true(앱의 .photook 폴더는 하위 폴더까지 스캔해도 보지 않으므로 세지 않는다).
    /// 확인할 수 없으면(폴더를 열 수 없음, 드라이브 분리 등) 있다고 본다: 틀려도 주의 문구가 하나 더 보일 뿐이다.
    /// </summary>
    private bool HasSubfolders(string root)
    {
        try
        {
            return fileSystem.EnumerateDirectories(root)
                .Any(d => !string.Equals(Path.GetFileName(d.TrimEnd('\\', '/')), PhotookFolder, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// 사이드카 이름을 키와 짝 확장자로 나눈다. IMG.RW2.xmp는 (IMG, .RW2), IMG.xmp는 (IMG, null).
    /// </summary>
    internal static (string Key, string? PairExtension) SplitSidecarName(string fileName, FileKinds kinds)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var inner = Path.GetExtension(stem);
        if (inner.Length > 0 && kinds.IsMediaExtension(inner))
        {
            return (Path.GetFileNameWithoutExtension(stem), inner);
        }

        return (stem, null);
    }

    /// <summary>
    /// 소스 폴더 하나의 파일 목록을 모은다. 열 수 없는 폴더(드라이브 루트의 System Volume Information, 권한이 없는 폴더,
    /// 소스 폴더 자체 포함)는 건너뛰고 폴더마다 오류로 기록한다(여러 소스에서 같은 폴더를 만나도 한 번만). 그래서 열 수 없는
    /// 소스 폴더는 "파일 0개"가 아니라 오류로 보인다. 그 밖의 예외로 열거가 끊기면 그때까지 모은 파일은 살리고 소스 폴더 오류로 기록한다.
    /// </summary>
    private List<string> Enumerate(string root, bool recursive, List<ScanError> errors, HashSet<string> skippedFolders,
        CancellationToken ct)
    {
        var paths = new List<string>();
        try
        {
            using var enumerator = fileSystem.EnumerateFiles(root, recursive, (folder, error) =>
            {
                if (skippedFolders.Add(folder))
                {
                    var message = error is LinkedFolderSkippedException ? LinkedFolderSkippedException.MessagePrefix : InaccessibleFolderMessage;
                    errors.Add(new ScanError(folder, message + folder));
                }
            }).GetEnumerator();
            while (enumerator.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                paths.Add(enumerator.Current);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add(new ScanError(root, $"폴더를 끝까지 읽지 못했습니다: {ex.Message}"));
        }

        return paths;
    }

    /// <summary>.photook 폴더 아래이거나 임시 파일(이름에 .photook-tmp 포함)이면 true.</summary>
    private static bool IsInternal(string path)
    {
        var segments = path.Split('\\', '/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], PhotookFolder, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return segments[^1].Contains(TempMarker, StringComparison.OrdinalIgnoreCase);
    }

    private List<FileGroup> BuildGroups(List<PhotoInfo> infos)
    {
        var groups = new List<FileGroup>();

        // 폴더별로 처리한다. 안쪽 목록은 경로 순서라서 "첫 번째"가 항상 같은 파일이다.
        var byDirectory = infos
            .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .GroupBy(i => Path.GetDirectoryName(i.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        foreach (var directory in byDirectory)
        {
            var byKey = new Dictionary<string, List<PhotoInfo>>(StringComparer.OrdinalIgnoreCase);
            var sidecars = new List<(PhotoInfo Info, string Key, string? Pair)>();

            foreach (var info in directory)
            {
                var name = Path.GetFileName(info.Path);
                if (info.Kind == MediaKind.Sidecar)
                {
                    var (key, pair) = SplitSidecarName(name, kinds);
                    sidecars.Add((info, key, pair));
                    continue;
                }

                var mediaKey = Path.GetFileNameWithoutExtension(name);
                if (!byKey.TryGetValue(mediaKey, out var list))
                {
                    byKey[mediaKey] = list = [];
                }

                list.Add(info);
            }

            // 규칙 2: 같은 키의 Image(비RAW)가 있으면 Video는 그 Image의 딸린 파일(라이브 포토).
            var companions = new Dictionary<string, List<PhotoInfo>>(StringComparer.OrdinalIgnoreCase);
            var primaries = new List<PhotoInfo>();
            var ownerOfMedia = new Dictionary<string, PhotoInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var members in byKey.Values)
            {
                var image = members.FirstOrDefault(m => m.Kind == MediaKind.Image);
                foreach (var member in members)
                {
                    if (member.Kind == MediaKind.Video && image is not null)
                    {
                        AddCompanion(companions, image.Path, member);
                        ownerOfMedia[member.Path] = image;
                    }
                    else
                    {
                        primaries.Add(member);
                        ownerOfMedia[member.Path] = member;
                    }
                }
            }

            // 규칙 3: 사이드카를 붙인다. 붙일 데가 없으면 자기 자신이 대표.
            var orphans = new List<PhotoInfo>();
            foreach (var (info, key, pair) in sidecars)
            {
                var owner = FindSidecarOwner(byKey, ownerOfMedia, key, pair);
                if (owner is null)
                {
                    orphans.Add(info);
                }
                else
                {
                    AddCompanion(companions, owner.Path, info);
                }
            }

            // 규칙 5: 촬영일 빌리기. 빌린 값을 다시 빌려 주지 않도록 원래 값에서만 고른다.
            // 사진·동영상 정보의 촬영일이 파일 이름에서 읽은 날짜보다 우선한다: 같은 이름 형제에 정보가 있으면 그것을 쓰고,
            // 파일 이름 날짜뿐인 대표도 형제의 사진·동영상 정보 날짜로 바꾼다.
            var timeByKey = new Dictionary<string, (DateTime Time, CaptureTimeSource Source)>(StringComparer.OrdinalIgnoreCase);
            foreach (var primary in primaries)
            {
                if (primary.CaptureTime is not { } time)
                {
                    continue;
                }

                var key = Path.GetFileNameWithoutExtension(primary.Path);
                if (!timeByKey.TryGetValue(key, out var existing)
                    || (!IsMetadataSource(existing.Source) && IsMetadataSource(primary.CaptureSource)))
                {
                    timeByKey[key] = (time, primary.CaptureSource);
                }
            }

            foreach (var primary in primaries)
            {
                var finalPrimary = primary;
                if ((primary.CaptureTime is null || primary.CaptureSource == CaptureTimeSource.FileName)
                    && timeByKey.TryGetValue(Path.GetFileNameWithoutExtension(primary.Path), out var borrowed)
                    && (primary.CaptureTime is null || IsMetadataSource(borrowed.Source)))
                {
                    finalPrimary = primary with { CaptureTime = borrowed.Time, CaptureSource = borrowed.Source };
                }

                groups.Add(new FileGroup(finalPrimary, Ordered(companions.GetValueOrDefault(primary.Path))));
            }

            foreach (var orphan in orphans)
            {
                groups.Add(new FileGroup(orphan, []));
            }
        }

        groups.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Primary.Path, b.Primary.Path));
        return groups;
    }

    private static bool IsMetadataSource(CaptureTimeSource source) =>
        source is CaptureTimeSource.Photo or CaptureTimeSource.Video;

    private static void AddCompanion(Dictionary<string, List<PhotoInfo>> companions, string ownerPath, PhotoInfo companion)
    {
        if (!companions.TryGetValue(ownerPath, out var list))
        {
            companions[ownerPath] = list = [];
        }

        list.Add(companion);
    }

    /// <summary>
    /// 짝 확장자가 있고 그 파일이 있으면 그 파일이 속한 그룹의 대표, 없으면 Raw → Image → Video 순서의 첫 대표.
    /// 짝 확장자를 지정했지만 그 파일이 없으면(RW2.xmp인데 RW2가 없는 경우) 같은 키의 다른 사진에 붙인다.
    /// </summary>
    private static PhotoInfo? FindSidecarOwner(
        Dictionary<string, List<PhotoInfo>> byKey,
        Dictionary<string, PhotoInfo> ownerOfMedia,
        string key,
        string? pairExtension)
    {
        if (!byKey.TryGetValue(key, out var members))
        {
            return null;
        }

        if (pairExtension is not null)
        {
            var target = members.FirstOrDefault(m =>
                string.Equals(Path.GetExtension(m.Path), pairExtension, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                return ownerOfMedia[target.Path];
            }
        }

        foreach (var kind in new[] { MediaKind.Raw, MediaKind.Image, MediaKind.Video })
        {
            // 라이브 포토의 영상처럼 다른 그룹에 딸린 파일은 대표가 아니므로 제외한다.
            var match = members.FirstOrDefault(m => m.Kind == kind && ownerOfMedia[m.Path] == m);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static List<PhotoInfo> Ordered(List<PhotoInfo>? list) =>
        list is null ? [] : list.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
}
