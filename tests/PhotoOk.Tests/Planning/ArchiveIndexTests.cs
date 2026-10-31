using System.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Planning;

public class ArchiveIndexTests
{
    private const string Root = @"D:\Archive";

    private readonly FakeFileSystem _fs = new();

    private ArchiveIndex Build(Action<string, Exception>? onInaccessible = null, CancellationToken ct = default) =>
        ArchiveIndex.Build(_fs, Root, onInaccessible, ct);

    [Fact]
    public void Build_GroupsByLength()
    {
        _fs.AddFile(@"D:\Archive\2026\a.jpg", [1, 2, 3]);
        _fs.AddFile(@"D:\Archive\2026\b.jpg", [4, 5, 6]);
        _fs.AddFile(@"D:\Archive\2027\c.jpg", [1]);

        var index = Build();

        Assert.Equal(3, index.Count);
        Assert.Equal([@"D:\Archive\2026\a.jpg", @"D:\Archive\2026\b.jpg"], index.WithLength(3).Order(StringComparer.Ordinal));
        Assert.Equal([@"D:\Archive\2027\c.jpg"], index.WithLength(1));
        Assert.Empty(index.WithLength(99));
    }

    [Fact]
    public void Build_ExcludesPhotookFolderAndTempFiles()
    {
        _fs.AddFile(@"D:\Archive\a.jpg", [1]);
        _fs.AddFile(@"D:\Archive\.photook\되돌림\a.jpg", [1]);
        _fs.AddFile(@"D:\Archive\a.jpg.photook-tmp", [1]);

        var index = Build();

        Assert.Equal(1, index.Count);
        Assert.Equal([@"D:\Archive\a.jpg"], index.WithLength(1));
    }

    [Fact]
    public void Build_ReportsInaccessibleFolder()
    {
        _fs.AddFile(@"D:\Archive\ok\a.jpg", [1]);
        _fs.AddFile(@"D:\Archive\locked\b.jpg", [2, 2]);
        _fs.DenyDirectory(@"D:\Archive\locked");
        var reported = new List<string>();

        var index = Build((folder, _) => reported.Add(folder));

        Assert.Equal([@"D:\Archive\locked"], reported);
        Assert.Equal(1, index.Count);
        Assert.Empty(index.WithLength(2));
    }

    [Fact]
    public void Build_MissingRoot_IsEmpty()
    {
        var reported = new List<string>();

        var index = Build((folder, _) => reported.Add(folder));

        Assert.Equal(0, index.Count);
        Assert.Empty(index.WithLength(1));
        Assert.Empty(reported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_MidWalkFailure_IsReportedNotSwallowed(bool directoryNotFound)
    {
        _fs.AddFile(@"D:\Archive\a.jpg", [1]);
        _fs.AddFile(@"D:\Archive\b.jpg", [2]);
        var hooked = new HookedFileSystem(_fs);
        hooked.OnEnumerated = path =>
        {
            if (path.EndsWith("b.jpg", StringComparison.Ordinal))
            {
                throw directoryNotFound ? new DirectoryNotFoundException("gone") : new IOException("broken");
            }
        };
        var reported = new List<string>();

        var index = ArchiveIndex.Build(hooked, Root, (path, _) => reported.Add(path), CancellationToken.None);

        Assert.Equal([Root], reported);
        Assert.Equal([@"D:\Archive\a.jpg"], index.WithLength(1));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Build_UnreadableFileSize_IsReported()
    {
        _fs.AddFile(@"D:\Archive\a.jpg", [1]);
        _fs.AddFile(@"D:\Archive\b.jpg", [2]);
        var hooked = new HookedFileSystem(_fs)
        {
            BeforeGetFile = path =>
            {
                if (path.EndsWith("b.jpg", StringComparison.Ordinal))
                {
                    throw new IOException("locked");
                }
            },
        };
        var reported = new List<string>();

        var index = ArchiveIndex.Build(hooked, Root, (path, _) => reported.Add(path), CancellationToken.None);

        Assert.Equal([@"D:\Archive\b.jpg"], reported);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Build_CancelledDuringWalk_Throws()
    {
        _fs.AddFile(@"D:\Archive\a.jpg", [1]);
        _fs.AddFile(@"D:\Archive\b.jpg", [2]);
        using var cts = new CancellationTokenSource();
        var hooked = new HookedFileSystem(_fs) { OnEnumerated = _ => cts.Cancel() };

        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveIndex.Build(hooked, Root, null, cts.Token));
    }

    [Fact]
    public void Build_Cancellation_Throws()
    {
        _fs.AddFile(@"D:\Archive\a.jpg", [1]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Build(ct: cts.Token));
    }
}
