using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PhotoOk.Core.Layout;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Naming;

/// <summary>
/// 파일이 놓인 폴더 경로에서 이벤트 이름을 읽는다. 디스크를 건드리지 않는다.
/// 재정리는 먼저 프리셋의 폴더 형식(날짜와 이벤트가 함께 있는 단계)으로 읽고(제외 단어는 따르지 않는다), 맞는 폴더가 없고 폴더 이름 사용이 켜져 있으면
/// 판별기로 고른다. 흡수는 폴더 이름 사용이 켜져 있을 때만 판별기로 고른다. 가져오기와 이벤트가 없는 프리셋은 읽지 않는다.
/// 폴더는 파일의 부모부터 그 파일이 속한 루트까지만 본다(루트 포함, 드라이브 루트는 이름이 없다). 재정리의 형식 읽기만 고른 폴더가 이벤트 폴더보다 아래여도 이름을 잃지 않도록
/// 아카이브 폴더 바로 아래까지 올라간다. 판별기는 루트 아래만 보고, 루트 자체의 이름은 파일이 그 바로 안에 있을 때만 쓴다.
/// 형식에 맞은 폴더 바로 아래의 이벤트 후보 폴더는 <c>\</c>로 나눈 이름의 나머지 조각으로 읽는다(<c>[2025-01-01] 가족여행\제주</c> → <c>가족여행\제주</c>).
/// 이름을 고른 폴더 아래의 이벤트 후보와 뜻 있는 폴더는 <see cref="KeptFolders"/>로 따로 돌려준다.
/// </summary>
public sealed class EventNameReader
{
    private static readonly HashSet<string> DateTokens = new(StringComparer.Ordinal) { "연도", "월", "일", "날짜", "기간" };

    private static readonly char[] Separators = ['\\', '/'];

    private static readonly HashSet<string> ReasonLabels = Enum.GetValues<ReviewReason>()
        .Select(reason => NormalizedSegment(ReviewReasonText.Label(reason)))
        .ToHashSet(StringComparer.Ordinal);

    private readonly bool _enabled;
    private readonly JobKind _kind;
    private readonly bool _useFolderNames;
    private readonly bool _splitUnderscores;
    private readonly FolderNameContext _context;
    private readonly IReadOnlyList<FormatLevel> _formats;
    private readonly IReadOnlyList<RootInfo> _roots;
    private readonly HashSet<string> _presetOwned;
    private readonly bool _usesExtension;
    private readonly Preset _preset;
    private readonly LayoutRenderer _renderer;
    private readonly DeviceAliasResolver _aliases;
    private readonly HashSet<LayoutBranch> _branchesWithEvent;

