using System.ComponentModel;
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

/// <summary>소스 화면이 가져오기·흡수·재정리에 맞게 바뀌는 부분. 가져오기 자체의 시험은 <see cref="SourceViewModelTests"/>.</summary>
public class SourceViewModelJobKindTests
{
    private const string SettingsPath = @"C:\Users\test\AppData\Roaming\PhotoOk\settings.jsonl";
    private const string PresetFolder = @"C:\Users\test\AppData\Roaming\PhotoOk\presets";
    private const string LogFolder = @"L:\PhotoOk\logs";
    private const string Archive = @"D:\Archive";
    private const string InsideArchiveForAbsorb = "아카이브 안의 폴더는 재정리를 쓰세요";
    private const string NoFolderToReorganize = "재정리할 폴더를 하나 이상 고르세요";
    private const string NotInArchive = "보관 폴더 안의 폴더를 고르세요";
    private const string FoldersOverlap = "서로 겹치는 폴더는 함께 고를 수 없습니다";
    private const string ImportOverlap = "보관 폴더와 가져올 폴더가 겹칩니다. 서로 다른 폴더를 고르세요";
    private const string WholeArchive = "아카이브 전체가 대상입니다. 손으로 모은 폴더도 옮겨질 수 있습니다";
    private const string EventsNotUsed = "이 정리 방식은 이벤트 이름을 쓰지 않습니다";

    private readonly FakeFileSystem _fs = new();
    private readonly FakeFolderPicker _folders = new();
    private readonly FakeFileOpenPicker _files = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly ShellViewModel _shell;
    private readonly ObservableObject _scanScreen = new ScreenStub();
    private readonly List<ImportSettings> _scanRequests = [];

    public SourceViewModelJobKindTests()
    {
        _shell = new ShellViewModel(_dialogs);
        foreach (var directory in new[]
        {
            @"E:\DCIM", @"D:\Photos", @"D:\Archive", @"D:\Archive2", @"D:\Archive\2024", @"D:\Archive\2024\a",
            @"D:\Archive\2025", @"D:\Archive\.photook\x",
        })
        {
            _fs.AddDirectory(directory);
        }
    }

    private sealed class ScreenStub : ObservableObject;

    private static AppSettings Settings(string[]? sources = null, string? archive = Archive, string presetName = "날짜 + 이벤트",
        TransferMode mode = TransferMode.Copy, bool includeSubfolders = true) =>
        new(sources ?? [], includeSubfolders, archive, presetName, null, mode);

    private SourceViewModel Create(JobKind kind, AppSettings? settings = null, string? driveRoot = null,
        ImportSettings? previous = null) =>
        new(_shell, _fs, new PresetLibrary(_fs, PresetFolder), SettingsPath, settings ?? Settings(), kind, driveRoot,
            _folders, _files, _dialogs, new FileLog(_fs, LogFolder),
            createScan: s =>
            {
                _scanRequests.Add(s);
                return _scanScreen;
            },
            createStart: () => new ScreenStub(),
            runCheck: check => Task.FromResult(check()), previous: previous);

    private static void AddSources(SourceViewModel vm, params string[] sources)
    {
        foreach (var source in sources)
        {
            vm.Sources.Add(source);
        }
    }

    // ----- 화면 문구와 보이는 항목 -----

    [Theory]
    [InlineData(JobKind.Import, "무엇을 어디로 가져올까요?")]
    [InlineData(JobKind.Absorb, "어떤 폴더를 합칠까요?")]
    [InlineData(JobKind.Reorganize, "어떤 폴더를 재정리할까요?")]
    public void Title_FollowsKind(JobKind kind, string title)
    {
        Assert.Equal(title, Create(kind).Title);
    }

