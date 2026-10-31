using System.IO;
using System.Text;
using PhotoOk.Core.Naming;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Naming;

public class IgnoredFolderNamesTests
{
    private const string Path = @"C:\app\PhotoOk\ignored-folder-names.txt";

    [Fact]
    public void Empty_ContainsNothing()
    {
        Assert.False(IgnoredFolderNames.Empty.Contains("셀렉션"));
    }

    [Fact]
    public void Load_MissingFile_IsEmpty()
    {
        var names = IgnoredFolderNames.Load(new FakeFileSystem(), Path);

        Assert.False(names.Contains("셀렉션"));
    }

    [Fact]
    public void Load_UnreadableFile_IsEmpty()
    {
        var failing = new FakeFileSystem();
        failing.AddFile(Path, "셀렉션\r\n"u8.ToArray());
        failing.FailReadsOf(Path);
        Assert.False(IgnoredFolderNames.Load(failing, Path).Contains("셀렉션"));

        var denied = new FakeFileSystem();
        denied.AddFile(Path, "셀렉션\r\n"u8.ToArray());
        denied.DenyReadsOf(Path);
        Assert.False(IgnoredFolderNames.Load(denied, Path).Contains("셀렉션"));
    }

    [Fact]
    public void Load_IgnoresBlankLines_AndComparesLoosely()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path, "\r\n셀렉션\r\n   \r\nMy Event\r\n\r\n"u8.ToArray());

        var names = IgnoredFolderNames.Load(fs, Path);

        Assert.True(names.Contains("셀렉션"));
        Assert.True(names.Contains("my_event"));
        Assert.True(names.Contains("MY-EVENT"));
        Assert.True(names.Contains("셀렉션2")); // 끝의 숫자를 뗀 값도 비교한다.
        Assert.False(names.Contains(""));
        Assert.False(names.Contains("  "));
        Assert.False(names.Contains("다른 행사"));
    }

    [Fact]
    public void Load_ReadsFileWithByteOrderMark()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path, [.. Encoding.UTF8.GetPreamble(), .. "셀렉션\r\n"u8.ToArray()]);

        Assert.True(IgnoredFolderNames.Load(fs, Path).Contains("셀렉션"));
    }

    [Fact]
    public void Add_AppendsLine_KeepingExistingLines()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path, "셀렉션\r\n"u8.ToArray());

        IgnoredFolderNames.Add(fs, Path, "모음집");

        var text = Encoding.UTF8.GetString(fs.ReadAllBytes(Path));
        Assert.Equal(["셀렉션", "모음집"], text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
        var names = IgnoredFolderNames.Load(fs, Path);
        Assert.True(names.Contains("셀렉션"));
        Assert.True(names.Contains("모음집"));
    }

    [Fact]
    public void Add_CreatesFileAndFolder()
    {
        var fs = new FakeFileSystem();

        IgnoredFolderNames.Add(fs, Path, "모음집");

        Assert.True(IgnoredFolderNames.Load(fs, Path).Contains("모음집"));
    }

    [Fact]
    public void Add_AfterLineWithoutTrailingNewline_DoesNotGlueLines()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path, "셀렉션"u8.ToArray());

        IgnoredFolderNames.Add(fs, Path, "모음집");

        var names = IgnoredFolderNames.Load(fs, Path);
        Assert.True(names.Contains("셀렉션"));
        Assert.True(names.Contains("모음집"));
    }

    [Fact]
    public void Add_Twice_OneLine()
    {
        var fs = new FakeFileSystem();

        IgnoredFolderNames.Add(fs, Path, "모음집");
        IgnoredFolderNames.Add(fs, Path, "모음집");
        IgnoredFolderNames.Add(fs, Path, " 모음 집 "); // 같은 이름으로 본다.

        var text = Encoding.UTF8.GetString(fs.ReadAllBytes(Path));
        Assert.Single(text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Add_BlankName_WritesNothing()
    {
        var fs = new FakeFileSystem();

        IgnoredFolderNames.Add(fs, Path, "   ");

        Assert.False(fs.FileExists(Path));
    }

    [Fact]
    public void Add_NameWithLineBreak_StaysOneLine()
    {
        var fs = new FakeFileSystem();

        IgnoredFolderNames.Add(fs, Path, "앞\r\n뒤");

        var text = Encoding.UTF8.GetString(fs.ReadAllBytes(Path));
        Assert.Single(text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Add_UnreadableFile_Propagates()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Path, "셀렉션\r\n"u8.ToArray());
        fs.FailReadsOf(Path);

        Assert.Throws<IOException>(() => IgnoredFolderNames.Add(fs, Path, "모음집"));
    }
}
