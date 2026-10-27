using System.Collections.ObjectModel;
using System.IO;
using PhotoOk.Core.Planning;

namespace PhotoOk.ViewModels;

/// <summary>미리보기 가운데의 정리 후 폴더 트리 한 칸. 이번 실행으로 놓이는 파일 수와 새 폴더 여부를 보인다.</summary>
public sealed class FolderNodeViewModel
{
    private const string TempMarker = ".photook-tmp-";

    public FolderNodeViewModel(string name, string fullPath, bool isNew)
    {
        Name = name;
        FullPath = fullPath;
        IsNew = isNew;
    }

    public string Name { get; }

    public string FullPath { get; }

    /// <summary>이번 실행이 만드는 폴더면 true.</summary>
    public bool IsNew { get; }

    /// <summary>이 폴더와 그 아래에 놓이는 파일 수.</summary>
    public int FileCount { get; private set; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    /// <summary>
    /// 계획의 복사·이동 작업 대상으로 아카이브 기준 트리를 만든다. 자리 바꾸기용 임시 이동과 건너뛴 파일은 넣지 않는다.
    /// 아카이브 루트 바로 아래에 놓이는 파일은 폴더가 없으므로 세지 않는다.
    /// </summary>
    public static IReadOnlyList<FolderNodeViewModel> Build(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var created = plan.Operations
            .Where(op => op.Kind == OperationKind.CreateDirectory)
            .Select(op => Path.TrimEndingDirectorySeparator(op.Destination))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var roots = new List<FolderNodeViewModel>();
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

            var siblings = roots;
            var current = plan.ArchiveRoot;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var node = siblings.FirstOrDefault(n => string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));
                if (node is null)
                {
                    node = new FolderNodeViewModel(segment, current, created.Contains(current));
                    siblings.Add(node);
                }

                node.FileCount++;
                siblings = node.ChildList;
            }
        }

        Sort(roots);
        foreach (var root in roots)
        {
            root.Publish();
        }

        return roots;
    }

    /// <summary>파일을 보관 폴더 안 최종 자리에 놓는 작업인가(임시 이름으로 비켜 두는 이동은 아니다).</summary>
    internal static bool IsPlacement(PlannedOperation op) =>
        op.Kind is OperationKind.Copy or OperationKind.Move
        && !op.Destination.Contains(TempMarker, StringComparison.OrdinalIgnoreCase);

    // 만드는 동안에는 List로 모으고 끝나면 한 번에 ObservableCollection으로 옮긴다(항목마다 알림이 가지 않게).
    private List<FolderNodeViewModel> ChildList { get; } = [];

    private void Publish()
    {
        Sort(ChildList);
        foreach (var child in ChildList)
        {
            Children.Add(child);
            child.Publish();
        }
    }

    private static void Sort(List<FolderNodeViewModel> nodes) =>
        nodes.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
}
