using System.IO;
using System.Threading;
using PhotoOk.Services;

namespace PhotoOk.Tests.Services;

/// <summary>설치 파일이 앱이 실행 중인지 알아보는 표시(뮤텍스).</summary>
public class RunningMarkerTests
{
    private static string UniqueName() => "PhotoOkTest" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Name_MatchesWhatTheInstallerLooksFor()
    {
        Assert.Equal(@"Local\PhotoOkRunning", RunningMarker.MutexName);
    }

    [Fact]
    public void Held_IsFoundByThePlainName_TheWayTheInstallerAsks()
    {
        // 설치 프로그램은 앞에 Local\을 붙이지 않고 이름만으로 찾는다. 같은 세션에서는 같은 뮤텍스여야 한다.
        var name = UniqueName();

        using var held = RunningMarker.TryHold(@"Local\" + name);

        Assert.NotNull(held);
        Assert.True(Mutex.TryOpenExisting(name, out var found));
        found!.Dispose();
    }

    [Fact]
    public void SecondInstance_IsAllowed()
    {
        var name = @"Local\" + UniqueName();

        using var first = RunningMarker.TryHold(name);
        using var second = RunningMarker.TryHold(name);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void Released_IsNoLongerFound()
    {
        var name = UniqueName();
        RunningMarker.TryHold(@"Local\" + name)!.Dispose();

        Assert.False(Mutex.TryOpenExisting(name, out _));
    }

    [Fact]
    public void BadName_DoesNotStopTheApp()
    {
        // 표시를 만들지 못해도 앱은 시작한다. 없는 이름 공간으로 만들 수 없는 경우를 대신한다.
        Assert.Null(RunningMarker.TryHold(@"NoSuchNamespace\" + UniqueName()));
    }

    [Fact]
    public void App_HoldsTheMarkerFromStartup()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PhotoOk", "App.xaml.cs"));
        var startup = source[source.IndexOf("protected override void OnStartup", StringComparison.Ordinal)..];

        Assert.True(startup.IndexOf("RunningMarker.TryHold", StringComparison.Ordinal) is > 0 and var hold
            && hold < startup.IndexOf("Compose(fileSystem)", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoOk.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("레포 루트를 찾지 못했습니다.");
    }
}
