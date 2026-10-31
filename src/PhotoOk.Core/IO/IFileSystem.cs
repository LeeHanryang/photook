namespace PhotoOk.Core.IO;

/// <summary>
/// 앱이 디스크에 접근하는 유일한 통로. 사용자 파일을 덮어쓰거나 지우는 연산은 의도적으로 없다.
/// </summary>
public interface IFileSystem
{
    /// <summary>
    /// 디렉터리 아래 파일의 전체 경로를 나열한다. 디렉터리가 없으면 <see cref="DirectoryNotFoundException"/>.
    /// 열 수 없는 폴더(시작 폴더 포함)는 건너뛰고 나머지를 끝까지 나열하되, 건너뛴 폴더마다
    /// <paramref name="onInaccessible"/>(폴더 경로, 예외)를 부른다. <paramref name="onInaccessible"/>이 null이면
    /// 조용히 빠뜨리지 않도록 그 예외를 그대로 던진다.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string directory, bool recursive, Action<string, Exception>? onInaccessible = null);

    /// <summary>
    /// 디렉터리 바로 아래 폴더의 전체 경로를 나열한다(한 단계만, 숨김·시스템 폴더 포함). 디렉터리가 없으면
    /// <see cref="DirectoryNotFoundException"/>, 목록을 볼 수 없으면 그 예외를 그대로 던진다.
    /// </summary>
    IEnumerable<string> EnumerateDirectories(string directory);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>없으면 <see cref="FileNotFoundException"/>.</summary>
    FileEntry GetFile(string path);

    /// <summary>읽기 전용(FileShare.Read)으로 연다.</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// 읽기 전용으로 열되, 읽는 동안 다른 핸들이 쓰기(덧붙이기)로 여는 것을 막지 않는다(FileShare.ReadWrite).
    /// 앱이 덧붙이는 기록 파일을 읽을 때만 쓴다. 사용자 파일은 <see cref="OpenRead"/>로 연다(읽는 동안 바뀌지 않게).
    /// </summary>
    Stream OpenReadShared(string path);

    /// <summary>FileMode.CreateNew + FileOptions.WriteThrough. 이미 있으면 <see cref="IOException"/>.</summary>
    Stream CreateNew(string path);

    /// <summary>덮어쓰지 않는 이동. 대상이 이미 있으면 <see cref="IOException"/>이고 양쪽 파일은 그대로다.</summary>
    void Move(string source, string destination);

    /// <summary>필요한 상위 폴더까지 만든다. 이미 있으면 아무 일도 하지 않는다.</summary>
    void CreateDirectory(string path);

    bool IsDirectoryEmpty(string path);

    /// <summary>비어 있는 폴더만 지운다. 비어 있지 않으면 <see cref="IOException"/>.</summary>
    void RemoveEmptyDirectory(string path);

    /// <summary>폴더가 연결 폴더(정션·심볼릭 링크)인지. 없는 경로나 속성을 읽을 수 없는 경로는 false.</summary>
    bool IsLinkedDirectory(string path);

    /// <summary>파일이 없으면 만들고, 한 줄을 덧붙인 뒤 디스크까지 flush한다.</summary>
    void AppendLine(string path, string line);

    /// <summary>
    /// 파일이 없으면 만들고, 여러 줄을 한 번 열어 덧붙인 뒤 디스크까지 한 번 flush한다.
    /// 도중에 끊기면 앞의 몇 줄만 들어가고 마지막 줄이 잘릴 수 있다(기록을 읽는 쪽이 잘린 줄과 모자란 계획을 가려낸다).
    /// </summary>
    void AppendLines(string path, IEnumerable<string> lines);

    /// <summary>
    /// 파일의 마지막 쓰기 시각만 바꾼다(내용은 그대로). 파일이 없으면 <see cref="FileNotFoundException"/>.
    /// 앱이 만든 파일(검증을 마친 복사 임시 파일)에 원본의 시각을 옮길 때만 쓴다. 원본에는 쓰지 않는다.
    /// </summary>
    void SetLastWriteTimeUtc(string path, DateTime utc);

    /// <summary>경로가 속한 볼륨의 루트. 예: <c>D:\</c>.</summary>
    string GetVolumeRoot(string path);

    long GetAvailableFreeSpace(string path);
}