    [Theory]
    [InlineData(JobKind.Import, true, false)]
    [InlineData(JobKind.Absorb, true, true)]
    [InlineData(JobKind.Reorganize, false, true)]
    public void ShownItems_FollowKind(JobKind kind, bool showModeChoice, bool showUseFolderNames)
    {
        var vm = Create(kind);

        Assert.Equal(kind, vm.Kind);
        Assert.Equal(showModeChoice, vm.ShowModeChoice);
        Assert.Equal(showUseFolderNames, vm.ShowUseFolderNames);
    }

    [Theory]
    [InlineData(JobKind.Import, "가져올 폴더", "가져올 폴더 선택")]
    [InlineData(JobKind.Absorb, "합칠 폴더", "합칠 폴더 선택")]
    [InlineData(JobKind.Reorganize, "재정리할 폴더", "재정리할 폴더 선택")]
    public void SourcesTitle_AndPickerTitle_FollowKind(JobKind kind, string heading, string pickerTitle)
    {
        var vm = Create(kind);
        _folders.Answers.Enqueue(null);

        vm.AddSourceCommand.Execute(null);

        Assert.Equal(heading, vm.SourcesTitle);
        Assert.Equal(pickerTitle, vm.PickSourceTitle);
        Assert.Equal(pickerTitle, _folders.Calls[0].Title);
    }

    // ----- 뒤로 왔을 때 이번에 고른 값 되살리기 -----

    private static ImportSettings Sent(JobKind kind, string[] sources, TransferMode mode, bool useFolderNames,
        bool includeSubfolders = true) =>
        new(sources, includeSubfolders, Archive, BuiltInPresets.DateEvent, mode, kind, useFolderNames);

    [Fact]
    public void Back_Absorb_RestoresSourcesModeAndUseFolderNames_WithoutSaving()
    {
        var previous = Sent(JobKind.Absorb, [@"D:\Photos", @"D:\Archive2"], TransferMode.Copy, useFolderNames: false,
            includeSubfolders: false);

        var vm = Create(JobKind.Absorb, Settings([@"E:\DCIM"]), previous: previous);

        Assert.Equal([@"D:\Photos", @"D:\Archive2"], vm.Sources);
        Assert.Equal(TransferMode.Copy, vm.Mode);
        Assert.False(vm.UseFolderNames);
        Assert.False(vm.IncludeSubfolders);
        Assert.False(_fs.FileExists(SettingsPath));
    }

    [Fact]
    public void Back_Reorganize_RestoresSources_ModeStaysMove()
    {
        var previous = Sent(JobKind.Reorganize, [@"D:\Archive\2024", @"D:\Archive\2025"], TransferMode.Move, useFolderNames: false);

        var vm = Create(JobKind.Reorganize, Settings([@"E:\DCIM"], mode: TransferMode.Copy, includeSubfolders: false),
            previous: previous);

        Assert.Equal([@"D:\Archive\2024", @"D:\Archive\2025"], vm.Sources);
        Assert.Equal(TransferMode.Move, vm.Mode);
        Assert.True(vm.IncludeSubfolders);
        Assert.False(vm.UseFolderNames);
        Assert.Null(vm.ValidationMessage);
    }

    [Fact]
    public void Back_Import_UsesSavedSettings_NotThePreviousRequest()
    {
        var previous = Sent(JobKind.Import, [@"D:\Photos"], TransferMode.Move, useFolderNames: false);

        var vm = Create(JobKind.Import, Settings([@"E:\DCIM"], mode: TransferMode.Copy), previous: previous);

        Assert.Equal([@"E:\DCIM"], vm.Sources);
        Assert.Equal(TransferMode.Copy, vm.Mode);
    }

    [Fact]
    public void Back_AfterNext_ThenNextAgain_SendsTheSameChoices()
    {
        var first = Create(JobKind.Absorb);
        first.Sources.Add(@"D:\Photos");
        first.Mode = TransferMode.Copy;
        first.UseFolderNames = false;
        first.NextCommand.Execute(null);
        var sent = Assert.Single(_scanRequests);

        var again = Create(JobKind.Absorb, AppSettingsStore.Load(_fs, SettingsPath), previous: sent);
        again.NextCommand.Execute(null);

        Assert.Equal(2, _scanRequests.Count);
        Assert.Equal(sent.Sources, _scanRequests[1].Sources);
        Assert.Equal(TransferMode.Copy, _scanRequests[1].Mode);
        Assert.False(_scanRequests[1].UseFolderNames);
    }

