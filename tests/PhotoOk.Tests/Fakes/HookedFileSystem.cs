using System.IO;
using PhotoOk.Core.IO;

namespace PhotoOk.Tests.Fakes;

/// <summary>작업 사이에 끼어들기 위한 얇은 감싸개. 이동 앞뒤에 동작을 걸고, 새로 만든 파일 경로를 남긴다.</summary>
public sealed class HookedFileSystem(IFileSystem inner) : IFileSystem
{
    public Action<string, string>? BeforeMove { get; set; }

    public Action<string, string>? AfterMove { get; set; }

    /// <summary>덧붙이기(<see cref="AppendLine"/>, <see cref="AppendLines"/>) 직전에 경로를 받아 부른다. 예외를 던지면 덧붙이지 않는다.</summary>
    public Action<string>? BeforeAppend { get; set; }

    /// <summary><see cref="CreateNew"/>로 만들려고 한 경로, 시도한 순서대로.</summary>
    public List<string> Created { get; } = [];

    /// <summary>덧붙이기 호출마다 (경로, 줄 수), 호출한 순서대로. <see cref="AppendLine"/>은 1줄로 남긴다.</summary>
    public List<(string Path, int Lines)> Appends { get; } = [];

    /// <summary><see cref="SetLastWriteTimeUtc"/>를 부른 경로, 부른 순서대로.</summary>
    public List<string> TimesSet { get; } = [];

    /// <summary><see cref="EnumerateFiles"/>를 부른 횟수.</summary>
    public int EnumerateFilesCalls { get; private set; }

    public IEnumerable<string> EnumerateFiles(string directory, bool recursive, Action<string, Exception>? onInaccessible = null)
    {
        EnumerateFilesCalls++;
        return inner.EnumerateFiles(directory, recursive, onInaccessible).Select(path =>
        {
            OnEnumerated?.Invoke(path);
            return path;
        });
    }

    /// <summary><see cref="EnumerateFiles"/>의 결과를 하나씩 꺼낼 때마다 경로를 받아 부른다. 예외를 던지면 열거가 그 자리에서 끊긴다.</summary>
    public Action<string>? OnEnumerated { get; set; }

    /// <summary><see cref="GetFile"/> 직전에 경로를 받아 부른다. 예외를 던지면 파일 정보를 주지 않는다.</summary>
    public Action<string>? BeforeGetFile { get; set; }

    public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);

    public bool FileExists(string path) => inner.FileExists(path);

    /// <summary><see cref="DirectoryExists"/>로 확인한 경로, 부른 순서대로.</summary>
    public List<string> DirectoryChecks { get; } = [];

    public bool DirectoryExists(string path)
    {
        lock (DirectoryChecks)
        {
            DirectoryChecks.Add(path);
        }

        return inner.DirectoryExists(path);
    }

    public FileEntry GetFile(string path)
    {
        BeforeGetFile?.Invoke(path);
        return inner.GetFile(path);
    }

    /// <summary><see cref="OpenRead"/> 직전에 경로를 받아 부른다. 예외를 던지면 열지 않는다.</summary>
    public Action<string>? BeforeOpenRead { get; set; }

    public Stream OpenRead(string path)
    {
        BeforeOpenRead?.Invoke(path);
        return inner.OpenRead(path);
    }

    /// <summary><see cref="BeforeOpenRead"/>도 부른다.</summary>
    public Stream OpenReadShared(string path)
    {
        BeforeOpenRead?.Invoke(path);
        return inner.OpenReadShared(path);
    }

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

    /// <summary><see cref="RemoveEmptyDirectory"/> 직전에 경로를 받아 부른다. 예외를 던지면 지우지 않는다.</summary>
    public Action<string>? BeforeRemoveDirectory { get; set; }

    public void RemoveEmptyDirectory(string path)
    {
        BeforeRemoveDirectory?.Invoke(path);
        inner.RemoveEmptyDirectory(path);
    }

    public bool IsLinkedDirectory(string path) => inner.IsLinkedDirectory(path);

    public void AppendLine(string path, string line)
    {
        Appends.Add((path, 1));
        BeforeAppend?.Invoke(path);
        inner.AppendLine(path, line);
    }

    public void AppendLines(string path, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        Appends.Add((path, list.Count));
        BeforeAppend?.Invoke(path);
        inner.AppendLines(path, list);
    }

    public void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        TimesSet.Add(path);
        inner.SetLastWriteTimeUtc(path, utc);
    }

    public string GetVolumeRoot(string path) => inner.GetVolumeRoot(path);

    /// <summary>정하면 여유 공간 조회를 이것으로 대신한다(예: 예외를 던져 알 수 없는 상황을 흉내 낸다).</summary>
    public Func<string, long>? FreeSpace { get; set; }

    public long GetAvailableFreeSpace(string path) => FreeSpace is { } freeSpace ? freeSpace(path) : inner.GetAvailableFreeSpace(path);
}
