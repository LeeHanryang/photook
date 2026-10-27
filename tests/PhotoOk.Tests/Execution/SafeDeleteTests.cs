using System.IO;
using PhotoOk.Core.Execution;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Execution;

public class SafeDeleteTests
{
    private const string Archive = @"D:\Archive";
    private const string RunId = "20261027-103000-ab12";

    private readonly FakeFileSystem _fs = new();

    private SafeDelete NewSafeDelete() => new(_fs, Archive, RunId);

    [Fact]
    public void QuarantineRoot_UsesExpectedLayout()
    {
        Assert.Equal(@"D:\Archive\.photook\되돌림\20261027-103000-ab12", NewSafeDelete().QuarantineRoot);
    }

    [Fact]
    public void Quarantine_MovesUnderPhotookFolder_KeepingRelativePath()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        _fs.AddFile(@"D:\Archive\2026\a.jpg", content);

        var result = NewSafeDelete().Quarantine(@"D:\Archive\2026\a.jpg");

        var expected = @"D:\Archive\.photook\되돌림\20261027-103000-ab12\2026\a.jpg";
        Assert.Equal(expected, result);
        Assert.False(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
        Assert.Equal(content, _fs.ReadAllBytes(expected));
    }

    [Fact]
    public void Quarantine_Throws_ForPathOutsideArchive()
    {
        _fs.AddFile(@"E:\Other\a.jpg", new byte[] { 1 });
        _fs.AddFile(@"D:\Archive2\a.jpg", new byte[] { 1 });
        var safeDelete = NewSafeDelete();

        Assert.Throws<InvalidOperationException>(() => safeDelete.Quarantine(@"E:\Other\a.jpg"));
        Assert.Throws<InvalidOperationException>(() => safeDelete.Quarantine(@"D:\Archive2\a.jpg"));
        Assert.True(_fs.FileExists(@"E:\Other\a.jpg"));
        Assert.True(_fs.FileExists(@"D:\Archive2\a.jpg"));
    }

    [Fact]
    public void Quarantine_Throws_ForPathAlreadyInsidePhotookFolder()
    {
        _fs.AddFile(@"D:\Archive\.photook\journal\r1.jsonl", new byte[] { 1 });

        Assert.Throws<InvalidOperationException>(() => NewSafeDelete().Quarantine(@"D:\Archive\.photook\journal\r1.jsonl"));
        Assert.True(_fs.FileExists(@"D:\Archive\.photook\journal\r1.jsonl"));
    }

    [Fact]
    public void Quarantine_Throws_ForArchiveRootItself()
    {
        _fs.AddDirectory(Archive);

        Assert.Throws<InvalidOperationException>(() => NewSafeDelete().Quarantine(Archive));
    }

    [Fact]
    public void Quarantine_AddsSuffix_WhenNameTaken()
    {
        var root = NewSafeDelete().QuarantineRoot;
        _fs.AddFile(@"D:\Archive\2026\a.jpg", new byte[] { 1 });
        _fs.AddFile(root + @"\2026\a.jpg", new byte[] { 9 });
        _fs.AddFile(root + @"\2026\a_1.jpg", new byte[] { 8 });

        var result = NewSafeDelete().Quarantine(@"D:\Archive\2026\a.jpg");

        Assert.Equal(root + @"\2026\a_2.jpg", result);
        Assert.Equal(new byte[] { 1 }, _fs.ReadAllBytes(result));
        Assert.Equal(new byte[] { 9 }, _fs.ReadAllBytes(root + @"\2026\a.jpg"));
        Assert.Equal(new byte[] { 8 }, _fs.ReadAllBytes(root + @"\2026\a_1.jpg"));
    }

    [Fact]
    public void Quarantine_SuffixGoesBeforeLastExtension()
    {
        var root = NewSafeDelete().QuarantineRoot;
        _fs.AddFile(@"D:\Archive\x.photook-tmp", new byte[] { 1 });
        _fs.AddFile(root + @"\x.photook-tmp", new byte[] { 9 });

        var result = NewSafeDelete().Quarantine(@"D:\Archive\x.photook-tmp");

        Assert.Equal(root + @"\x_1.photook-tmp", result);
    }

    [Fact]
    public void Quarantine_AcceptsCaseDifferentArchivePath()
    {
        _fs.AddFile(@"D:\Archive\a.jpg", new byte[] { 1 });

        var result = NewSafeDelete().Quarantine(@"d:\ARCHIVE\a.jpg");

        Assert.Equal(NewSafeDelete().QuarantineRoot + @"\a.jpg", result);
        Assert.False(_fs.FileExists(@"D:\Archive\a.jpg"));
    }

    [Fact]
    public void Quarantine_Works_WhenArchiveIsDriveRoot()
    {
        _fs.AddFile(@"D:\2026\a.jpg", new byte[] { 1 });
        var safeDelete = new SafeDelete(_fs, @"D:\", RunId);

        var result = safeDelete.Quarantine(@"D:\2026\a.jpg");

        Assert.Equal(@"D:\.photook\되돌림\20261027-103000-ab12\2026\a.jpg", result);
        Assert.False(_fs.FileExists(@"D:\2026\a.jpg"));
    }

    [Fact]
    public void RemoveEmptyDirectory_ReturnsFalse_WhenNotEmpty()
    {
        _fs.AddFile(@"D:\Archive\2026\a.jpg", new byte[] { 1 });

        Assert.False(NewSafeDelete().RemoveEmptyDirectory(@"D:\Archive\2026"));
        Assert.True(_fs.DirectoryExists(@"D:\Archive\2026"));
        Assert.True(_fs.FileExists(@"D:\Archive\2026\a.jpg"));
    }

    [Fact]
    public void RemoveEmptyDirectory_ReturnsFalse_WhenMissing()
    {
        Assert.False(NewSafeDelete().RemoveEmptyDirectory(@"D:\Archive\nothing"));
    }

    [Fact]
    public void RemoveEmptyDirectory_RemovesEmptyFolder()
    {
        _fs.AddDirectory(@"D:\Archive\2026\empty");

        Assert.True(NewSafeDelete().RemoveEmptyDirectory(@"D:\Archive\2026\empty"));
        Assert.False(_fs.DirectoryExists(@"D:\Archive\2026\empty"));
        Assert.True(_fs.DirectoryExists(@"D:\Archive\2026"));
    }
}