    // ----- 미리보기에서 바꾼 옵션을 뒤로 가져온다 -----

    [Theory]
    [InlineData(JobKind.Import)]
    [InlineData(JobKind.Absorb)]
    [InlineData(JobKind.Reorganize)]
    public async Task Back_RestoresPresetAndPreviewOptions_WithoutSavingThem(JobKind kind)
    {
        var source = kind == JobKind.Reorganize ? @"D:\Archive\2024" : @"D:\Photos";
        var previous = new ImportSettings([source], true, Archive, BuiltInPresets.Monthly, TransferMode.Move, kind,
            UseFolderNames: true, SplitUnderscores: true, Subfolders: SubfolderPolicy.Merge, KeepReviewInPlace: false);

        var vm = Create(kind, Settings([source]), previous: previous);
        await vm.WhenArchiveChecked;

        Assert.Equal("월 단위", vm.SelectedPreset?.Name);
        Assert.False(_fs.FileExists(SettingsPath));

        vm.NextCommand.Execute(null);

        var sent = Assert.Single(_scanRequests);
        Assert.Same(BuiltInPresets.Monthly, sent.Preset);
        Assert.True(sent.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Merge, sent.Subfolders);
        Assert.False(sent.KeepReviewInPlace);

        // 옵션은 실행할 때 저장한다. 소스 화면의 "다음"은 정리 방식만 새 값으로 저장한다.
        var saved = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal("월 단위", saved.PresetName);
        Assert.False(saved.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Keep, saved.Subfolders);
        Assert.True(saved.KeepReviewInPlace);
    }

    private void AddBrokenSettingsFile()
    {
        _fs.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        _fs.AppendLine(SettingsPath, "다른 버전이 쓴 줄");
    }

    [Theory]
    [InlineData(JobKind.Absorb, @"D:\Photos")]
    [InlineData(JobKind.Reorganize, @"D:\Archive\2024")]
    public async Task Next_UnparseableSettings_NonImport_WritesNothingButContinues(JobKind kind, string source)
    {
        AddBrokenSettingsFile();
        var before = _fs.ReadAllBytes(SettingsPath);
        var vm = Create(kind);
        await vm.WhenArchiveChecked;
        AddSources(vm, source);

        vm.NextCommand.Execute(null);

        // 기본값이 든 줄로 읽지 못한 줄을 가리지 않는다. 기억하지 못할 뿐 작업은 이어 간다.
        Assert.Equal(before, _fs.ReadAllBytes(SettingsPath));
        Assert.Single(_scanRequests);
        Assert.Same(_scanScreen, _shell.Current);
        Assert.Empty(_dialogs.Errors);
    }

    [Fact]
    public async Task Next_UnparseableSettings_Import_WritesItsOwnValues()
    {
        AddBrokenSettingsFile();
        var vm = Create(JobKind.Import, Settings([@"E:\DCIM"]));
        await vm.WhenArchiveChecked;

        vm.NextCommand.Execute(null);

        Assert.Equal([@"E:\DCIM"], AppSettingsStore.Load(_fs, SettingsPath).Sources);
    }

    [Fact]
    public void Back_RestoresUserPreset_ByFile()
    {
        var path = Path.Combine(PresetFolder, "plain.json");
        _fs.AddFile(path, Encoding.UTF8.GetBytes(PresetSerializer.Serialize(BuiltInPresets.DateOnly with { Name = "내 방식" })));
        var previous = new ImportSettings([@"D:\Photos"], true, Archive, BuiltInPresets.DateOnly with { Name = "내 방식" },
            TransferMode.Move, JobKind.Absorb, UseFolderNames: true, PresetFile: path);

        var vm = Create(JobKind.Absorb, Settings(), previous: previous);

        Assert.Equal(path, vm.SelectedPreset?.FilePath);
    }

