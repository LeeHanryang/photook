using System.IO;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.Workflow;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Workflow;

public class CachingHasherTests
{
    private static readonly DateTime Time1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Time2 = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly FakeFileSystem _fs = new();
    private readonly CountingHasher _counting;
    private readonly CachingHasher _caching;

    public CachingHasherTests()
    {
        _counting = new CountingHasher(new XxHasher(_fs));
        _caching = new CachingHasher(_counting, _fs);
    }

    [Fact]
    public void CachingHasher_ReusesHash_ForSameFile_IgnoringPathCase()
    {
        _fs.AddFile(@"D:\A\one.jpg", [1, 2, 3], Time1);

        var first = _caching.Hash(@"D:\A\one.jpg");
        var second = _caching.Hash(@"d:\a\ONE.JPG");

        Assert.Equal(first, second);
        Assert.Equal(1, _counting.TotalCalls);
    }

    [Fact]
    public void CachingHasher_Rehashes_WhenFileChanges()
    {
        _fs.AddFile(@"D:\A\one.jpg", [1, 2, 3], Time1);
        var original = _caching.Hash(@"D:\A\one.jpg");

        // 수정 시각만 바뀐 경우
        _fs.AddFile(@"D:\A\one.jpg", [1, 2, 3], Time2);
        Assert.Equal(original, _caching.Hash(@"D:\A\one.jpg"));
        Assert.Equal(2, _counting.TotalCalls);

        // 크기가 바뀐 경우
        _fs.AddFile(@"D:\A\one.jpg", [1, 2, 3, 4], Time2);
        var changed = _caching.Hash(@"D:\A\one.jpg");
        Assert.NotEqual(original, changed);
        Assert.Equal(3, _counting.TotalCalls);
    }

    [Fact]
    public void CachingHasher_PropagatesMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() => _caching.Hash(@"D:\A\missing.jpg"));
        Assert.Equal(0, _counting.TotalCalls);
    }
}

/// <summary>호출 횟수를 세는 해시 래퍼. 경로는 대소문자를 무시하고 센다.</summary>
internal sealed class CountingHasher(IHasher inner) : IHasher
{
    public Dictionary<string, int> CallsByPath { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalCalls => CallsByPath.Values.Sum();

    public string Hash(string path)
    {
        CallsByPath[path] = CallsByPath.GetValueOrDefault(path) + 1;
        return inner.Hash(path);
    }
}
