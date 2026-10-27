using System.IO;
using System.Text;
using PhotoOk.Core.IO;

namespace PhotoOk.Tests.Fakes;

/// <summary>
/// 메모리 위의 가짜 디스크. 실제 디스크의 규칙을 최대한 그대로 따른다.
/// <list type="bullet">
/// <item>경로는 전체 경로로 정규화하고(드라이브 문자 필수), 대소문자를 구분하지 않으며 끝의 구분자는 무시한다.</item>
/// <item>폴더는 명시적으로 추적한다. 폴더는 처음 만들어진 대소문자를 유지한다.</item>
/// <item>덮어쓰기는 없다. <see cref="CreateNew"/>와 <see cref="Move"/>는 대상이 있으면 <see cref="IOException"/>.</item>
/// <item><see cref="CreateNew"/> 스트림은 쓴 만큼 바로 파일에 보인다(중간에 죽으면 일부만 쓰인 파일이 남는다).</item>
/// <item>열려 있는 핸들을 흉내 낸다: 쓰는 중인 파일은 읽을 수도 옮길 수도 없고, 읽는 중인 파일은 옮길 수도 덧붙일 수도 없다(<see cref="IOException"/>).
/// 모의 크래시가 나면 프로세스가 죽은 것처럼 열린 핸들이 모두 풀린다.</item>
/// <item>검사용 보조(<see cref="ReadAllBytes"/>, <see cref="AllFiles"/>)는 분리된 볼륨의 내용도 본다.</item>
/// </list>
/// 변경 작업(<see cref="MutatingOps"/>)은 시도한 횟수로 센다: CreateNew, CreateNew 스트림의 Write 1회마다, Move,
/// CreateDirectory, RemoveEmptyDirectory, AppendLine, AppendLines(호출 1회), SetLastWriteTimeUtc. 검증에서 실패하는 시도도 센다.
/// <see cref="AppendLines"/>는 이 가짜 디스크에서 원자적이다(모의 크래시는 호출 전체의 앞에서만 난다). 실제 디스크에서 묶음 중간에
/// 끊기는 경우는 기록을 읽는 쪽(RunStarted의 계획 수, 잘린 줄 규칙)이 가려낸다.
/// </summary>
public sealed class FakeFileSystem : IFileSystem
{
    private static readonly DateTime DefaultTimeUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _dirs = new(StringComparer.OrdinalIgnoreCase);      // 정규화 경로 -> 표시 경로
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedVolumes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _freeSpace = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failReadsAfterOpen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deniedDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IHandle> _handles = new();
    private int? _crashAtOps;
    private bool _corruptNextWrite;
    private int _mutatingOps;

    /// <summary>쓰기 작업이 파일에 남기는 마지막 쓰기 시각. 기본은 고정 시각이라 테스트가 결정적이다.</summary>
    public DateTime NowUtc { get; set; } = DefaultTimeUtc;

    public int MutatingOps
    {
        get { lock (_gate) { return _mutatingOps; } }
    }

    // ------------------------------------------------------------------
    // 테스트 준비·검사용 보조
    // ------------------------------------------------------------------