    [Fact]
    public async Task Next_SendsPresetFile()
    {
        var path = Path.Combine(PresetFolder, "plain.json");
        _fs.AddFile(path, Encoding.UTF8.GetBytes(PresetSerializer.Serialize(BuiltInPresets.DateOnly with { Name = "내 방식" })));
        var vm = Create(JobKind.Absorb, Settings() with { PresetName = "내 방식", PresetFile = path });
        AddSources(vm, @"D:\Photos");
        await vm.WhenArchiveChecked;

        vm.NextCommand.Execute(null);

        Assert.Equal(path, Assert.Single(_scanRequests).PresetFile);
    }

    // ----- 가져오기는 그대로 -----

    [Fact]
    public void Import_Unchanged_KeepsSavedSourcesModeAndSubfolders()
    {
        var vm = Create(JobKind.Import, Settings([@"E:\DCIM"], mode: TransferMode.Copy, includeSubfolders: false));

        Assert.Equal([@"E:\DCIM"], vm.Sources);
        Assert.Equal(TransferMode.Copy, vm.Mode);
        Assert.False(vm.IncludeSubfolders);
        Assert.Null(vm.EventsNotUsedNotice);
        Assert.Null(vm.WholeArchiveNotice);
    }

    [Fact]
    public void Import_FolderInsideArchive_StillGivesTheImportOverlapMessage()
    {
        var vm = Create(JobKind.Import, Settings([@"D:\Archive\2024"]));

        Assert.Equal(ImportOverlap, vm.ValidationMessage);
    }

    // ----- 흡수 -----

    [Fact]
    public void Absorb_DefaultsToMove_ButShowsTheChoice()
    {
        var vm = Create(JobKind.Absorb, Settings(mode: TransferMode.Copy));

        Assert.Equal(TransferMode.Move, vm.Mode);
        Assert.True(vm.ShowModeChoice);

        vm.Mode = TransferMode.Copy;
        vm.Sources.Add(@"D:\Photos");
        vm.NextCommand.Execute(null);

        Assert.Equal(TransferMode.Copy, Assert.Single(_scanRequests).Mode);
    }

    [Fact]
    public void Absorb_SourcesNotPrefilled()
    {
        var vm = Create(JobKind.Absorb, Settings([@"E:\DCIM", @"D:\Photos"]));

        Assert.Empty(vm.Sources);
        Assert.Equal("가져올 폴더를 하나 이상 고르세요", vm.ValidationMessage);
    }

