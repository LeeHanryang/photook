using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Settings;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;
using PhotoOk.Tests.Fakes;
using PhotoOk.ViewModels;

namespace PhotoOk.Tests.ViewModels;

public class SourceViewModelTests
{
    private const string SettingsPath = @"C:\Users\test\AppData\Roaming\PhotoOk\settings.jsonl";
    private const string PresetFolder = @"C:\Users\test\AppData\Roaming\PhotoOk\presets";
    private const string NoSource = "가져올 폴더를 하나 이상 고르세요";
    private const string NoArchive = "보관 폴더를 고르세요";
    private const string Overlap = "보관 폴더와 가져올 폴더가 겹칩니다. 서로 다른 폴더를 고르세요";
    private const string NoPreset = "정리 방식을 고르세요";
    private const string MoveNoticeText = "다른 드라이브에서는 이동을 골라도 복사합니다";
    private const string PresetErrorTitle = "프리셋을 열 수 없습니다";
    private const string LogFolder = @"L:\PhotoOk\logs";

    private readonly FakeFileSystem _fs = new();
    private readonly FakeFolderPicker _folders = new();
    private readonly FakeFileOpenPicker _files = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly ShellViewModel _shell;
    private readonly ObservableObject _scanScreen = new ScreenStub();
    private readonly ObservableObject _startScreen = new ScreenStub();
    private readonly List<ImportSettings> _scanRequests = [];

