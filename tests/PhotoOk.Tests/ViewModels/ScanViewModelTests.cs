using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Hashing;
using PhotoOk.Core.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class ScanViewModelTests
{
    private const string Archive = @"D:\Archive";
    private const string LogFolder = @"C:\Users\test\AppData\Local\PhotoOk\logs";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly DateTime Oct14 = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Oct15 = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Unspecified);

    private readonly FakeFileSystem _fs = new();
    private readonly FakeMetadataReader _reader = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly ShellViewModel _shell;
    private readonly ObservableObject _previewScreen = new ScreenStub();
    private readonly ObservableObject _sourceScreen = new ScreenStub();
    private readonly List<ScanOutcome> _previewRequests = [];

    public ScanViewModelTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(Archive);
    }

    private sealed class ScreenStub : ObservableObject;

    /// <summary>첫 파일을 읽을 때 알리고, 풀어 줄 때까지 기다린다(스캔 도중 취소를 흉내 낸다).</summary>
    private sealed class BlockingReader(IMetadataReader inner) : IMetadataReader
    {
        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public PhotoInfo Read(FileEntry file)
        {
            Entered.Set();
            Release.Wait(Wait);
            return inner.Read(file);
        }
    }

    private static ImportSettings Settings() =>
        new([@"E:\DCIM"], true, Archive, BuiltInPresets.DateEvent, TransferMode.Copy);

    private ScanViewModel Create(IMetadataReader? reader = null)
    {
        var workflow = new ImportWorkflow(_fs, reader ?? _reader, new XxHasher(_fs), TimeZoneInfo.Utc);
        var vm = new ScanViewModel(_shell, workflow, Settings(), _dialogs, new FileLog(_fs, LogFolder),
            createPreview: scan =>
            {
                _previewRequests.Add(scan);
                return _previewScreen;
            },
            createSource: () => _sourceScreen);
        _shell.Navigate(vm);
        return vm;
    }

    private void AddPhoto(string path, byte[] content, DateTime capture)
    {
        _fs.AddFile(path, content);
        _reader.Set(path, capture);
    }

    [Fact]
    public async Task Scan_NavigatesToPreview_WithSessions()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        AddPhoto(@"E:\DCIM\B.JPG", [2], Oct14);
        AddPhoto(@"E:\DCIM\C.JPG", [3], Oct15);
        var vm = Create();
        var busyEnded = 0;
        vm.BusyEnded += (_, _) =>
        {
            Assert.False(vm.IsBusy);
            busyEnded++;
        };

        await vm.StartAsync();

        Assert.Same(_previewScreen, _shell.Current);
        var scan = Assert.Single(_previewRequests);
        Assert.Equal(["2026-10-14", "2026-10-15"], scan.Sessions.Select(s => s.Id));
        Assert.Equal(3, scan.FileCount);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Scan_Cancel_ReturnsToSource()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        AddPhoto(@"E:\DCIM\B.JPG", [2], Oct14);
        var reader = new BlockingReader(_reader);
        var vm = Create(reader);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        var running = vm.StartAsync();
        Assert.True(reader.Entered.Wait(Wait));
        Assert.True(vm.IsBusy);
        vm.CancelCommand.Execute(null);
        reader.Release.Set();
        await running;

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Empty(_previewRequests);
        Assert.False(vm.IsBusy);
        Assert.Equal(1, busyEnded);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Scan_CancelFromWindowClose_DoesNotThrow_AndEndsBusy()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        var reader = new BlockingReader(_reader);
        var vm = Create(reader);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        var running = vm.StartAsync();
        Assert.True(reader.Entered.Wait(Wait));
        vm.Cancel();
        vm.Cancel();
        reader.Release.Set();
        await running;
        vm.Cancel(); // 끝난 뒤에 불려도 예외가 없어야 한다

        Assert.Same(_sourceScreen, _shell.Current);
        Assert.Equal(1, busyEnded);
    }

    [Fact]
    public async Task Scan_UnexpectedFailure_ReturnsToSource_WithKoreanError()
    {
        AddPhoto(@"E:\DCIM\A.JPG", [1], Oct14);
        var workflow = new ImportWorkflow(_fs, _reader, new XxHasher(_fs), TimeZoneInfo.Utc);
        var vm = new ScanViewModel(_shell, workflow, Settings(), _dialogs, new FileLog(_fs, LogFolder),
            createPreview: _ => throw new InvalidOperationException("boom"),
            createSource: () => _sourceScreen);
        _shell.Navigate(vm);
        var busyEnded = 0;
        vm.BusyEnded += (_, _) => busyEnded++;

        await vm.StartAsync();

        Assert.Same(_sourceScreen, _shell.Current);
        var error = Assert.Single(_dialogs.Errors);
        Assert.Equal("스캔하지 못했습니다", error.Title);
        Assert.DoesNotContain("boom", error.Message, StringComparison.Ordinal);
        Assert.Contains(_fs.AllFiles(), p => p.StartsWith(LogFolder, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, busyEnded);
    }

    [Fact]
    public void FilesReadLabel_SaysFilesRead()
    {
        var vm = Create();

        vm.FilesRead = 7;

        Assert.Equal("읽은 파일 7개", vm.FilesReadText);
    }
}
