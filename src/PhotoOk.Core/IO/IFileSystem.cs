namespace PhotoOk.Core.IO;

/// <summary>
/// 앱이 디스크에 접근하는 유일한 통로. 사용자 파일을 덮어쓰거나 지우는 연산은 의도적으로 없다.
/// </summary>
public interface IFileSystem
{
    /// <summary>디렉터리 아래 파일의 전체 경로를 나열한다. 디렉터리가 없으면 <see cref="DirectoryNotFoundException"/>.</summary>
    IEnumerable<string> EnumerateFiles(string directory, bool recursive);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>없으면 <see cref="FileNotFoundException"/>.</summary>
    FileEntry GetFile(string path);

    /// <summary>읽기 전용(FileShare.Read)으로 연다.</summary>
    Stream OpenRead(string path);

    /// <summary>FileMode.CreateNew + FileOptions.WriteThrough. 이미 있으면 <see cref="IOException"/>.</summary>
    Stream CreateNew(string path);

    /// <summary>덮어쓰지 않는 이동. 대상이 이미 있으면 <see cref="IOException"/>이고 양쪽 파일은 그대로다.</summary>
    void Move(string source, string destination);

    /// <summary>필요한 상위 폴더까지 만든다. 이미 있으면 아무 일도 하지 않는다.</summary>
    void CreateDirectory(string path);

    bool IsDirectoryEmpty(string path);

    /// <summary>비어 있는 폴더만 지운다. 비어 있지 않으면 <see cref="IOException"/>.</summary>
    void RemoveEmptyDirectory(string path);

    /// <summary>파일이 없으면 만들고, 한 줄을 덧붙인 뒤 디스크까지 flush한다.</summary>
    void AppendLine(string path, string line);

    /// <summary>경로가 속한 볼륨의 루트. 예: <c>D:\</c>.</summary>
    string GetVolumeRoot(string path);

    long GetAvailableFreeSpace(string path);
}
