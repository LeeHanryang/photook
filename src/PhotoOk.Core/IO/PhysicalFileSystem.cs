using System.Text;

namespace PhotoOk.Core.IO;

/// <summary>실제 디스크를 쓰는 <see cref="IFileSystem"/>.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive)
    {
        // 존재 확인은 즉시 하고(지연 열거 때문에 오류가 늦게 나지 않도록), 열거 자체는 지연시킨다.
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {directory}");
        }

        return Directory.EnumerateFiles(
            directory,
            "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
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
