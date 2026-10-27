using System.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.ViewModels;

/// <summary>
/// 미리보기 가운데의 정리 후 폴더 트리 한 칸. 이번 실행으로 놓이는 파일 수와 새 폴더 여부를 보인다.
/// 만든 뒤에는 바뀌지 않으므로 백그라운드에서 만들어 화면 스레드로 넘겨도 된다.
/// </summary>
public sealed class FolderNodeViewModel
{
    private const string TempMarker = ".photook-tmp-";

    public FolderNodeViewModel(string name, string fullPath, bool isNew, int fileCount,
        IReadOnlyList<FolderNodeViewModel> children)
    {
        Name = name;
        FullPath = fullPath;
        IsNew = isNew;
        FileCount = fileCount;
        Children = children;
    }

    public string Name { get; }

    public string FullPath { get; }

    /// <summary>이번 실행이 만드는 폴더면 true.</summary>
    public bool IsNew { get; }

    /// <summary>이 폴더와 그 아래에 놓이는 파일 수.</summary>
    public int FileCount { get; }

    /// <summary>이름순 하위 폴더.</summary>
    public IReadOnlyList<FolderNodeViewModel> Children { get; }

    /// <summary>
    /// 계획의 복사·이동 작업 대상으로 아카이브 기준 트리를 만든다. 자리 바꾸기용 임시 이동과 건너뛴 파일은 넣지 않는다.
    /// 아카이브 루트 바로 아래에 놓이는 파일은 폴더가 없으므로 세지 않는다. 노드마다 하위 폴더를 사전으로 찾으므로
    /// 작업 수에 비례한 시간이 든다. 화면 스레드 밖에서 불러도 된다.
    /// </summary>
    public static IReadOnlyList<FolderNodeViewModel> Build(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var created = plan.Operations
            .Where(op => op.Kind == OperationKind.CreateDirectory)
            .Select(op => Path.TrimEndingDirectorySeparator(op.Destination))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var root = new NodeBuilder("", plan.ArchiveRoot);
        foreach (var op in plan.Operations)
        {
            if (!IsPlacement(op) || Path.GetDirectoryName(op.Destination) is not { } directory)
            {
                continue;
            }

            var relative = Path.GetRelativePath(plan.ArchiveRoot, directory);
            if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                continue;
            }

            var node = root;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new NodeBuilder(segment, Path.Combine(node.FullPath, segment));
                    node.Children.Add(segment, child);
                }

                child.FileCount++;
                node = child;
            }
        }

        return root.Freeze(created).Children;
    }

    /// <summary>파일을 보관 폴더 안 최종 자리에 놓는 작업인가(임시 이름으로 비켜 두는 이동은 아니다).</summary>
    internal static bool IsPlacement(PlannedOperation op) =>
        op.Kind is OperationKind.Copy or OperationKind.Move
        && !op.Destination.Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    private sealed class NodeBuilder(string name, string fullPath)
    {
        public string Name { get; } = name;

        public string FullPath { get; } = fullPath;

        public int FileCount { get; set; }

        public Dictionary<string, NodeBuilder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FolderNodeViewModel Freeze(HashSet<string> created) =>
            new(Name, FullPath, created.Contains(FullPath), FileCount,
                Children.Values
                    .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(child => child.Freeze(created))
                    .ToList());
    }
}
