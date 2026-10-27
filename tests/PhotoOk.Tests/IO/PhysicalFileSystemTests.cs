using System.IO;
using System.Text;
using PhotoOk.Core.IO;

namespace PhotoOk.Tests.IO;

public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photook-tests", Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileSystem _fs = new();

    public PhysicalFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 테스트 결과에 영향을 주지 않는다.
        }
    }

    private string P(string name) => Path.Combine(_root, name);

    private static void Write(Stream stream, string text)
    {
        using (stream)
        {
            stream.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    [Fact]
    public void CreateNew_Throws_WhenFileExists()
    {
        var path = P("a.txt");
        Write(_fs.CreateNew(path), "first");

        Assert.Throws<IOException>(() => _fs.CreateNew(path));

        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void Move_Throws_AndKeepsBoth_WhenDestinationExists()
    {
        var src = P("src.txt");
        var dst = P("dst.txt");
        File.WriteAllText(src, "source");
        File.WriteAllText(dst, "destination");

        Assert.Throws<IOException>(() => _fs.Move(src, dst));

        Assert.Equal("source", File.ReadAllText(src));
        Assert.Equal("destination", File.ReadAllText(dst));
    }

    [Fact]
    public void Move_MovesFile_WhenDestinationFree()
    {
        var src = P("src.txt");
        var dst = P("dst.txt");
        File.WriteAllText(src, "source");

        _fs.Move(src, dst);

        Assert.False(File.Exists(src));
        Assert.Equal("source", File.ReadAllText(dst));
    }

    [Fact]
    public void RemoveEmptyDirectory_Throws_WhenNotEmpty()
    {
        var dir = P("d");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "x");

        Assert.Throws<IOException>(() => _fs.RemoveEmptyDirectory(dir));

        Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
    }

    [Fact]
    public void RemoveEmptyDirectory_RemovesEmptyDirectory()
    {
        var dir = P("d");
        Directory.CreateDirectory(dir);
        Assert.True(_fs.IsDirectoryEmpty(dir));

        _fs.RemoveEmptyDirectory(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void IsDirectoryEmpty_ReturnsFalse_WhenSubdirectoryPresent()
    {
        var dir = P("d");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));

        Assert.False(_fs.IsDirectoryEmpty(dir));
    }

    [Fact]
    public void AppendLine_AppendsLines_InOrder()
    {
        var path = P("log.jsonl");

        _fs.AppendLine(path, "a");
        _fs.AppendLine(path, "b");

        Assert.Equal("a" + Environment.NewLine + "b" + Environment.NewLine, File.ReadAllText(path));
    }

    [Fact]
    public void AppendLine_WritesUtf8WithoutBom()
    {
        var path = P("log.jsonl");

        _fs.AppendLine(path, "가");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(Encoding.UTF8.GetBytes("가" + Environment.NewLine), bytes);
    }

    [Fact]
    public void GetVolumeRoot_ReturnsDriveRoot()
    {
        Assert.Equal(@"C:\", _fs.GetVolumeRoot(@"C:\x\y.jpg"));
    }

    [Fact]
    public void GetFile_ReturnsLengthAndUtcTime()
    {
        var path = P("a.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var entry = _fs.GetFile(path);

        Assert.Equal(3, entry.Length);
        Assert.Equal(stamp, entry.LastWriteTimeUtc);
        Assert.Equal(DateTimeKind.Utc, entry.LastWriteTimeUtc.Kind);
    }

    [Fact]
    public void GetFile_Throws_WhenMissing()
    {
        Assert.Throws<FileNotFoundException>(() => _fs.GetFile(P("none.bin")));
    }

    [Fact]
    public void EnumerateFiles_RespectsRecursiveFlag()
    {
        Directory.CreateDirectory(P("sub"));
        File.WriteAllText(P("top.txt"), "t");
        File.WriteAllText(Path.Combine(P("sub"), "deep.txt"), "d");

        var flat = _fs.EnumerateFiles(_root, recursive: false).ToList();
        var deep = _fs.EnumerateFiles(_root, recursive: true).ToList();

        Assert.Equal(new[] { P("top.txt") }, flat);
        Assert.Equal(2, deep.Count);
        Assert.Contains(Path.Combine(P("sub"), "deep.txt"), deep);
    }

    [Fact]
    public void OpenRead_AllowsConcurrentReaders_ButNotWriting()
    {
        var path = P("a.txt");
        File.WriteAllText(path, "hello");

        using var first = _fs.OpenRead(path);
        using var second = _fs.OpenRead(path);

        Assert.False(first.CanWrite);
        Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }

    [Fact]
    public void CreateDirectory_CreatesParents_AndIsIdempotent()
    {
        var dir = Path.Combine(P("a"), "b", "c");

        _fs.CreateDirectory(dir);
        _fs.CreateDirectory(dir);

        Assert.True(_fs.DirectoryExists(dir));
        Assert.False(_fs.FileExists(dir));
    }

    // 아래 테스트들은 FakeFileSystem이 실제 디스크와 같은 예외·잠금 동작을 따르는지 기준을 고정한다.

    [Fact]
    public void Move_Throws_WhenSourceMissing_OrDestinationDirectoryMissing()
    {
        var src = P("src.txt");
        File.WriteAllText(src, "s");

        Assert.Throws<FileNotFoundException>(() => _fs.Move(P("none.txt"), P("x.txt")));
        Assert.Throws<DirectoryNotFoundException>(() => _fs.Move(src, Path.Combine(P("nodir"), "x.txt")));
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Move_Throws_WhenDestinationIsExistingDirectory()
    {
        var src = P("src.txt");
        File.WriteAllText(src, "s");
        Directory.CreateDirectory(P("dir"));

        Assert.ThrowsAny<IOException>(() => _fs.Move(src, P("dir")));

        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Move_Throws_WhileSourceIsOpenForRead()
    {
        var src = P("src.txt");
        File.WriteAllText(src, "s");

        using (_fs.OpenRead(src))
        {
            Assert.Throws<IOException>(() => _fs.Move(src, P("dst.txt")));
        }

        _fs.Move(src, P("dst.txt"));
        Assert.True(File.Exists(P("dst.txt")));
    }

    [Fact]
    public void Move_CaseOnlyRename_Succeeds()
    {
        var src = P("a.txt");
        File.WriteAllText(src, "s");

        _fs.Move(src, P("A.TXT"));

        Assert.Equal("A.TXT", Path.GetFileName(Directory.EnumerateFiles(_root).Single()));
    }

    [Fact]
    public void OpenRead_Throws_WhileFileIsBeingWritten()
    {
        var path = P("a.bin");

        using (_fs.CreateNew(path))
        {
            Assert.Throws<IOException>(() => _fs.OpenRead(path));
        }

        using var ok = _fs.OpenRead(path);
    }

    [Fact]
    public void OpenRead_Throws_WhenFileMissing_OrDirectoryMissing()
    {
        Assert.Throws<FileNotFoundException>(() => _fs.OpenRead(P("none.bin")));
        Assert.Throws<DirectoryNotFoundException>(() => _fs.OpenRead(Path.Combine(P("nodir"), "none.bin")));
    }

    [Fact]
    public void CreateNew_Throws_WhenParentMissing_OrPathIsDirectory()
    {
        Directory.CreateDirectory(P("dir"));

        Assert.Throws<DirectoryNotFoundException>(() => _fs.CreateNew(Path.Combine(P("nodir"), "a.bin")));
        Assert.Throws<UnauthorizedAccessException>(() => _fs.CreateNew(P("dir")));
        Assert.True(Directory.Exists(P("dir")));
    }

    [Fact]
    public void CreateDirectory_Throws_WhenFileInTheWay()
    {
        File.WriteAllText(P("f"), "x");

        Assert.Throws<IOException>(() => _fs.CreateDirectory(P("f")));
        Assert.Throws<IOException>(() => _fs.CreateDirectory(Path.Combine(P("f"), "sub")));
    }

    [Fact]
    public void RemoveEmptyDirectory_Throws_WhenMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _fs.RemoveEmptyDirectory(P("none")));
    }

    [Fact]
    public void EnumerateFiles_IncludesHiddenAndSystemFiles()
    {
        Directory.CreateDirectory(P("sub"));
        var hidden = P("hidden.jpg");
        var system = Path.Combine(P("sub"), "system.jpg");
        File.WriteAllText(hidden, "h");
        File.WriteAllText(system, "s");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.SetAttributes(system, FileAttributes.Hidden | FileAttributes.System);

        try
        {
            var files = _fs.EnumerateFiles(_root, recursive: true).ToList();

            Assert.Contains(hidden, files);
            Assert.Contains(system, files);
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            File.SetAttributes(system, FileAttributes.Normal);
        }
    }

    /// <summary>현재 사용자에게 이 폴더의 목록 보기를 거부한다. Dispose하면 거부 규칙을 뗀다.</summary>
    internal static IDisposable DenyListing(string directory)
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(user,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        var restore = new Restore(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(deny);
            info.SetAccessControl(current);
        });

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFiles(directory)); // 정말 열 수 없는 폴더인지 확인
        }
        catch
        {
            restore.Dispose();
            throw;
        }

        return restore;
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    [Fact]
    public void EnumerateFiles_SkipsInaccessibleFolder_ReportsIt_AndKeepsTheRest()
    {
        // SD 카드 루트의 System Volume Information처럼 열 수 없는 폴더가 있어도 나머지 파일은 모두 나와야 하고, 건너뛴 폴더는 알려야 한다.
        var locked = P("a-locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "secret.jpg"), "x");
        Directory.CreateDirectory(P("z-open"));
        File.WriteAllText(P("top.jpg"), "t");
        File.WriteAllText(Path.Combine(P("z-open"), "deep.jpg"), "d");

        using (DenyListing(locked))
        {
            var skipped = new List<(string Path, Exception Error)>();
            var files = _fs.EnumerateFiles(_root, recursive: true, (path, ex) => skipped.Add((path, ex))).ToList();

            Assert.Equal(
                new[] { P("top.jpg"), Path.Combine(P("z-open"), "deep.jpg") }.Order(StringComparer.OrdinalIgnoreCase),
                files.Order(StringComparer.OrdinalIgnoreCase));
            var (path, error) = Assert.Single(skipped);
            Assert.Equal(locked, path);
            Assert.IsType<UnauthorizedAccessException>(error);
        }
    }

    [Fact]
    public void EnumerateFiles_InaccessibleRoot_IsReported_NotEmpty()
    {
        var locked = P("locked-root");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "a.jpg"), "x");

        using (DenyListing(locked))
        {
            Assert.True(Directory.Exists(locked)); // 존재 확인은 통과하는 폴더다

            var skipped = new List<string>();
            var files = _fs.EnumerateFiles(locked, recursive: true, (path, _) => skipped.Add(path)).ToList();

            Assert.Empty(files);
            Assert.Equal([locked], skipped);
        }
    }

    [Fact]
    public void EnumerateFiles_WithoutCallback_ThrowsForInaccessibleFolder_InsteadOfSkippingSilently()
    {
        var locked = P("a-locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(P("top.jpg"), "t");

        using (DenyListing(locked))
        {
            Assert.Throws<UnauthorizedAccessException>(() => _fs.EnumerateFiles(_root, recursive: true).ToList());
        }
    }

    [Fact]
    public void EnumerateFiles_Throws_WhenDirectoryMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _fs.EnumerateFiles(P("none"), recursive: true).ToList());
    }

    [Fact]
    public void AppendLine_Throws_WhenParentMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(() => _fs.AppendLine(Path.Combine(P("nodir"), "log"), "a"));
    }

    [Fact]
    public void GetAvailableFreeSpace_ReturnsPositiveValue()
    {
        Assert.True(_fs.GetAvailableFreeSpace(_root) > 0);
    }
}