    [Theory]
    [InlineData(@"D:\Archive")]
    [InlineData(@"D:\Archive\2024")]
    [InlineData(@"d:\archive\2024\a\")]
    [InlineData(@"D:\Archive\.photook\x")]
    public void Absorb_RejectsFolderInsideArchive(string source)
    {
        var vm = Create(JobKind.Absorb);
        vm.Sources.Add(source);

        Assert.Equal(InsideArchiveForAbsorb, vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Assert.Empty(_scanRequests);
    }

    [Fact]
    public void Absorb_FolderOutsideArchive_IsAccepted_EvenWithSimilarName()
    {
        var vm = Create(JobKind.Absorb);
        AddSources(vm, @"D:\Photos", @"D:\Archive2");

        Assert.Null(vm.ValidationMessage);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Absorb_FolderContainingArchive_GivesTheOverlapMessage()
    {
        _fs.AddDirectory(@"D:\Photos\Archive");
        var vm = Create(JobKind.Absorb, Settings(archive: @"D:\Photos\Archive"));
        vm.Sources.Add(@"D:\Photos");

        Assert.Equal(ImportOverlap, vm.ValidationMessage);
    }

    // ----- 재정리 -----

    [Fact]
    public void Reorganize_NoDefaultFolders_AndPickerStartsAtArchive()
    {
        var vm = Create(JobKind.Reorganize, Settings([@"E:\DCIM"]));

        Assert.Empty(vm.Sources);
        Assert.Equal(NoFolderToReorganize, vm.ValidationMessage);

        _folders.Answers.Enqueue(@"D:\Archive\2024");
        vm.AddSourceCommand.Execute(null);

        Assert.Equal(Archive, _folders.Calls[0].InitialFolder);
        Assert.Equal([@"D:\Archive\2024"], vm.Sources);
    }

    [Fact]
    public void Reorganize_ModeIsMoveAndHidden_SubfoldersAlwaysIncluded()
    {
        var vm = Create(JobKind.Reorganize, Settings(mode: TransferMode.Copy, includeSubfolders: false));

        Assert.Equal(TransferMode.Move, vm.Mode);
        Assert.False(vm.ShowModeChoice);
        Assert.True(vm.IncludeSubfolders);
        Assert.False(vm.ShowIncludeSubfolders);
        Assert.Null(vm.MoveHint);
        Assert.Null(vm.MoveNotice);

        vm.Sources.Add(@"D:\Archive\2024");
        vm.NextCommand.Execute(null);

        var request = Assert.Single(_scanRequests);
        Assert.Equal(JobKind.Reorganize, request.Kind);
        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.True(request.IncludeSubfolders);
    }

    [Theory]
    [InlineData(@"D:\Photos", NotInArchive)]
    [InlineData(@"D:\Archive2", NotInArchive)]
    [InlineData(@"D:\Archive\.photook\x", NotInArchive)]
    [InlineData(@"D:\Archive\.PHOTOOK", NotInArchive)]
    [InlineData(@"D:\Archive\2024|D:\Archive\2024\a", FoldersOverlap)]
    [InlineData(@"D:\Archive\2024\a|D:\Archive\2024", FoldersOverlap)]
    [InlineData(@"D:\Archive|D:\Archive\2025", FoldersOverlap)]
    [InlineData(@"D:\Archive\2025|D:\Photos", NotInArchive)]
    public void Reorganize_Validation_RejectsBadFolders(string sources, string expected)
    {
        var vm = Create(JobKind.Reorganize);
        AddSources(vm, sources.Split('|'));

        Assert.Equal(expected, vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
        vm.NextCommand.Execute(null);
        Assert.Empty(_scanRequests);
    }

    [Fact]
    public void Reorganize_Validation_AcceptsSeveralSeparateFoldersInsideArchive()
    {
        var vm = Create(JobKind.Reorganize);
        AddSources(vm, @"D:\Archive\2024", @"D:\Archive\2025");

        Assert.Null(vm.ValidationMessage);
        Assert.True(vm.NextCommand.CanExecute(null));
    }

    [Fact]
    public void Reorganize_FolderDeletedAfterPicking_IsBlockedWithNotFoundMessage()
    {
        _fs.AddDirectory(@"D:\Archive\gone");
        var vm = Create(JobKind.Reorganize);
        vm.Sources.Add(@"D:\Archive\gone");
        Assert.Null(vm.ValidationMessage);

        _fs.RemoveEmptyDirectory(@"D:\Archive\gone"); // 고른 뒤에 폴더가 사라졌다(속성 변경 알림 없음)
        vm.NextCommand.Execute(null);

        Assert.Empty(_scanRequests);
        Assert.Equal(@"폴더를 찾을 수 없습니다: D:\Archive\gone", vm.ValidationMessage);
        Assert.False(vm.NextCommand.CanExecute(null));
        Assert.NotSame(_scanScreen, _shell.Current);
    }

    [Fact]
    public void Reorganize_WholeArchive_ShowsNoticeButAllowed()
    {
        var vm = Create(JobKind.Reorganize);
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Null(vm.WholeArchiveNotice);

        vm.Sources.Add(@"d:\archive\");

        Assert.Equal(WholeArchive, vm.WholeArchiveNotice);
        Assert.Contains(nameof(SourceViewModel.WholeArchiveNotice), changed);
        Assert.Null(vm.ValidationMessage);
        vm.NextCommand.Execute(null);
        Assert.Equal(JobKind.Reorganize, Assert.Single(_scanRequests).Kind);
    }

    [Fact]
    public void Reorganize_SubfolderOnly_ShowsNoWholeArchiveNotice()
    {
        var vm = Create(JobKind.Reorganize);
        vm.Sources.Add(@"D:\Archive\2024");

        Assert.Null(vm.WholeArchiveNotice);
    }

    [Fact]
    public void Reorganize_DoesNotRequireOverlapCheckAgainstArchive()
    {
        var reorganize = Create(JobKind.Reorganize);
        reorganize.Sources.Add(@"D:\Archive\2024");
        var import = Create(JobKind.Import);
        import.Sources.Add(@"D:\Archive\2024");

        Assert.Null(reorganize.ValidationMessage);
        Assert.Equal(ImportOverlap, import.ValidationMessage);
    }

    [Fact]
    public void Reorganize_ArchiveChange_RechecksFolders()
    {
        var vm = Create(JobKind.Reorganize);
        vm.Sources.Add(@"D:\Archive\2024");
        Assert.Null(vm.ValidationMessage);

        _fs.AddDirectory(@"D:\Archive2\x");
        vm.ArchiveRoot = @"D:\Archive2";

        Assert.Equal(NotInArchive, vm.ValidationMessage);
    }

    [Fact]
    public void Reorganize_WithoutArchive_AsksForArchiveFirst()
    {
        var vm = Create(JobKind.Reorganize, Settings(archive: null));

        Assert.Equal("보관 폴더를 고르세요", vm.ValidationMessage);
    }

    // ----- 이름 옵션 -----

    [Theory]
    [InlineData(JobKind.Absorb, true)]
    [InlineData(JobKind.Absorb, false)]
    [InlineData(JobKind.Reorganize, true)]
    [InlineData(JobKind.Reorganize, false)]
    public void Next_PassesKindAndUseFolderNames(JobKind kind, bool useFolderNames)
    {
        var vm = Create(kind);
        vm.Sources.Add(kind == JobKind.Absorb ? @"D:\Photos" : @"D:\Archive\2024");
        vm.UseFolderNames = useFolderNames;

        vm.NextCommand.Execute(null);

        var request = Assert.Single(_scanRequests);
        Assert.Equal(kind, request.Kind);
        Assert.Equal(useFolderNames, request.UseFolderNames);
        Assert.Same(_scanScreen, _shell.Current);
    }

    [Fact]
    public void Next_CarriesSavedPreviewOptionsUnchanged()
    {
        var saved = Settings() with { SplitUnderscores = true, Subfolders = SubfolderPolicy.Merge, KeepReviewInPlace = false };
        var vm = Create(JobKind.Reorganize, saved);
        vm.Sources.Add(@"D:\Archive\2024");

        vm.NextCommand.Execute(null);

        var request = Assert.Single(_scanRequests);
        Assert.True(request.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Merge, request.Subfolders);
        Assert.False(request.KeepReviewInPlace);
        var stored = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.True(stored.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Merge, stored.Subfolders);
        Assert.False(stored.KeepReviewInPlace);
    }

    [Fact]
    public void Next_DefaultPreviewOptions_AreFalseKeepTrue()
    {
        var vm = Create(JobKind.Absorb);
        vm.Sources.Add(@"D:\Photos");

        vm.NextCommand.Execute(null);

        var request = Assert.Single(_scanRequests);
        Assert.False(request.SplitUnderscores);
        Assert.Equal(SubfolderPolicy.Keep, request.Subfolders);
        Assert.True(request.KeepReviewInPlace);
    }

    [Theory]
    [InlineData(JobKind.Absorb, @"D:\Photos")]
    [InlineData(JobKind.Reorganize, @"D:\Archive\2024")]
    public void Save_NonImport_KeepsImportSources(JobKind kind, string source)
    {
        var previous = Settings([@"E:\DCIM"], mode: TransferMode.Copy, includeSubfolders: false);
        var vm = Create(kind, previous);
        vm.Sources.Add(source);
        vm.UseFolderNames = false;

        vm.NextCommand.Execute(null);

        var stored = AppSettingsStore.Load(_fs, SettingsPath);
        Assert.Equal([@"E:\DCIM"], stored.Sources);
        Assert.Equal(TransferMode.Copy, stored.Mode);
        Assert.False(stored.IncludeSubfolders);
        Assert.Equal(Archive, stored.ArchiveRoot);
        Assert.False(stored.UseFolderNames);
    }

    [Fact]
    public void Save_Import_StillSavesItsSources()
    {
        var vm = Create(JobKind.Import, Settings([@"E:\DCIM"], archive: Archive));

        vm.NextCommand.Execute(null);

        Assert.Equal([@"E:\DCIM"], AppSettingsStore.Load(_fs, SettingsPath).Sources);
        Assert.Equal(JobKind.Import, Assert.Single(_scanRequests).Kind);
    }

    [Fact]
    public void UseFolderNames_DefaultsOn_AndPersists()
    {
        var vm = Create(JobKind.Absorb);
        Assert.True(vm.UseFolderNames);
        vm.UseFolderNames = false;
        vm.Sources.Add(@"D:\Photos");
        vm.NextCommand.Execute(null);

        var again = Create(JobKind.Absorb, AppSettingsStore.Load(_fs, SettingsPath));

        Assert.False(again.UseFolderNames);
    }

    [Fact]
    public void OldSettingsLine_LoadsWithDefaultOn()
    {
        _fs.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        _fs.AppendLine(SettingsPath, "{\"sources\":[],\"includeSubfolders\":true,\"archiveRoot\":\"D:\\\\Archive\",\"presetName\":\"날짜 + 이벤트\",\"presetFile\":null,\"mode\":\"Copy\"}");

        var vm = Create(JobKind.Absorb, AppSettingsStore.Load(_fs, SettingsPath));

        Assert.True(vm.UseFolderNames);
    }

    [Fact]
    public void EventsNotUsedNotice_WhenPresetHasNoEvent()
    {
        var vm = Create(JobKind.Absorb, Settings(presetName: "날짜 + 이벤트"));
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Null(vm.EventsNotUsedNotice);

        vm.SelectedPreset = vm.Presets.Single(p => p.Name == "날짜만");

        Assert.Equal(EventsNotUsed, vm.EventsNotUsedNotice);
        Assert.Contains(nameof(SourceViewModel.EventsNotUsedNotice), changed);

        vm.SelectedPreset = vm.Presets.Single(p => p.Name == "날짜 + 이벤트");
        Assert.Null(vm.EventsNotUsedNotice);
    }

    [Fact]
    public void EventsNotUsedNotice_IsHiddenForImport()
    {
        var vm = Create(JobKind.Import, Settings(presetName: "날짜만"));

        Assert.Null(vm.EventsNotUsedNotice);
    }

    [Fact]
    public void Absorb_WithDriveRoot_UsesThatRoot()
    {
        _fs.AddDirectory(@"E:\");
        var vm = Create(JobKind.Absorb, driveRoot: @"E:\");

        Assert.Equal([@"E:\"], vm.Sources);
    }

    [Fact]
    public void ImportSettings_ForPresetFileWithoutEvent_StillValidates()
    {
        // 사용자 프리셋도 같은 방식으로 이벤트 사용 여부를 판단한다.
        var path = Path.Combine(PresetFolder, "plain.json");
        _fs.AddFile(path, Encoding.UTF8.GetBytes(PresetSerializer.Serialize(BuiltInPresets.DateOnly with { Name = "내 방식" })));
        var vm = Create(JobKind.Reorganize, Settings() with { PresetName = "내 방식", PresetFile = path });

        Assert.Equal(EventsNotUsed, vm.EventsNotUsedNotice);
    }
}
