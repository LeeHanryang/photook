using PhotoOk.Core.IO;
using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Planning;

/// <summary>
/// 아카이브 안 파일의 크기 → 경로 목록. 흡수할 때 아카이브 어디에든 같은 내용이 있는지 찾는 데 쓴다(크기가 같은 파일만 해시로 비교한다).
/// 메모리에만 두며 디스크에 저장하지 않는다. 앱의 <c>.photook</c> 폴더와 임시 파일은 스캔과 같은 규칙으로 뺀다.
/// </summary>
public sealed class ArchiveIndex
{
    private static readonly IReadOnlyList<string> NoPaths = [];

    private readonly Dictionary<long, List<string>> _byLength;

    private ArchiveIndex(Dictionary<long, List<string>> byLength, int count)
    {
        _byLength = byLength;
        Count = count;
    }

    public int Count { get; }

    /// <summary>
    /// 아카이브 폴더 아래 모든 파일을 크기별로 모은다. 폴더가 없으면 빈 색인이다(알리지 않는다).
    /// 열 수 없는 폴더는 건너뛰고 <paramref name="onInaccessible"/>(경로, 예외)로 알린다. 열거가 중간에 끊기면(폴더가 사라진 경우 포함)
    /// 그때까지 모은 것을 쓰고 아카이브 폴더로 알린다. 크기를 알 수 없는 파일은 뺀 채 그 파일 경로로 알린다(그 파일의 중복을 찾지 못할 뿐이다).
    /// </summary>
    public static ArchiveIndex Build(IFileSystem fs, string archiveRoot, Action<string, Exception>? onInaccessible, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(archiveRoot);

        var byLength = new Dictionary<long, List<string>>();
        var count = 0;
        if (!fs.DirectoryExists(archiveRoot))
        {
            return new ArchiveIndex(byLength, count);
        }

        try
        {
            foreach (var path in fs.EnumerateFiles(archiveRoot, true, (folder, error) => onInaccessible?.Invoke(folder, error)))
            {
                ct.ThrowIfCancellationRequested();
                if (Scanner.IsInternal(path))
                {
                    continue;
                }

                long length;
                try
                {
                    length = fs.GetFile(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    onInaccessible?.Invoke(path, ex);
                    continue;
                }

                if (!byLength.TryGetValue(length, out var list))
                {
                    byLength[length] = list = [];
                }

                list.Add(path);
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onInaccessible?.Invoke(archiveRoot, ex);
        }

        ct.ThrowIfCancellationRequested();
        return new ArchiveIndex(byLength, count);
    }

    /// <summary>크기가 같은 파일의 경로(없으면 빈 목록).</summary>
    public IReadOnlyList<string> WithLength(long length) =>
        _byLength.TryGetValue(length, out var list) ? list : NoPaths;
}