    // 폴더 이름은 Zfc, JPG, 2024처럼 되풀이되므로 이름별로 한 번만 판별한다. 제외 목록과 기기 이름이 고정된 이 판독기 안에서만 기억한다.
    private readonly ConcurrentDictionary<string, (FolderKind Kind, FolderNameCandidate? Candidate)> _judged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Name, bool ReadsPieces)?> _formatNames = new(StringComparer.Ordinal);

    /// <param name="archiveRoot">아카이브 폴더. 재정리의 형식 읽기는 고른 폴더 위로 이 폴더 바로 아래까지 본다. 없으면 위를 보지 않는다.</param>
    public EventNameReader(Preset preset, JobKind kind, bool useFolderNames, IReadOnlyList<string> roots, FolderNameContext context,
        bool splitUnderscores = false, string? archiveRoot = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(context);

        _preset = preset;
        _renderer = new LayoutRenderer(preset);
        _aliases = new DeviceAliasResolver(preset.DeviceAliases);
        _branchesWithEvent = preset.Branches
            .Where(branch => branch.Levels.Any(level => TryParse(level.Template)?.Tokens.Contains("이벤트") == true))
            .ToHashSet();
        _kind = kind;
        _useFolderNames = useFolderNames;
        _splitUnderscores = splitUnderscores;
        _context = context;
        _enabled = kind != JobKind.Import && PresetUsesEvents(preset);
        _formats = _enabled && kind == JobKind.Reorganize ? BuildFormats(preset) : [];
        _presetOwned = PresetOwnedNames(preset);
        _usesExtension = AllTemplates(preset).Any(t => t.Tokens.Contains("확장자"));
        var archive = kind == JobKind.Reorganize && archiveRoot is not null ? NormalizeRoot(archiveRoot) : null;
        _roots = roots
            .Select(NormalizeRoot)
            .Where(r => r.Length > 0)
            .OrderByDescending(r => r.Length)
            .Select(r => new RootInfo(r, RootFolderName(r), FoldersAbove(r, archive)))
            .ToList();
    }

    /// <summary>프리셋의 어느 분기든 이벤트 토큰을 쓰는가.</summary>
    public static bool PresetUsesEvents(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return AllTemplates(preset).Any(t => t.Tokens.Contains("이벤트"));
    }

    public string? Read(FileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Resolve(group).Name;
    }

    /// <summary>
    /// <see cref="Read"/>가 돌려준 이름이 프리셋 형식에서 읽은 것인가. 형식에서 읽은 이름은 사람이나 앱이 그 자리에 정해 둔 이름이라 제외 목록을 따르지 않고,
    /// 카드에 "이 이름 다시 쓰지 않기"도 없다.
    /// </summary>
    public bool IsFormatName(FileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Resolve(group) is { Name: not null, FormatIndex: >= 0 };
    }

    /// <summary>
    /// 이름을 고른 폴더(이름이 없으면 루트) 아래에서 이벤트 후보와 뜻 있는 폴더만 파일에서 먼 쪽부터 모은 폴더 이름들.
    /// 이름이 고른 폴더 위에서 왔으면 그 사이의 폴더(고른 폴더 포함)도 담아, 어느 폴더를 골라도 결과가 같다. 이름이 없으면 루트 폴더 자체는 담지 않는다.
    /// 고른 분기에 이벤트 토큰이 없으면(판단 필요) 이름이 경로에 쓰이지 않으므로, 형식에서 읽은 이름의 나머지 조각도 담고 판별기가 고른 폴더는 자르는 기준이 되지 않는다.
    /// 가져오기와 파일이 루트 밖이면 빈 목록이다. 프리셋이 스스로 만드는 이름은 담지 않는다.
    /// </summary>
    public IReadOnlyList<string> KeptFolders(FileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (_kind == JobKind.Import)
        {
            return [];
        }

        var reading = Resolve(group);
        var folders = reading.Folders;
        var limit = reading.NameIndex < 0 ? reading.BelowRoot : reading.NameIndex;
        if (reading.NameIndex >= 0 && !SelectedBranchUsesEvent(group))
        {
            limit = reading.FormatIndex >= 0 ? reading.FormatIndex : reading.BelowRoot;
        }

        var kept = new List<string>();
        for (var i = limit - 1; i >= 0; i--)
        {
            if (!IsPresetOwned(folders[i], group) && Judge(folders[i]).Kind != FolderKind.Disposable)
            {
                kept.Add(folders[i]);
            }
        }

        return kept;
    }

    // 계획이 고르는 분기와 같은 함수로 고른다.
    private bool SelectedBranchUsesEvent(FileGroup group) =>
        _branchesWithEvent.Contains(_renderer.SelectBranch(ReviewDetector.Detect(group, _preset, _aliases)));

    private Reading Resolve(FileGroup group)
    {
        var (folders, belowRoot, withinRoot) = FoldersNearestFirst(group.Primary.Path);
        if (folders.Count == 0)
        {
            return new Reading(null, folders, belowRoot, -1, -1);
        }

        var index = -1;
        var formatIndex = -1;
        string? name = null;
        if (_enabled)
        {
            if (_kind == JobKind.Reorganize)
            {
                (name, formatIndex, index) = ReadByFormat(folders, group);
            }

            if (name is null && _useFolderNames)
            {
                // 프리셋이 만든 이름을 이벤트 이름으로 읽으면 다시 정리할 때 파일이 옮겨진다. 위치가 어긋나지 않도록 빈 이름으로 바꿔 넘긴다.
                // 판별기는 고른 폴더 아래만 본다. 고른 폴더의 이름은 파일이 그 바로 안에 있을 때만 쓴다(걸러진 폴더를 타고 올라가 받지 않는다).
                var visible = folders.Take(belowRoot > 0 ? belowRoot : withinRoot).Select(folder => IsPresetOwned(folder, group) ? string.Empty : folder).ToList();
                index = FolderNameClassifier.PickIndex(visible, folder => Judge(folder).Candidate);
                name = index < 0 ? null : Judge(folders[index]).Candidate?.Name;
            }
        }

        // 이름이 다듬다가 비어도 고른 폴더의 위치는 지킨다(그 폴더 자체를 하위 폴더로 유지하지 않도록).
        var found = name is not null;
        name = Finish(name);
        return new Reading(name, folders, belowRoot, found ? index : -1, formatIndex);
    }

    // 앞뒤 공백을 다듬고, 켜져 있으면 _를 역슬래시로 바꾼다(양 끝의 _는 버리고 이어진 _는 하나로 합친다). 비면 null.
    private string? Finish(string? name)
    {
        name = name?.Trim();
        if (_splitUnderscores && name is not null)
        {
            name = string.Join('\\', name.Split(['_', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// 파일에 가까운 폴더부터 프리셋 형식을 맞춘다. 맞은 폴더 바로 아래로 이어지는 이벤트 후보 폴더는 <c>\</c>로 나눈 이름의 나머지 조각이므로 이름에 붙이고,
    /// 후보가 아닌 첫 폴더(기기, 포맷, 뜻 있는 폴더, 프리셋이 만드는 이름)에서 멈춘다. 쓸 때와 읽을 때가 같아야 다시 정리해도 파일이 옮겨지지 않는다.
    /// 돌려주는 위치는 형식에 맞은 폴더와 마지막 조각의 폴더다(유지할 폴더는 그 아래부터).
    /// </summary>
    private (string? Name, int FormatIndex, int LastPiece) ReadByFormat(IReadOnlyList<string> folders, FileGroup group)
    {
        for (var i = 0; i < folders.Count; i++)
        {
            if (_formatNames.GetOrAdd(folders[i], MatchFormat) is not var (name, readsPieces))
            {
                continue;
            }

            var last = i;
            while (readsPieces && last > 0
                   && !IsPresetOwned(folders[last - 1], group)
                   && Judge(folders[last - 1]).Kind == FolderKind.EventCandidate)
            {
                last--;
                name += '\\' + folders[last];
            }

            return (name, i, last);
        }

        return (null, -1, -1);
    }

    // 폴더 이름 하나를 프리셋 형식들에 맞춘다. 맞지 않거나 이벤트 자리가 비었으면 null.
    private (string Name, bool ReadsPieces)? MatchFormat(string folder)
    {
        foreach (var format in _formats)
        {
            var match = format.Pattern.Match(folder);
            if (match.Success && match.Groups[1].Value.Trim() is { Length: > 0 } name)
            {
                return (name, format.ReadsPieces);
            }
        }

        return null;
    }

    private (FolderKind Kind, FolderNameCandidate? Candidate) Judge(string folder) =>
        _judged.GetOrAdd(folder, static (name, context) => FolderNameClassifier.Judge(name, context), _context);

    // 파일에 가까운 폴더부터: 루트 아래 폴더(BelowRoot개), 루트 폴더(여기까지 WithinRoot개), 루트 위 폴더(재정리의 형식 읽기만 본다).
    private (List<string> Folders, int BelowRoot, int WithinRoot) FoldersNearestFirst(string filePath)
    {
        var path = filePath.Replace('/', '\\');
        foreach (var root in _roots)
        {
            if (path.Length <= root.Path.Length || !path.StartsWith(root.Path + "\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = path[(root.Path.Length + 1)..];
            var segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var folders = new List<string>();
            for (var i = segments.Length - 2; i >= 0; i--)
            {
                folders.Add(segments[i]);
            }

            var belowRoot = folders.Count;
            if (root.Name.Length > 0)
            {
                folders.Add(root.Name);
            }

            var withinRoot = folders.Count;
            folders.AddRange(root.Above);
            return (folders, belowRoot, withinRoot);
        }

        return ([], 0, 0);
    }

    // 루트 위에서 아카이브 폴더 바로 아래까지의 폴더 이름(가까운 쪽부터). 아카이브 폴더 자체는 프리셋이 만든 단계가 아니므로 뺀다.
    private static List<string> FoldersAbove(string root, string? archive)
    {
        var above = new List<string>();
        if (archive is null || archive.Length == 0 || !root.StartsWith(archive + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return above;
        }

        var segments = root[(archive.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            above.Add(segments[i]);
        }

        return above;
    }

    private static string NormalizeRoot(string root) => root.Replace('/', '\\').TrimEnd(Separators);

    private static string RootFolderName(string normalizedRoot)
    {
        var last = normalizedRoot.LastIndexOf('\\');
        var name = last < 0 ? normalizedRoot : normalizedRoot[(last + 1)..];
        // "F:" 같은 드라이브 글자만 남은 루트에는 이름이 없다.
        return name.EndsWith(':') ? string.Empty : name;
    }

    private bool IsPresetOwned(string folder, FileGroup group) =>
        _presetOwned.Contains(FolderNameNormalizer.Normalize(folder))
        || IsReasonLabels(folder)
        || (_usesExtension && string.Equals(folder, Path.GetExtension(group.Primary.Path).TrimStart('.'), StringComparison.OrdinalIgnoreCase));

    /// <summary>프리셋이 렌더링하면 나오는 고정된 폴더 이름(정규화한 값): 토큰 없는 단계, 빈 값 대신 쓰는 글자, <c>{포맷}</c>을 쓸 때의 포맷 묶음 이름. 판단 사유는 <see cref="IsReasonLabels"/>가 본다.</summary>
    private static HashSet<string> PresetOwnedNames(Preset preset)
    {
        var names = new List<string>();
        foreach (var level in preset.Branches.SelectMany(branch => branch.Levels))
        {
            if (TryParse(level.Template) is { } template && !template.Tokens.Any())
            {
                names.Add(string.Concat(template.Parts.Select(part => part.Literal)));
            }

            if (!string.IsNullOrEmpty(level.Replacement))
            {
                names.Add(level.Replacement);
            }
        }

        if (AllTemplates(preset).Any(t => t.Tokens.Contains("포맷")))
        {
            names.AddRange(preset.FormatGroups.Keys);
        }

        return names.Select(NormalizedSegment).ToHashSet(StringComparer.Ordinal);
    }

    // LayoutRenderer는 판단 사유를 ·로 잇는다. 폴더 이름을 ·로 나눈 조각이 모두 사유이면 프리셋이 만든 이름이다.
    private static bool IsReasonLabels(string folder) =>
        folder.Split('·').All(piece => ReasonLabels.Contains(FolderNameNormalizer.Normalize(piece)));

    private static string NormalizedSegment(string name) => FolderNameNormalizer.Normalize(PathSanitizer.SanitizeSegment(name));

    private static IEnumerable<PathTemplate> AllTemplates(Preset preset) =>
        preset.Branches.SelectMany(branch => branch.Levels)
            .Select(level => TryParse(level.Template))
            .OfType<PathTemplate>();

    private static PathTemplate? TryParse(string template)
    {
        try
        {
            return PathTemplate.Parse(template);
        }
        catch (TemplateException)
        {
            return null;
        }
    }

    /// <summary>날짜와 이벤트가 함께 있는 단계들. 같은 모양은 한 번만 담는다.</summary>
    private static List<FormatLevel> BuildFormats(Preset preset)
    {
        var formats = new Dictionary<string, FormatLevel>(StringComparer.Ordinal);
        foreach (var branch in preset.Branches)
        {
            var templates = branch.Levels.Select(level => TryParse(level.Template)).OfType<PathTemplate>().ToList();

            // {원래폴더}가 같은 분기에 있으면 이름에 붙인 폴더가 또 렌더링되어 다시 정리할 때마다 깊어진다.
            var readsPieces = !templates.Any(t => t.Tokens.Contains("원래폴더"));
            foreach (var template in templates)
            {
                var tokens = template.Tokens.ToList();
                if (!tokens.Contains("이벤트") || !tokens.Any(DateTokens.Contains))
                {
                    continue;
                }

                var pattern = PatternOf(template);
                formats[pattern] = formats.TryGetValue(pattern, out var seen)
                    ? seen with { ReadsPieces = seen.ReadsPieces && readsPieces }
                    : new FormatLevel(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), readsPieces);
            }
        }

        return [.. formats.Values];
    }

    private static string PatternOf(PathTemplate template)
    {
        var pattern = new StringBuilder("^");
        foreach (var part in template.Parts)
        {
            if (part.Literal is not null)
            {
                pattern.Append(Regex.Escape(part.Literal));
            }
            else if (part.Token == "이벤트")
            {
                pattern.Append("(.+)");
            }
            else if (DateTokens.Contains(part.Token!))
            {
                pattern.Append(@"[\d\-~.]+");
            }
            else
            {
                pattern.Append(".+?");
            }
        }

        return pattern.Append('$').ToString();
    }

    /// <param name="Name">읽은 이름. 없으면 null.</param>
    /// <param name="Folders">파일에 가까운 폴더부터: 루트 아래, 루트, 루트 위.</param>
    /// <param name="BelowRoot"><paramref name="Folders"/> 앞쪽 중 루트 아래 폴더의 수.</param>
    /// <param name="NameIndex">이름으로 쓴 가장 아래 폴더의 위치. 없으면 -1.</param>
    /// <param name="FormatIndex">프리셋 형식에 맞은 폴더의 위치. 형식에서 읽지 않았으면 -1.</param>
    private readonly record struct Reading(string? Name, List<string> Folders, int BelowRoot, int NameIndex, int FormatIndex);

    /// <param name="Pattern">폴더 이름에 맞출 식. 첫 묶음이 이벤트 이름이다.</param>
    /// <param name="ReadsPieces">맞은 폴더 아래의 이벤트 후보 폴더를 이름의 나머지 조각으로 읽는가.</param>
    private sealed record FormatLevel(Regex Pattern, bool ReadsPieces);

    /// <param name="Path">다듬은 루트 경로.</param>
    /// <param name="Name">루트 폴더 이름. 드라이브 루트는 빈 글자.</param>
    /// <param name="Above">루트 위에서 아카이브 폴더 바로 아래까지의 폴더 이름(가까운 쪽부터).</param>
    private sealed record RootInfo(string Path, string Name, IReadOnlyList<string> Above);
}
