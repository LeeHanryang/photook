using System.IO;
using PhotoOk.Core.IO;

namespace PhotoOk.Tests.Fakes;

/// <summary>작업 사이에 끼어들기 위한 얇은 감싸개. 이동 앞뒤에 동작을 걸고, 새로 만든 파일 경로를 남긴다.</summary>
public sealed class HookedFileSystem(IFileSystem inner) : IFileSystem
{
    public Action<string, string>? BeforeMove { get; set; }

    public Action<string, string>? AfterMove { get; set; }

    /// <summary><see cref="CreateNew"/>로 만들려고 한 경로, 시도한 순서대로.</summary>
    public List<string> Created { get; } = [];

    /// <summary>덧붙이기 호출마다 (경로, 줄 수), 호출한 순서대로. <see cref="AppendLine"/>은 1줄로 남긴다.</summary>
    public List<(string Path, int Lines)> Appends { get; } = [];

    /// <summary><see cref="SetLastWriteTimeUtc"/>를 부른 경로, 부른 순서대로.</summary>
    public List<string> TimesSet { get; } = [];

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive, Action<string, Exception>? onInaccessible = null) =>
        inner.EnumerateFiles(directory, recursive, onInaccessible);

    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public FileEntry GetFile(string path) => inner.GetFile(path);

    public Stream OpenRead(string path) => inner.OpenRead(path);

    public Stream CreateNew(string path)
    {
        Created.Add(path);
        return inner.CreateNew(path);
    }

    public void Move(string source, string destination)
    {
        BeforeMove?.Invoke(source, destination);
        inner.Move(source, destination);
        AfterMove?.Invoke(source, destination);
    }

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public bool IsDirectoryEmpty(string path) => inner.IsDirectoryEmpty(path);

    public void RemoveEmptyDirectory(string path) => inner.RemoveEmptyDirectory(path);

    public void AppendLine(string path, string line)
    {
        Appends.Add((path, 1));
        inner.AppendLine(path, line);
    }

    public void AppendLines(string path, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        Appends.Add((path, list.Count));
        inner.AppendLines(path, list);
    }

    public void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        TimesSet.Add(path);
        inner.SetLastWriteTimeUtc(path, utc);
    }

    public string GetVolumeRoot(string path) => inner.GetVolumeRoot(path);

    public long GetAvailableFreeSpace(string path) => inner.GetAvailableFreeSpace(path);
}
