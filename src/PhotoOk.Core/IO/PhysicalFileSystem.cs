using System.Text;

namespace PhotoOk.Core.IO;

/// <summary>실제 디스크를 쓰는 <see cref="IFileSystem"/>.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 한 폴더씩 연다. 숨김·시스템 파일과 폴더도 사진일 수 있으므로 속성으로 거르지 않는다.
    private static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.None,
    };

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive, Action<string, Exception>? onInaccessible = null)
    {
        // 존재 확인은 즉시 하고(지연 열거 때문에 오류가 늦게 나지 않도록), 열거 자체는 지연시킨다.
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {directory}");
        }

        return Walk(directory, recursive, onInaccessible);
    }

    /// <summary>
    /// 폴더를 하나씩 열어 파일을 나열한다. 드라이브 루트의 System Volume Information처럼 열 수 없는 폴더는
    /// 알린 뒤 건너뛰고 나머지를 끝까지 나열한다. 한 폴더의 목록은 다 읽은 뒤에 돌려주므로 폴더 중간에서 끊기지 않는다.
    /// </summary>
    private static IEnumerable<string> Walk(string root, bool recursive, Action<string, Exception>? onInaccessible)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            List<string> files;
            List<string> subdirectories;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", OneLevel).ToList();
                subdirectories = recursive ? Directory.EnumerateDirectories(directory, "*", OneLevel).ToList() : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (onInaccessible is null)
                {
                    throw;
                }

                onInaccessible(directory, ex);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            for (var i = subdirectories.Count - 1; i >= 0; i--)
            {
                // 연결 폴더(정션·심볼릭 링크)는 따라가지 않는다. 같은 사진을 두 번 세거나 순환에 빠질 수 있다.
                // 사용자가 직접 고른 루트는 위에서 이미 열었으므로 여기서는 하위 폴더만 본다.
                if (IsLinkedFolder(subdirectories[i]))
                {
                    var skipped = new LinkedFolderSkippedException(subdirectories[i]);
                    if (onInaccessible is null)
                    {
                        throw skipped;
                    }

                    onInaccessible(subdirectories[i], skipped);
                    continue;
                }

                pending.Push(subdirectories[i]);
            }
        }
    }

    private static bool IsLinkedFolder(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 속성을 읽을 수 없으면 연결 폴더가 아닌 것으로 보고 평소처럼 연다. 못 열면 그때 열거 오류로 알린다.
            return false;
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {directory}");
        }

        return Directory.EnumerateDirectories(directory, "*", OneLevel);
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FileEntry GetFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
        }

        return new FileEntry(info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream CreateNew(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);

    public void Move(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool IsDirectoryEmpty(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
        }

        return !Directory.EnumerateFileSystemEntries(path).Any();
    }

    public void RemoveEmptyDirectory(string path)
    {
        if (!IsDirectoryEmpty(path))
        {
            throw new IOException($"비어 있지 않은 폴더는 지우지 않습니다: {path}");
        }

        Directory.Delete(path, recursive: false);
    }

    public void AppendLine(string path, string line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        var bytes = Utf8NoBom.GetBytes(line + Environment.NewLine);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    public void AppendLines(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024);
        foreach (var line in lines)
        {
            var bytes = Utf8NoBom.GetBytes(line + Environment.NewLine);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.Flush(flushToDisk: true);
    }

    public void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
        }

        File.SetLastWriteTimeUtc(path, utc);
    }

    public string GetVolumeRoot(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"볼륨 루트를 알 수 없는 경로입니다: {path}", nameof(path));
        }

        return root;
    }

    public long GetAvailableFreeSpace(string path)
    {
        var root = GetVolumeRoot(path);
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