    /// <summary>파일을 바로 만든다(변경 작업으로 세지 않음). 상위 폴더는 암묵적으로 만들고, 이미 있으면 내용을 교체한다.</summary>
    public void AddFile(string path, byte[] content, DateTime? lastWriteUtc = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            var full = Normalize(path);
            if (_dirs.ContainsKey(full))
            {
                throw new IOException($"같은 이름의 폴더가 있습니다: {path}");
            }

            var parent = Path.GetDirectoryName(full) ?? throw new IOException($"루트에는 파일을 만들 수 없습니다: {path}");
            ThrowIfFileInChain(parent);
            EnsureDirectory(parent);

            var time = ToUtc(lastWriteUtc ?? DefaultTimeUtc);
            if (_files.TryGetValue(full, out var existing))
            {
                existing.SetContent(content);
                existing.LastWriteTimeUtc = time;
                return;
            }

            var file = new FakeFile(Path.Combine(_dirs[parent], Path.GetFileName(full)), time);
            file.SetContent(content);
            _files[full] = file;
        }
    }

    /// <summary>폴더를 바로 만든다(상위 폴더 포함, 변경 작업으로 세지 않음).</summary>
    public void AddDirectory(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            ThrowIfFileInChain(full);
            EnsureDirectory(full);
        }
    }

    /// <summary>파일 내용을 복사해서 돌려준다. 분리된 볼륨의 파일도 읽는다.</summary>
    public byte[] ReadAllBytes(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            if (!_files.TryGetValue(full, out var file))
            {
                throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
            }

            return file.Content.ToArray();
        }
    }

    /// <summary>모든 파일의 경로(분리된 볼륨 포함)를 정렬해서 돌려준다.</summary>
    public IReadOnlyList<string> AllFiles()
    {
        lock (_gate)
        {
            return _files.Values
                .Select(f => f.Path)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>볼륨의 여유 공간을 정한다. 정하지 않으면 <c>long.MaxValue / 4</c>.</summary>
    public void SetFreeSpace(string volumeRoot, long bytes)
    {
        lock (_gate)
        {
            _freeSpace[VolumeKeyOfRoot(volumeRoot)] = bytes;
        }
    }

    // ------------------------------------------------------------------
    // 장애 주입
    // ------------------------------------------------------------------

    /// <summary>지금부터 <paramref name="mutatingOps"/>번의 변경 작업은 성공하고, 그 다음 변경 작업부터 <see cref="SimulatedCrashException"/>을 던진다.</summary>
    public void CrashAfter(int mutatingOps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(mutatingOps);
        lock (_gate)
        {
            _crashAtOps = _mutatingOps + mutatingOps;
        }
    }

    /// <summary>크래시 예약, <see cref="FailReadsOf"/>, <see cref="FailReadsAfterOpen"/>, <see cref="CorruptNextWrite"/>를 모두 해제한다. (분리된 볼륨은 <see cref="RestoreVolume"/>으로 되돌린다.)</summary>
    public void ClearFault()
    {
        lock (_gate)
        {
            _crashAtOps = null;
            _failReads.Clear();
            _failReadsAfterOpen.Clear();
            _corruptNextWrite = false;
        }
    }

    /// <summary>다음에 만드는 <see cref="CreateNew"/> 스트림에 처음 쓰는 바이트를 뒤집는다(비트 반전).</summary>
    public void CorruptNextWrite()
    {
        lock (_gate)
        {
            _corruptNextWrite = true;
        }
    }

    /// <summary>이 경로의 <see cref="OpenRead"/>가 <see cref="IOException"/>을 던지게 한다.</summary>
    public void FailReadsOf(string path)
    {
        lock (_gate)
        {
            _failReads.Add(Normalize(path));
        }
    }

    /// <summary>
    /// 이 경로는 <see cref="OpenRead"/>로 열리지만, 그 스트림에서 읽으면 <see cref="IOException"/>을 던진다
    /// (불량 섹터·CRC 오류처럼 열기는 되고 읽는 도중에 실패하는 상황).
    /// </summary>
    public void FailReadsAfterOpen(string path)
    {
        lock (_gate)
        {
            _failReadsAfterOpen.Add(Normalize(path));
        }
    }

    /// <summary>
    /// 이 폴더를 목록을 볼 수 없는 폴더로 만든다(목록 보기 거부 권한, System Volume Information).
    /// <see cref="EnumerateFiles"/>만 영향을 받는다: 이 폴더와 그 아래 파일은 나오지 않고, 건너뛴 폴더로 알린다.
    /// 존재 확인은 실제 디스크처럼 true다. <see cref="ClearFault"/>로 풀리지 않는다.
    /// </summary>
    public void DenyDirectory(string path)
    {
        lock (_gate)
        {
            _deniedDirs.Add(Normalize(path));
        }
    }

    /// <summary>드라이브를 뽑은 것처럼 만든다. 접근하면 <see cref="DirectoryNotFoundException"/>, 존재 확인은 false.</summary>
    public void RemoveVolume(string volumeRoot)
    {
        lock (_gate)
        {
            _removedVolumes.Add(VolumeKeyOfRoot(volumeRoot));
        }
    }

    /// <summary>분리했던 드라이브를 내용 그대로 다시 연결한다.</summary>
    public void RestoreVolume(string volumeRoot)
    {
        lock (_gate)
        {
            _removedVolumes.Remove(VolumeKeyOfRoot(volumeRoot));
        }
    }

    // ------------------------------------------------------------------
    // IFileSystem
    // ------------------------------------------------------------------

    /// <summary>
    /// 스냅샷을 돌려준다(목록을 정하는 동안 <paramref name="onInaccessible"/>을 부른다). 열 수 없는 폴더(<see cref="DenyDirectory"/>)는
    /// 그 아래로 내려가지 않으므로, 재귀 나열에서는 가장 바깥의 거부 폴더만 알린다.
    /// </summary>
    public IEnumerable<string> EnumerateFiles(string directory, bool recursive, Action<string, Exception>? onInaccessible = null)
    {
        lock (_gate)
        {
            var dir = Normalize(directory);
            EnsureVolumePresent(dir);
            if (!_dirs.TryGetValue(dir, out var storedDisplay))
            {
                throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {directory}");
            }

            var denied = new List<string>();
            if (_deniedDirs.Contains(dir))
            {
                denied.Add(dir);
            }
            else if (recursive)
            {
                denied.AddRange(_deniedDirs
                    .Where(d => _dirs.ContainsKey(d) && IsUnder(d, dir))
                    .Where(d => !_deniedDirs.Any(outer => !PathEquals(outer, d) && IsUnder(d, outer) && IsUnder(outer, dir)))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
            }

            foreach (var d in denied)
            {
                var error = new UnauthorizedAccessException($"폴더를 열 수 없습니다: {_dirs[d]}");
                if (onInaccessible is null)
                {
                    throw error;
                }

                onInaccessible(_dirs[d], error);
            }

            if (_deniedDirs.Contains(dir))
            {
                return [];
            }

            var prefixLength = storedDisplay.Length;
            var matches = _files
                .Where(kv => recursive ? IsUnder(kv.Key, dir) : PathEquals(Path.GetDirectoryName(kv.Key), dir))
                .Where(kv => !denied.Any(d => IsUnder(kv.Key, d)))
                .Select(kv => kv.Value.Path)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.Combine(dir, p[prefixLength..].TrimStart('\\')))
                .ToList(); // 스냅샷: 열거하면서 옮겨도 안전하다.
            return matches;
        }
    }

    public bool FileExists(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            return !IsVolumeRemoved(full) && _files.ContainsKey(full);
        }
    }

    public bool DirectoryExists(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            return !IsVolumeRemoved(full) && _dirs.ContainsKey(full);
        }
    }

    public FileEntry GetFile(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            EnsureVolumePresent(full);
            if (!_files.TryGetValue(full, out var file))
            {
                throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
            }

            return new FileEntry(file.Path, file.Content.Length, file.LastWriteTimeUtc);
        }
    }

    public Stream OpenRead(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            EnsureVolumePresent(full);
            if (!_files.TryGetValue(full, out var file))
            {
                if (_dirs.ContainsKey(full))
                {
                    throw new UnauthorizedAccessException($"폴더는 파일로 열 수 없습니다: {path}");
                }

                var parent = Path.GetDirectoryName(full);
                if (parent is null || !_dirs.ContainsKey(parent))
                {
                    throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
                }

                throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
            }

            if (_failReads.Contains(full))
            {
                throw new IOException($"모의 읽기 오류: {path}");
            }

            if (file.Writers > 0)
            {
                throw new IOException($"다른 곳에서 쓰는 중인 파일입니다: {path}");
            }

            var stream = new ReadStream(this, file, failOnRead: _failReadsAfterOpen.Contains(full));
            file.Readers++;
            _handles.Add(stream);
            return stream;
        }
    }

    public Stream CreateNew(string path)
    {
        lock (_gate)
        {
            BeginMutation();
            var full = Normalize(path);
            EnsureVolumePresent(full);
            var parent = Path.GetDirectoryName(full) ?? throw new IOException($"루트에는 파일을 만들 수 없습니다: {path}");
            if (!_dirs.TryGetValue(parent, out var parentDisplay))
            {
                throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
            }

            if (_files.ContainsKey(full))
            {
                throw new IOException($"파일이 이미 있습니다: {path}");
            }

            if (_dirs.ContainsKey(full))
            {
                throw new UnauthorizedAccessException($"같은 이름의 폴더가 있습니다: {path}");
            }

            var file = new FakeFile(Path.Combine(parentDisplay, Path.GetFileName(full)), NowUtc);
            _files[full] = file;

            var stream = new WriteStream(this, file, corruptFirstByte: _corruptNextWrite);
            _corruptNextWrite = false;
            file.Writers++;
            _handles.Add(stream);
            return stream;
        }
    }

    public void Move(string source, string destination)
    {
        lock (_gate)
        {
            BeginMutation();
            var src = Normalize(source);
            var dst = Normalize(destination);
            EnsureVolumePresent(src);
            EnsureVolumePresent(dst);

            if (!_files.TryGetValue(src, out var file))
            {
                var srcParent = Path.GetDirectoryName(src);
                if (srcParent is null || !_dirs.ContainsKey(srcParent))
                {
                    throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {source}");
                }

                throw new FileNotFoundException($"파일을 찾을 수 없습니다: {source}", source);
            }

            var dstParent = Path.GetDirectoryName(dst) ?? throw new IOException($"루트로는 옮길 수 없습니다: {destination}");
            if (!_dirs.TryGetValue(dstParent, out var dstParentDisplay))
            {
                throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {destination}");
            }

            if (file.Writers > 0 || file.Readers > 0)
            {
                throw new IOException($"사용 중인 파일은 옮길 수 없습니다: {source}");
            }

            var sameFile = PathEquals(src, dst);
            if (!sameFile && (_files.ContainsKey(dst) || _dirs.ContainsKey(dst)))
            {
                throw new IOException($"대상이 이미 있습니다: {destination}");
            }

            // 같은 파일의 이름만 대소문자로 바꾸는 경우(NTFS가 허용)도 여기로 온다.
            _files.Remove(src);
            file.Path = Path.Combine(dstParentDisplay, Path.GetFileName(dst));
            _files[dst] = file;
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_gate)
        {
            BeginMutation();
            var full = Normalize(path);
            EnsureVolumePresent(full);
            ThrowIfFileInChain(full);
            EnsureDirectory(full);
        }
    }

    public bool IsDirectoryEmpty(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            EnsureVolumePresent(full);
            if (!_dirs.ContainsKey(full))
            {
                throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
            }

            return IsEmptyDirectory(full);
        }
    }

    public void RemoveEmptyDirectory(string path)
    {
        lock (_gate)
        {
            BeginMutation();
            var full = Normalize(path);
            EnsureVolumePresent(full);
            if (!_dirs.ContainsKey(full))
            {
                if (_files.ContainsKey(full))
                {
                    throw new IOException($"폴더가 아닙니다: {path}");
                }

                throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
            }

            if (Path.GetDirectoryName(full) is null)
            {
                throw new IOException($"볼륨 루트는 지울 수 없습니다: {path}");
            }

            if (!IsEmptyDirectory(full))
            {
                throw new IOException($"비어 있지 않은 폴더는 지우지 않습니다: {path}");
            }

            _dirs.Remove(full);
        }
    }

    public void AppendLine(string path, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        AppendLines(path, [line]);
    }

    /// <summary>변경 작업 하나로 센다. 줄은 하나씩 덧붙이지만 모의 크래시가 그 사이에 날 수 없으므로 원자적이다.</summary>
    public void AppendLines(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var list = lines.ToList();
        lock (_gate)
        {
            BeginMutation();
            var full = Normalize(path);
            EnsureVolumePresent(full);
            var parent = Path.GetDirectoryName(full) ?? throw new IOException($"루트에는 파일을 만들 수 없습니다: {path}");
            if (!_dirs.TryGetValue(parent, out var parentDisplay))
            {
                throw new DirectoryNotFoundException($"폴더를 찾을 수 없습니다: {path}");
            }

            if (_dirs.ContainsKey(full))
            {
                throw new UnauthorizedAccessException($"같은 이름의 폴더가 있습니다: {path}");
            }

            if (!_files.TryGetValue(full, out var file))
            {
                file = new FakeFile(Path.Combine(parentDisplay, Path.GetFileName(full)), NowUtc);
                _files[full] = file;
            }
            else if (file.Writers > 0 || file.Readers > 0)
            {
                throw new IOException($"사용 중인 파일입니다: {path}");
            }

            foreach (var line in list)
            {
                file.Append(Utf8NoBom.GetBytes(line + Environment.NewLine));
            }

            file.LastWriteTimeUtc = NowUtc;
        }
    }

    public void SetLastWriteTimeUtc(string path, DateTime utc)
    {
        lock (_gate)
        {
            BeginMutation();
            var full = Normalize(path);
            EnsureVolumePresent(full);
            if (!_files.TryGetValue(full, out var file))
            {
                throw new FileNotFoundException($"파일을 찾을 수 없습니다: {path}", path);
            }

            if (file.Writers > 0)
            {
                throw new IOException($"다른 곳에서 쓰는 중인 파일입니다: {path}");
            }

            file.LastWriteTimeUtc = ToUtc(utc);
        }
    }

    public string GetVolumeRoot(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            return Path.GetPathRoot(full) ?? throw new ArgumentException($"볼륨 루트를 알 수 없는 경로입니다: {path}", nameof(path));
        }
    }

    public long GetAvailableFreeSpace(string path)
    {
        lock (_gate)
        {
            var full = Normalize(path);
            EnsureVolumePresent(full);
            return _freeSpace.TryGetValue(VolumeKeyOf(full), out var free) ? free : long.MaxValue / 4;
        }
    }

    // ------------------------------------------------------------------
    // 내부: 경로·볼륨·폴더 도우미 (모두 _gate 안에서 호출)
    // ------------------------------------------------------------------

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("경로가 비어 있습니다.", nameof(path));
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"전체 경로가 아닙니다: {path}", nameof(path));
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    private static string VolumeKeyOf(string fullPath) => Path.GetPathRoot(fullPath)!.ToUpperInvariant();

    private static string VolumeKeyOfRoot(string volumeRoot)
    {
        if (volumeRoot.Length == 2 && volumeRoot[1] == ':')
        {
            volumeRoot += '\\';
        }

        return VolumeKeyOf(Normalize(volumeRoot));
    }

    private static bool PathEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string fullPath, string directory)
    {
        var prefix = directory.EndsWith('\\') ? directory : directory + "\\";
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private bool IsVolumeRemoved(string fullPath) => _removedVolumes.Contains(VolumeKeyOf(fullPath));

    private void EnsureVolumePresent(string fullPath)
    {
        if (IsVolumeRemoved(fullPath))
        {
            throw new DirectoryNotFoundException($"드라이브를 찾을 수 없습니다: {Path.GetPathRoot(fullPath)}");
        }
    }

    /// <summary>경로 자체나 그 상위 중 하나가 파일이면 IOException (아무것도 만들기 전에 검사한다).</summary>
    private void ThrowIfFileInChain(string fullDirectory)
    {
        for (string? p = fullDirectory; p is not null; p = Path.GetDirectoryName(p))
        {
            if (_files.ContainsKey(p))
            {
                throw new IOException($"같은 이름의 파일이 있어 폴더를 만들 수 없습니다: {p}");
            }
        }
    }

    private void EnsureDirectory(string fullDirectory)
    {
        if (_dirs.ContainsKey(fullDirectory))
        {
            return;
        }

        var parent = Path.GetDirectoryName(fullDirectory);
        if (parent is null)
        {
            _dirs[fullDirectory] = fullDirectory; // 볼륨 루트
            return;
        }

        EnsureDirectory(parent);
        _dirs[fullDirectory] = Path.Combine(_dirs[parent], Path.GetFileName(fullDirectory));
    }

    private bool IsEmptyDirectory(string fullDirectory) =>
        !_files.Keys.Any(k => PathEquals(Path.GetDirectoryName(k), fullDirectory))
        && !_dirs.Keys.Any(k => PathEquals(Path.GetDirectoryName(k), fullDirectory));

    // ------------------------------------------------------------------
    // 내부: 변경 작업 계수와 크래시
    // ------------------------------------------------------------------

    private void BeginMutation()
    {
        if (_crashAtOps is { } limit && _mutatingOps >= limit)
        {
            // 프로세스가 죽으면 열려 있던 핸들도 모두 닫힌다.
            foreach (var handle in _handles.ToArray())
            {
                handle.Release();
            }

            throw new SimulatedCrashException();
        }

        _mutatingOps++;
    }

    private void WriteTo(WriteStream stream, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (stream.Released)
            {
                throw new ObjectDisposedException(nameof(WriteStream));
            }

            BeginMutation();
            if (IsVolumeRemoved(stream.File.Path))
            {
                throw new IOException($"드라이브를 사용할 수 없습니다: {Path.GetPathRoot(stream.File.Path)}");
            }

            if (data.Length == 0)
            {
                return;
            }

            if (stream.CorruptFirstByte)
            {
                var copy = data.ToArray();
                copy[0] ^= 0xFF;
                stream.CorruptFirstByte = false;
                stream.File.Append(copy);
            }
            else
            {
                stream.File.Append(data);
            }

            stream.File.LastWriteTimeUtc = NowUtc;
        }
    }

    private void ReleaseHandle(IHandle handle)
    {
        lock (_gate)
        {
            handle.Release();
        }
    }

    // ------------------------------------------------------------------
    // 내부 타입
    // ------------------------------------------------------------------

    private interface IHandle
    {
        /// <summary>핸들을 닫는다. 여러 번 불러도 안전하다. _gate 안에서만 호출한다.</summary>
        void Release();
    }

    private sealed class FakeFile
    {
        private readonly MemoryStream _content = new();

        public FakeFile(string path, DateTime lastWriteTimeUtc)
        {
            Path = path;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        public string Path { get; set; }

        public DateTime LastWriteTimeUtc { get; set; }

        public int Readers { get; set; }

        public int Writers { get; set; }

        public ReadOnlySpan<byte> Content => _content.GetBuffer().AsSpan(0, (int)_content.Length);

        public void SetContent(byte[] content)
        {
            _content.SetLength(0);
            _content.Write(content);
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            _content.Seek(0, SeekOrigin.End);
            _content.Write(data);
        }
    }

    private sealed class ReadStream : MemoryStream, IHandle
    {
        private readonly FakeFileSystem _fs;
        private readonly FakeFile _file;
        private readonly bool _failOnRead;
        private bool _released;

        public ReadStream(FakeFileSystem fs, FakeFile file, bool failOnRead)
            : base(file.Content.ToArray(), writable: false)
        {
            _fs = fs;
            _file = file;
            _failOnRead = failOnRead;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfFaulted();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfFaulted();
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            ThrowIfFaulted();
            return base.ReadByte();
        }

        private void ThrowIfFaulted()
        {
            if (_failOnRead)
            {
                throw new IOException($"모의 읽기 오류(열린 뒤): {_file.Path}");
            }
        }

        // 크래시로 핸들이 풀려도 이미 가진 스냅샷은 읽을 수 있다(프로세스가 죽었으니 어차피 쓰이지 않는다).
        void IHandle.Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            _file.Readers--;
            _fs._handles.Remove(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fs.ReleaseHandle(this);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class WriteStream : Stream, IHandle
    {
        private readonly FakeFileSystem _fs;

        public WriteStream(FakeFileSystem fs, FakeFile file, bool corruptFirstByte)
        {
            _fs = fs;
            File = file;
            CorruptFirstByte = corruptFirstByte;
        }

        public FakeFile File { get; }

        public bool CorruptFirstByte { get; set; }

        public bool Released { get; private set; }

        void IHandle.Release()
        {
            if (Released)
            {
                return;
            }

            Released = true;
            File.Writers--;
            _fs._handles.Remove(this);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !Released;

        public override long Length
        {
            get { lock (_fs._gate) { return File.Content.Length; } }
        }

        public override long Position
        {
            get => Length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            _fs.WriteTo(this, buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => _fs.WriteTo(this, buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _fs.WriteTo(this, buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fs.ReleaseHandle(this);
            }

            base.Dispose(disposing);
        }
    }
}
