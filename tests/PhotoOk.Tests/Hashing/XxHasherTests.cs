using System.IO;
using System.IO.Hashing;
using PhotoOk.Core.Hashing;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Hashing;

public class XxHasherTests
{
    [Fact]
    public void Hash_IsStableLowercaseHex()
    {
        var fs = new FakeFileSystem();
        byte[] content = [0xAB, 0xCD, 0xEF, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        fs.AddFile(@"D:\a\one.bin", content);
        fs.AddFile(@"D:\b\two.bin", content);
        fs.AddFile(@"D:\b\other.bin", [9, 9, 9]);
        var hasher = new XxHasher(fs);

        var one = hasher.Hash(@"D:\a\one.bin");
        var two = hasher.Hash(@"D:\b\two.bin");

        Assert.Equal(one, two);
        Assert.Equal(16, one.Length);
        Assert.Equal(one.ToLowerInvariant(), one);
        Assert.Matches("^[0-9a-f]{16}$", one);
        Assert.Equal(XxHasher.Format(XxHash64.HashToUInt64(content)), one);
        Assert.NotEqual(one, hasher.Hash(@"D:\b\other.bin"));
    }

    [Fact]
    public void Format_PadsToSixteenLowercaseDigits()
    {
        Assert.Equal("00000000000000ab", XxHasher.Format(0xAB));
        Assert.Equal("ffffffffffffffff", XxHasher.Format(ulong.MaxValue));
    }

    [Fact]
    public void Hash_ReleasesReadHandle()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(@"D:\a\one.bin", [1, 2, 3]);
        fs.AddDirectory(@"D:\b");

        _ = new XxHasher(fs).Hash(@"D:\a\one.bin");

        fs.Move(@"D:\a\one.bin", @"D:\b\one.bin"); // 열린 읽기 핸들이 남아 있으면 IOException
        Assert.True(fs.FileExists(@"D:\b\one.bin"));
    }
}