    public SourceViewModelTests()
    {
        _shell = new ShellViewModel(_dialogs);
        _fs.AddDirectory(@"E:\");
        _fs.AddDirectory(@"E:\DCIM");
        _fs.AddDirectory(@"D:\Photos");
        _fs.AddDirectory(@"D:\Photos2");
        _fs.AddDirectory(@"D:\Archive");
    }

    private sealed class ScreenStub : ObservableObject;

    private static AppSettings Settings(string[]? sources = null, string? archive = null, string? presetName = "날짜 + 이벤트",
        string? presetFile = null, TransferMode mode = TransferMode.Copy, bool includeSubfolders = true) =>
        new(sources ?? [], includeSubfolders, archive, presetName, presetFile, mode);

    /// <summary>보관 폴더 확인은 기본으로 그 자리에서 끝낸다(결과가 바로 보인다). 백그라운드 동작은 따로 시험한다.</summary>
    private SourceViewModel Create(AppSettings? settings = null, string? driveRoot = null,
        PhotoOk.Core.IO.IFileSystem? fileSystem = null, Func<Func<bool>, Task<bool>>? runCheck = null) =>
        new(_shell, fileSystem ?? _fs, new PresetLibrary(_fs, PresetFolder), SettingsPath, settings ?? AppSettings.Default, driveRoot,
            _folders, _files, _dialogs, new FileLog(_fs, LogFolder),
            createScan: s =>
            {
                _scanRequests.Add(s);
                return _scanScreen;
            },
            createStart: () => _startScreen,
            runCheck: runCheck ?? (check => Task.FromResult(check())));

    private string AddUserPreset(string fileName, string presetName)
    {
        var path = Path.Combine(PresetFolder, fileName);
        var json = PresetSerializer.Serialize(BuiltInPresets.Monthly with { Name = presetName });
        _fs.AddFile(path, Encoding.UTF8.GetBytes(json));
        return path;
    }

    // ----- 초기 상태 -----

    [Fact]
    public void Initial_UsesSavedSettings()
    {
        var vm = Create(Settings([@"E:\DCIM", @"D:\Photos"], @"D:\Archive", "월 단위", mode: TransferMode.Move, includeSubfolders: false));

        Assert.Equal([@"E:\DCIM", @"D:\Photos"], vm.Sources);
        Assert.Equal(@"D:\Archive", vm.ArchiveRoot);
        Assert.False(vm.IncludeSubfolders);
        Assert.Equal(TransferMode.Move, vm.Mode);
        Assert.Equal("월 단위", vm.SelectedPreset!.Name);
        Assert.Equal(4, vm.Presets.Count);
    }

    [Fact]
    public void Initial_WithDriveRoot_ReplacesSavedSources()
    {
        var vm = Create(Settings([@"D:\Photos"], @"D:\Archive"), driveRoot: @"E:\");

        Assert.Equal([@"E:\"], vm.Sources);
        Assert.Equal(@"D:\Archive", vm.ArchiveRoot);
    }

    [Fact]
    public void Initial_Defaults_SelectFirstBuiltIn()
    {
        var vm = Create();

        Assert.Empty(vm.Sources);
        Assert.Null(vm.ArchiveRoot);
        Assert.Equal("날짜 + 이벤트", vm.SelectedPreset!.Name);
        Assert.Null(vm.SelectedPreset.FilePath);
    }

    [Fact]
    public void Initial_UnknownPresetName_FallsBackToFirstBuiltIn()
    {
        var vm = Create(Settings(presetName: "없는 방식"));

        Assert.Equal("날짜 + 이벤트", vm.SelectedPreset!.Name);
    }

    [Fact]
    public void Initial_UserPreset_IsSelectedByFile_EvenWhenNamesRepeat()
    {
        AddUserPreset("a.json", "같은 이름");
        var second = AddUserPreset("b.json", "같은 이름");

        var vm = Create(Settings(presetName: "같은 이름", presetFile: second));

        Assert.Equal(second, vm.SelectedPreset!.FilePath);
        Assert.Equal(6, vm.Presets.Count);
    }

    [Fact]
    public void Initial_PresetFileOutsideList_IsLoadedAndAdded()
    {
        const string outside = @"D:\Elsewhere\내 방식.json";
        _fs.AddFile(outside, Encoding.UTF8.GetBytes(PresetSerializer.Serialize(BuiltInPresets.DateOnly with { Name = "내 방식" })));

        var vm = Create(Settings(presetName: "내 방식", presetFile: outside));

        Assert.Equal(outside, vm.SelectedPreset!.FilePath);
        Assert.Equal(5, vm.Presets.Count);
        Assert.Contains(vm.Presets, p => p.FilePath == outside);
    }

    [Fact]
    public void Initial_MissingPresetFile_FallsBackToBuiltInByName()
    {
        var vm = Create(Settings(presetName: "월 단위", presetFile: @"D:\Gone\없음.json"));

        Assert.Equal("월 단위", vm.SelectedPreset!.Name);
        Assert.Null(vm.SelectedPreset.FilePath);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void Initial_BuiltInName_IsNotMatchedToUserPresetWithSameName()
    {
        AddUserPreset("mine.json", "월 단위");

        var vm = Create(Settings(presetName: "월 단위"));

        Assert.Null(vm.SelectedPreset!.FilePath);
    }

    // ----- 검증 -----

    [Fact]
    public void Validation_ReportsFirstProblem_InOrder()
    {
        var vm = Create();
        Assert.Equal(NoSource, vm.ValidationMessage);

        vm.Sources.Add(@"E:\DCIM");
        Assert.Equal(NoArchive, vm.ValidationMessage);

        vm.ArchiveRoot = @"D:\Archive";
        Assert.Null(vm.ValidationMessage);

        vm.Sources.Add(@"X:\없는폴더");
        Assert.Equal(@"폴더를 찾을 수 없습니다: X:\없는폴더", vm.ValidationMessage);

        vm.Sources.RemoveAt(1);
        vm.SelectedPreset = null;
        Assert.Equal(NoPreset, vm.ValidationMessage);
    }

    [Fact]
    public void Validation_SourceNotFound_BeatsOverlap()
    {
        var vm = Create(Settings([@"D:\Photos\없음"], @"D:\Photos"));

        Assert.Equal(@"폴더를 찾을 수 없습니다: D:\Photos\없음", vm.ValidationMessage);
    }

    [Fact]
    public void Validation_RelativeSource_IsNotFound()
    {
        var vm = Create(Settings([@"DCIM"], @"D:\Archive"));

        Assert.Equal("폴더를 찾을 수 없습니다: DCIM", vm.ValidationMessage);
    }

    [Theory]
    [InlineData(@"E:\", @"E:\Photos")]
    [InlineData(@"D:\Photos", @"D:\Photos\Sub")]
    [InlineData(@"D:\Photos\Sub", @"D:\Photos")]
    [InlineData(@"D:\Photos", @"D:\Photos")]
    [InlineData(@"D:\Photos", @"d:\photos\")]
    [InlineData(@"D:\PHOTOS\", @"D:\photos\Sub")]
    public void Validation_Overlap_IsDetected(string source, string archive)
    {
        _fs.AddDirectory(source);
        var vm = Create(Settings([source], archive));

        Assert.Equal(Overlap, vm.ValidationMessage);
    }

    [Theory]
    [InlineData(@"D:\Photos2", @"D:\Photos")]
    [InlineData(@"D:\Photos", @"D:\Photos2\Sub")]
    [InlineData(@"E:\DCIM", @"D:\Archive")]
    public void Validation_SiblingWithSamePrefix_IsNotOverlap(string source, string archive)
    {
        _fs.AddDirectory(archive);
        var vm = Create(Settings([source], archive));

        Assert.Null(vm.ValidationMessage);
    }

    [Theory]
    [InlineData(@"Archive")]          // 손으로 고친 상대 경로
    [InlineData(@"D:\없는 보관 폴더")]  // 사라진 폴더
    public void Validation_ArchiveMustBeFullyQualifiedAndExist(string archive)
    {
        var vm = Create(Settings([@"E:\DCIM"], archive));

        Assert.Equal("보관 폴더를 찾을 수 없습니다: " + archive, vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Validation_ArchiveNotFound_ComesAfterChooseArchive()
    {
        var vm = Create(Settings([@"E:\DCIM"], archive: null));
        Assert.Equal(NoArchive, vm.ValidationMessage);

        vm.ArchiveRoot = @"D:\없음";
        Assert.Equal(@"보관 폴더를 찾을 수 없습니다: D:\없음", vm.ValidationMessage);
    }

    [Fact]
    public void ArchiveExistence_IsCheckedOncePerPathChange()
    {
        var hooked = new HookedFileSystem(_fs);
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"), fileSystem: hooked);

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(vm.ValidationMessage);
            Assert.True(vm.NextCommand.CanExecute(null));
        }

        Assert.Single(hooked.DirectoryChecks, p => p == @"D:\Archive");

        vm.ArchiveRoot = @"D:\Photos2";
        _ = vm.ValidationMessage;
        _ = vm.NextCommand.CanExecute(null);

        Assert.Single(hooked.DirectoryChecks, p => p == @"D:\Photos2");
    }

    [Fact]
    public async Task ArchiveExistence_IsCheckedInBackground_AndValidationUpdatesWhenDone()
    {
        using var gate = new ManualResetEventSlim();
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"), runCheck: check => Task.Run(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return check();
        }));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("보관 폴더를 확인하는 중…", vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));

        gate.Set();
        await vm.WhenArchiveChecked;

        Assert.Contains(nameof(SourceViewModel.ValidationMessage), raised);
        Assert.Null(vm.ValidationMessage);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ArchiveExistence_OnlyLatestCheckCounts()
    {
        using var firstGate = new ManualResetEventSlim();
        var calls = 0;
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"), runCheck: check =>
            Interlocked.Increment(ref calls) == 1
                ? Task.Run(() =>
                {
                    firstGate.Wait(TimeSpan.FromSeconds(10));
                    return check();
                })
                : Task.FromResult(check()));
        var first = vm.WhenArchiveChecked;

        vm.ArchiveRoot = @"D:\없음";
        firstGate.Set();
        await first;

        Assert.Equal(@"보관 폴더를 찾을 수 없습니다: D:\없음", vm.ValidationMessage);
    }

    [Fact]
    public void Next_RechecksArchive_WhenItVanishedAfterCheck()
    {
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"));
        Assert.True(vm.NextCommand.CanExecute(null));

        _fs.RemoveVolume(@"D:\"); // 보관 드라이브가 빠졌다(속성 변경 알림 없음)
        vm.NextCommand.Execute(null);

        Assert.Empty(_scanRequests);
        Assert.Equal(@"보관 폴더를 찾을 수 없습니다: D:\Archive", vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Next_Blocked_ReRaisesValidation_WhenFolderVanished()
    {
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"));
        Assert.True(vm.NextCommand.CanExecute(null));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var canExecuteChanges = 0;
        vm.NextCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        _fs.RemoveVolume(@"E:\"); // 카드가 빠졌다(속성 변경 알림 없음)
        vm.NextCommand.Execute(null);

        Assert.Empty(_scanRequests);
        Assert.Contains(nameof(SourceViewModel.ValidationMessage), raised);
        Assert.True(canExecuteChanges > 0);
        Assert.Equal(@"폴더를 찾을 수 없습니다: E:\DCIM", vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Next_IsBlocked_WhenArchiveOverlapsSource()
    {
        var vm = Create(Settings([@"E:\"], @"E:\Photos"));

        Assert.Equal(Overlap, vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.NextCommand.Execute(null);

        Assert.Empty(_scanRequests);
        Assert.False(_fs.FileExists(SettingsPath));
        Assert.NotSame(_scanScreen, _shell.Current);
    }

    [Fact]
    public void NextCommand_CanExecute_TracksInputs()
    {
        var vm = Create();
        var changes = 0;
        vm.NextCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.False(vm.NextCommand.CanExecute(null));

        vm.Sources.Add(@"E:\DCIM");
        vm.ArchiveRoot = @"D:\Archive";

        Assert.True(vm.NextCommand.CanExecute(null));
        Assert.True(changes >= 2);

        var before = changes;
        vm.SelectedPreset = null;
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.True(changes > before);

        vm.SelectedPreset = vm.Presets[0];
        vm.Sources.Clear();
        Assert.False(vm.NextCommand.CanExecute(null));
    }

    // ----- 다음 -----

    [Fact]
    public void Next_SavesSettings_AndNavigatesToScan()
    {
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive", "월 단위", mode: TransferMode.Move, includeSubfolders: false));

        vm.NextCommand.Execute(null);

        var lines = Encoding.UTF8.GetString(_fs.ReadAllBytes(SettingsPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var last = lines[^1];
        Assert.Contains("E:\\\\DCIM", last);
        Assert.Contains("D:\\\\Archive", last);
        Assert.Contains("월 단위", last);

        var saved = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal([@"E:\DCIM"], saved.Sources);
        Assert.False(saved.IncludeSubfolders);
        Assert.Equal(@"D:\Archive", saved.ArchiveRoot);
        Assert.Equal("월 단위", saved.PresetName);
        Assert.Null(saved.PresetFile);
        Assert.Equal(TransferMode.Move, saved.Mode);

        var request = Assert.Single(_scanRequests);
        Assert.Equal([@"E:\DCIM"], request.Sources);
        Assert.False(request.IncludeSubfolders);
        Assert.Equal(@"D:\Archive", request.ArchiveRoot);
        Assert.Equal(BuiltInPresets.Monthly, request.Preset);
        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.Same(_scanScreen, _shell.Current);
    }

    [Fact]
    public void Next_RemembersUserPresetByFile()
    {
        var path = AddUserPreset("mine.json", "내 방식");
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive", "내 방식", presetFile: path));

        vm.NextCommand.Execute(null);

        var saved = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal("내 방식", saved.PresetName);
        Assert.Equal(path, saved.PresetFile);
    }

    [Fact]
    public void Next_AppendsSettingsLine_WithoutOverwritingEarlierOnes()
    {
        AppSettingsStore.Save(_fs, SettingsPath, Settings([@"E:\DCIM"], @"D:\Old"));
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"));

        vm.NextCommand.Execute(null);

        var lines = Encoding.UTF8.GetString(_fs.ReadAllBytes(SettingsPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("D:\\\\Old", lines[0]);
    }

    [Fact]
    public void Next_StillGoesToScan_WhenSettingsCannotBeSaved()
    {
        _fs.AddDirectory(SettingsPath);
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"));

        vm.NextCommand.Execute(null);

        Assert.Single(_scanRequests);
        Assert.Same(_scanScreen, _shell.Current);
        var log = string.Concat(_fs.AllFiles()
            .Where(p => p.StartsWith(LogFolder, StringComparison.OrdinalIgnoreCase))
            .Select(p => Encoding.UTF8.GetString(_fs.ReadAllBytes(p))));
        Assert.Contains("마지막 선택을 저장하지 못했습니다", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Back_NavigatesToStart()
    {
        var vm = Create();

        vm.BackCommand.Execute(null);

        Assert.Same(_startScreen, _shell.Current);
    }

    // ----- 이동 안내 -----

    [Fact]
    public void MoveNotice_AppearsForCrossDriveMove()
    {
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive", mode: TransferMode.Move));
        Assert.Equal(MoveNoticeText, vm.MoveNotice);

        vm.Mode = TransferMode.Copy;
        Assert.Null(vm.MoveNotice);

        vm.Mode = TransferMode.Move;
        Assert.Equal(MoveNoticeText, vm.MoveNotice);

        vm.ArchiveRoot = @"e:\Archive";
        Assert.Null(vm.MoveNotice);
    }

    [Fact]
    public void MoveNotice_AppearsWhenAnySourceIsOnAnotherDrive()
    {
        var vm = Create(Settings([@"D:\Photos", @"E:\DCIM"], @"D:\Archive", mode: TransferMode.Move));

        Assert.Equal(MoveNoticeText, vm.MoveNotice);

        vm.Sources.RemoveAt(1);
        Assert.Null(vm.MoveNotice);
    }

    [Fact]
    public void MoveNotice_IsRaisedAsPropertyChange()
    {
        var vm = Create(Settings([@"E:\DCIM"], @"D:\Archive"));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Mode = TransferMode.Move;
        Assert.Contains(nameof(SourceViewModel.MoveNotice), changed);

        changed.Clear();
        vm.Sources.Add(@"D:\Photos");
        Assert.Contains(nameof(SourceViewModel.MoveNotice), changed);
        Assert.Contains(nameof(SourceViewModel.ValidationMessage), changed);
    }

    // ----- 폴더 고르기 -----

    [Fact]
    public void AddSource_AddsPickedFolder_IgnoringCancelAndDuplicates()
    {
        var vm = Create();
        _folders.Answers.Enqueue(@"E:\DCIM");
        _folders.Answers.Enqueue(null);
        _folders.Answers.Enqueue(@"e:\dcim\");
        _folders.Answers.Enqueue(@"D:\Photos");

        vm.AddSourceCommand.Execute(null);
        vm.AddSourceCommand.Execute(null);
        vm.AddSourceCommand.Execute(null);
        vm.AddSourceCommand.Execute(null);

        Assert.Equal([@"E:\DCIM", @"D:\Photos"], vm.Sources);
        Assert.Equal(4, _folders.Calls.Count);
    }

    [Fact]
    public void RemoveSource_RemovesIt()
    {
        var vm = Create(Settings([@"E:\DCIM", @"D:\Photos"]));

        vm.RemoveSourceCommand.Execute(@"E:\DCIM");

        Assert.Equal([@"D:\Photos"], vm.Sources);
    }

    [Fact]
    public void PickArchive_SetsArchive_AndKeepsOldOnCancel()
    {
        var vm = Create(Settings(archive: @"D:\Archive"));
        _folders.Answers.Enqueue(null);
        vm.PickArchiveCommand.Execute(null);
        Assert.Equal(@"D:\Archive", vm.ArchiveRoot);
        Assert.Equal(@"D:\Archive", _folders.Calls[0].InitialFolder);

        _folders.Answers.Enqueue(@"D:\Photos2");
        vm.PickArchiveCommand.Execute(null);
        Assert.Equal(@"D:\Photos2", vm.ArchiveRoot);
    }

    // ----- 프리셋 파일 -----

    [Fact]
    public void OpenPresetFile_Success_AddsAndSelects()
    {
        const string path = @"D:\Elsewhere\새 방식.json";
        _fs.AddFile(path, Encoding.UTF8.GetBytes(PresetSerializer.Serialize(BuiltInPresets.DateOnly with { Name = "새 방식" })));
        var vm = Create();
        _files.Answers.Enqueue(path);

        vm.OpenPresetFileCommand.Execute(null);

        Assert.Equal("새 방식", vm.SelectedPreset!.Name);
        Assert.Equal(path, vm.SelectedPreset.FilePath);
        Assert.Equal(5, vm.Presets.Count);
        Assert.Empty(_dialogs.Errors);

        // 같은 파일을 다시 열면 목록에 또 넣지 않고 그 항목을 고른다.
        vm.SelectedPreset = vm.Presets[0];
        _files.Answers.Enqueue(path);
        vm.OpenPresetFileCommand.Execute(null);
        Assert.Equal(5, vm.Presets.Count);
        Assert.Equal(path, vm.SelectedPreset!.FilePath);
    }

    [Fact]
    public void OpenPresetFile_Cancel_ChangesNothing()
    {
        var vm = Create();
        var selected = vm.SelectedPreset;

        vm.OpenPresetFileCommand.Execute(null);

        Assert.Single(_files.Calls);
        Assert.Same(selected, vm.SelectedPreset);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public void OpenPresetFile_Error_IsShown()
    {
        const string path = @"D:\Elsewhere\깨진.json";
        _fs.AddFile(path, Encoding.UTF8.GetBytes("{ 깨진"));
        var vm = Create();
        var selected = vm.SelectedPreset;
        _files.Answers.Enqueue(path);

        vm.OpenPresetFileCommand.Execute(null);

        var error = Assert.Single(_dialogs.Errors);
        Assert.Equal(PresetErrorTitle, error.Title);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Same(selected, vm.SelectedPreset);
        Assert.Equal(4, vm.Presets.Count);
    }
}
