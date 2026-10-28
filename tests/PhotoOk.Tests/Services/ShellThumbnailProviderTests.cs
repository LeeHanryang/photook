using System.IO;
using System.Windows.Media;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;

namespace PhotoOk.Tests.Services;

/// <summary>
/// 실제 Windows 셸을 쓰는 검사. 임시 폴더의 파일만 읽는다. 셸 상태에 따라 불안정할 수 있어 "Shell" 분류로 묶는다
/// (<c>dotnet test --filter "Category!=Shell"</c>로 뺄 수 있다).
/// </summary>
[Trait("Category", "Shell")]
public sealed class ShellThumbnailProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photook-tests", Guid.NewGuid().ToString("N"));
    private readonly ShellThumbnailProvider _provider = new(workerCount: 1);

    public ShellThumbnailProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 셸이 아직 파일을 잡고 있으면 임시 폴더가 남을 수 있다. 결과에는 영향이 없다.
        }
    }

    [Fact]
    public async Task Jpeg_GivesFrozenImage()
    {
        var path = Path.Combine(_root, "a.jpg");
        File.WriteAllBytes(path, TestImages.Jpeg(new DateTime(2026, 10, 14, 9, 0, 0)));

        var image = await _provider.GetAsync(path, 96, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(image);
        Assert.True(image.IsFrozen);
        Assert.IsAssignableFrom<ImageSource>(image);
    }

    [Fact]
    public async Task MissingFile_GivesNull()
    {
        var image = await _provider.GetAsync(Path.Combine(_root, "없음.jpg"), 96, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(image);
    }

    [Fact]
    public async Task Cancelled_GivesNull()
    {
        var path = Path.Combine(_root, "b.jpg");
        File.WriteAllBytes(path, TestImages.Jpeg());

        var image = await _provider.GetAsync(path, 96, new CancellationToken(canceled: true));

        Assert.Null(image);
    }
}

/// <summary>닫기 동작. 셸을 부르지 않는다.</summary>
public sealed class ShellThumbnailProviderDisposeTests
{
    [Fact]
    public async Task Dispose_StopsWorkers_AndLaterRequestsGiveNull()
    {
        var provider = new ShellThumbnailProvider(workerCount: 2);

        provider.Dispose();
        provider.Dispose(); // 두 번 불러도 된다

        Assert.True(provider.IsStopped);
        var image = await provider.GetAsync(@"C:\없음\a.jpg", 96, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(image);
    }
}
