using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace PhotoOk.Tests.Views;

/// <summary>뷰 XAML의 묶음(바인딩)과 크기를 확인한다. 화면을 띄우지 않고 XAML 파일을 읽어 본다.</summary>
public class ViewLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>창 테두리와 세로 스크롤 막대 몫.</summary>
    private const double WindowChrome = 16;

    private static XDocument Load(params string[] parts) =>
        XDocument.Load(Path.Combine([RepoRoot(), "src", "PhotoOk", .. parts]));

    private static double Number(XElement element, string attribute) =>
        double.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);

    [Fact]
    public void MainWindow_MinWidth_FitsThePreviewPanes()
    {
        var window = Load("MainWindow.xaml").Root!;
        var preview = Load("Views", "PreviewView.xaml");

        // 세 칸: 고정 너비 칸은 줄어들지 않고, 가운데(*) 칸은 MinWidth까지 줄어든다.
        var columns = preview.Descendants(Wpf + "ColumnDefinition")
            .Where(c => c.Attribute("MinWidth") is not null)
            .ToList();
        Assert.Equal(3, columns.Count);
        var panes = columns.Sum(c => c.Attribute("Width")!.Value == "*" ? Number(c, "MinWidth") : Number(c, "Width"));
        var outerMargin = 2 * double.Parse(preview.Root!.Element(Wpf + "Grid")!.Attribute("Margin")!.Value.Split(',')[0],
            CultureInfo.InvariantCulture);

        Assert.True(Number(window, "MinWidth") >= panes + outerMargin + WindowChrome,
            $"창 최소 너비 {Number(window, "MinWidth")} < 미리보기 세 칸 {panes + outerMargin + WindowChrome}");
        Assert.True(Number(window, "MinHeight") >= 640);
    }

    [Fact]
    public void RunView_ShowsCancelling_WhileCancelling()
    {
        var text = Load("Views", "RunView.xaml").Descendants(Wpf + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "취소하는 중…");

        Assert.Contains("IsCancelling", (string?)text.Attribute("Visibility"), StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewView_DetailPanel_IsHidden_WhenDetailIsNull()
    {
        var preview = Load("Views", "PreviewView.xaml");
        var panel = preview.Descendants(Wpf + "StackPanel").Single(p => (string?)p.Attribute("DataContext") == "{Binding Detail}");
        var host = panel.Parent!;

        var trigger = host.Descendants(Wpf + "DataTrigger").Single();
        Assert.Equal("{Binding Detail}", (string?)trigger.Attribute("Binding"));
        Assert.Equal("{x:Null}", (string?)trigger.Attribute("Value"));
        Assert.Equal("Collapsed", (string?)trigger.Element(Wpf + "Setter")!.Attribute("Value"));
    }

    [Fact]
    public void HistoryView_Row_UsesCountsConverter()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PhotoOk", "Views", "HistoryView.xaml"));

        Assert.Contains("""Text="{Binding Converter={StaticResource CountsText}}""", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceView_SourceList_TrimsLongPathsInsteadOfScrolling()
    {
        var list = Load("Views", "SourceView.xaml").Descendants(Wpf + "ListBox")
            .Single(l => (string?)l.Attribute("ItemsSource") == "{Binding Sources}");

        // 가로 스크롤을 허용하면 긴 경로가 줄을 넓혀 "빼기" 버튼이 화면 밖으로 밀린다.
        Assert.Equal("Disabled", (string?)list.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("Stretch", (string?)list.Attribute("HorizontalContentAlignment"));
    }

    [Fact]
    public void HistoryView_List_IsNotCenteredByMaxWidth()
    {
        var list = Load("Views", "HistoryView.xaml").Descendants(Wpf + "ListBox").Single();
        var host = list.Parent!;

        // 폭 제한(MaxWidth)과 늘이기(Stretch)를 함께 쓰면 넓은 창에서 목록이 가운데로 간다. 제목처럼 왼쪽에 붙어야 한다.
        Assert.Null(host.Attribute("MaxWidth"));
    }

    [Fact]
    public void HistoryView_RunsNotice_IsOnItsOwnLineAboveTheList()
    {
        var notice = Load("Views", "HistoryView.xaml").Descendants(Wpf + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding RunsNotice}");
        var host = notice.Parent!;

        // 같은 Grid 칸에 겹치면 뒤에 그려지는 목록이 안내를 가린다. 목록 위에 따로 쌓여야 한다.
        Assert.Equal(Wpf + "DockPanel", host.Name);
        Assert.Equal("Top", (string?)notice.Attribute("DockPanel.Dock"));
        var children = host.Elements().ToList();
        Assert.True(children.IndexOf(notice) < children.FindIndex(e => e.Name == Wpf + "ListBox"));
    }

    [Fact]
    public void PreviewView_ShowsIgnoredFiles()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PhotoOk", "Views", "PreviewView.xaml"));

        Assert.Contains("{Binding IgnoredNotice}", xaml, StringComparison.Ordinal);
        Assert.Contains("{Binding IgnoredFiles}", xaml, StringComparison.Ordinal);
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
