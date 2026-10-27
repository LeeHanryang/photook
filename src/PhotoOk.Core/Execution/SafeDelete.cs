using PhotoOk.Core.IO;

namespace PhotoOk.Core.Execution;

/// <summary>
/// 파일을 영구 삭제하지 않고 <c>&lt;아카이브&gt;\.photook\되돌림\&lt;실행ID&gt;\&lt;상대 경로&gt;</c>로 옮겨 치운다.
/// 실제로 지우는 것은 비어 있는 폴더뿐이다.
/// </summary>
public sealed class SafeDelete
{
    private const string PhotookFolderName = ".photook";
    private const string QuarantineFolderName = "되돌림";

    private readonly IFileSystem _fileSystem;
    private readonly string _archiveRoot;
    private readonly string _photookRoot;

    public SafeDelete(IFileSystem fileSystem, string archiveRoot, string runId)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        _fileSystem = fileSystem;
        _archiveRoot = TrimSeparators(Path.GetFullPath(archiveRoot));
        _photookRoot = Path.Combine(_archiveRoot, PhotookFolderName);
        QuarantineRoot = Path.Combine(_photookRoot, QuarantineFolderName, runId);
    }

    /// <summary><c>&lt;아카이브&gt;\.photook\되돌림\&lt;실행ID&gt;</c></summary>
    public string QuarantineRoot { get; }

    /// <summary>
    /// 아카이브 안의 파일을 격리 폴더로 옮기고 최종 경로를 돌려준다.
    /// 아카이브 밖이거나 이미 <c>.photook</c> 안에 있는 경로면 <see cref="InvalidOperationException"/>.
    /// 같은 이름이 이미 있으면 <c>_1</c>, <c>_2</c>… 를 마지막 확장자 앞에 붙인다.
    /// </summary>
    public string Quarantine(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);

        if (!IsStrictlyUnder(full, _archiveRoot))
        {
            throw new InvalidOperationException($"아카이브 밖의 경로는 치울 수 없습니다: {path}");
        }

        if (IsStrictlyUnder(full, _photookRoot))
        {
            throw new InvalidOperationException($"이미 .photook 안에 있는 경로는 치울 수 없습니다: {path}");
        }

        var relative = full.Substring(WithSeparator(_archiveRoot).Length);
        var target = Path.Combine(QuarantineRoot, relative);
        var targetDirectory = Path.GetDirectoryName(target)!;
        _fileSystem.CreateDirectory(targetDirectory);

        var name = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
        var candidate = target;
        for (var n = 1; _fileSystem.FileExists(candidate) || _fileSystem.DirectoryExists(candidate); n++)
        {
            candidate = Path.Combine(targetDirectory, $"{name}_{n}{extension}");
        }

        _fileSystem.Move(full, candidate);
        return candidate;
    }

    /// <summary>폴더가 있고 비어 있을 때만 지운다. 없거나 비어 있지 않으면 false.</summary>
    public bool RemoveEmptyDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_fileSystem.DirectoryExists(path) || !_fileSystem.IsDirectoryEmpty(path))
        {
            return false;
        }

        try
        {
            _fileSystem.RemoveEmptyDirectory(path);
            return true;
        }
        catch (IOException) when (_fileSystem.DirectoryExists(path) && !_fileSystem.IsDirectoryEmpty(path))
        {
            // 확인한 뒤 다른 곳에서 파일이 생겼다. 그대로 둔다.
            return false;
        }
    }

    /// <summary>
    /// 되돌리기가 실행이 만든 폴더를 치울 때 쓴다. 동작은 <see cref="RemoveEmptyDirectory"/>와 같다(비어 있을 때만 지운다).
    /// 폴더 제거를 부르는 코드는 이 파일에만 둔다는 구조 테스트 규칙 때문에 따로 둔 입구다.
    /// </summary>
    public bool RemoveCreatedDirectory(string path) => RemoveEmptyDirectory(path);

    // 드라이브 루트(D:\)는 끝의 구분자를 남겨야 Path.Combine이 올바르게 동작한다.
    private static string TrimSeparators(string path) =>
        path == Path.GetPathRoot(path)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string WithSeparator(string root) =>
        root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

    private static bool IsStrictlyUnder(string fullPath, string root)
    {
        var prefix = WithSeparator(root);
        return fullPath.Length > prefix.Length && fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
