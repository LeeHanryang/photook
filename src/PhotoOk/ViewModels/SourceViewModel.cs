using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.IO;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Settings;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 가져올 폴더, 보관 폴더, 정리 방식, 복사/이동을 고르는 화면. 사용자 파일은 만지지 않고 폴더 존재만 확인한다.
/// 다음·뒤로 화면은 직접 만들지 않고 생성자로 받은 팩토리로 만든다.
/// </summary>
public sealed partial class SourceViewModel : ObservableObject
{
    private const string FirstBuiltInName = "날짜 + 이벤트";
    private const string PresetFileFilter = "프리셋 파일 (*.json)|*.json";

    private readonly ShellViewModel _shell;
    private readonly IFileSystem _fileSystem;
    private readonly PresetLibrary _presetLibrary;
    private readonly string _settingsPath;
    private readonly IFolderPicker _folderPicker;
    private readonly IFileOpenPicker _filePicker;
    private readonly IDialogService _dialogs;
    private readonly Func<ImportSettings, ObservableObject> _createScan;
    private readonly Func<ObservableObject> _createStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(MoveNotice))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private string? archiveRoot;

    [ObservableProperty]
    private bool includeSubfolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private PresetEntry? selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveNotice))]
    private TransferMode mode;

    /// <param name="settings">처음에 보여 줄 값(마지막 작업의 선택).</param>
    /// <param name="driveRoot">시작 화면에서 드라이브를 골라 왔으면 그 루트. 있으면 저장된 가져올 폴더 대신 이것만 채운다.</param>
    public SourceViewModel(ShellViewModel shell, IFileSystem fileSystem, PresetLibrary presetLibrary, string settingsPath,
        AppSettings settings, string? driveRoot, IFolderPicker folderPicker, IFileOpenPicker filePicker,
        IDialogService dialogs, Func<ImportSettings, ObservableObject> createScan, Func<ObservableObject> createStart)
    {
        _shell = shell;
        _fileSystem = fileSystem;
        _presetLibrary = presetLibrary;
        _settingsPath = settingsPath;
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _dialogs = dialogs;
        _createScan = createScan;
        _createStart = createStart;

        foreach (var source in driveRoot is null ? settings.Sources : [driveRoot])
        {
            Sources.Add(source);
        }

        Sources.CollectionChanged += OnSourcesChanged;

        archiveRoot = settings.ArchiveRoot;
        includeSubfolders = settings.IncludeSubfolders;
        mode = settings.Mode;

        var (presets, _) = presetLibrary.LoadAll();
        foreach (var preset in presets)
        {
            Presets.Add(preset);
        }

        selectedPreset = ChooseInitialPreset(settings);
    }

    public ObservableCollection<string> Sources { get; } = [];

    public ObservableCollection<PresetEntry> Presets { get; } = [];

    /// <summary>다음으로 가지 못하는 이유(한 가지). 가능하면 null.</summary>
    public string? ValidationMessage
    {
        get
        {
            if (Sources.Count == 0)
            {
                return "가져올 폴더를 하나 이상 고르세요";
            }

            if (string.IsNullOrWhiteSpace(ArchiveRoot))
            {
                return "보관 폴더를 고르세요";
            }

            foreach (var source in Sources)
            {
                if (!Path.IsPathFullyQualified(source) || !_fileSystem.DirectoryExists(source))
                {
                    return "폴더를 찾을 수 없습니다: " + source;
                }
            }

            if (Sources.Any(source => Overlaps(source, ArchiveRoot)))
            {
                return "보관 폴더와 가져올 폴더가 겹칩니다. 서로 다른 폴더를 고르세요";
            }

            return SelectedPreset is null ? "정리 방식을 고르세요" : null;
        }
    }

    /// <summary>이동을 골랐는데 가져올 폴더가 보관 폴더와 다른 드라이브면 알린다(그 파일은 복사로 처리된다).</summary>
    public string? MoveNotice
    {
        get
        {
            if (Mode != TransferMode.Move || string.IsNullOrWhiteSpace(ArchiveRoot))
            {
                return null;
            }

            var archiveVolume = VolumeOf(ArchiveRoot);
            if (archiveVolume is null)
            {
                return null;
            }

            var crossDrive = Sources.Any(source =>
                VolumeOf(source) is { } volume && !string.Equals(volume, archiveVolume, StringComparison.OrdinalIgnoreCase));
            return crossDrive ? "다른 드라이브에서는 이동을 골라도 복사합니다" : null;
        }
    }

    [RelayCommand]
    private void AddSource()
    {
        var picked = _folderPicker.PickFolder("가져올 폴더 선택", Sources.Count > 0 ? Sources[^1] : null);
        if (picked is null || Sources.Any(s => SamePath(s, picked)))
        {
            return;
        }

        Sources.Add(picked);
    }

    [RelayCommand]
    private void RemoveSource(string source) => Sources.Remove(source);

    [RelayCommand]
    private void PickArchive()
    {
        var picked = _folderPicker.PickFolder("보관 폴더 선택", ArchiveRoot);
        if (picked is not null)
        {
            ArchiveRoot = picked;
        }
    }

    [RelayCommand]
    private void OpenPresetFile()
    {
        var path = _filePicker.PickFile("프리셋 파일 열기", PresetFileFilter);
        if (path is null)
        {
            return;
        }

        PresetEntry loaded;
        try
        {
            loaded = _presetLibrary.LoadFile(path);
        }
        catch (PresetException ex)
        {
            _dialogs.ShowError("프리셋을 열 수 없습니다", ex.Message);
            return;
        }

        SelectedPreset = AddOrFind(loaded);
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next()
    {
        // 명령의 Execute는 CanExecute를 확인하지 않으므로 여기서도 막는다.
        if (ValidationMessage is not null || SelectedPreset is null || ArchiveRoot is null)
        {
            return;
        }

        var sources = Sources.ToList();
        Save(sources);
        _shell.Navigate(_createScan(new ImportSettings(sources, IncludeSubfolders, ArchiveRoot, SelectedPreset.Preset, Mode)));
    }

    private bool CanNext() => ValidationMessage is null;

    [RelayCommand]
    private void Back() => _shell.Navigate(_createStart());

    private void Save(List<string> sources)
    {
        var settings = new AppSettings(sources, IncludeSubfolders, ArchiveRoot, SelectedPreset?.Name, SelectedPreset?.FilePath, Mode);
        try
        {
            AppSettingsStore.Save(_fileSystem, _settingsPath, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 마지막 선택을 기억하지 못할 뿐이다. 가져오기를 막지는 않는다.
        }
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(MoveNotice));
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 같은 이름의 사용자 프리셋이 있을 수 있으므로 사용자 프리셋은 파일로, 내장 프리셋은 파일이 없을 때만 이름으로 찾는다.
    /// 파일이 목록에 없으면 읽어서 목록에 더한다. 어느 쪽도 못 찾으면 첫 내장 프리셋.
    /// </summary>
    private PresetEntry? ChooseInitialPreset(AppSettings settings)
    {
        if (settings.PresetFile is not null)
        {
            var byFile = Presets.FirstOrDefault(p => p.FilePath is not null && SamePath(p.FilePath, settings.PresetFile));
            if (byFile is not null)
            {
                return byFile;
            }

            try
            {
                return AddOrFind(_presetLibrary.LoadFile(settings.PresetFile));
            }
            catch (PresetException)
            {
                // 파일이 없어졌거나 읽을 수 없으면 내장 프리셋 가운데서 고른다.
            }
        }

        return Presets.FirstOrDefault(p => p.FilePath is null && p.Name == settings.PresetName)
            ?? Presets.FirstOrDefault(p => p.FilePath is null && p.Name == FirstBuiltInName);
    }

    /// <summary>같은 파일이 목록에 이미 있으면 그 항목을, 없으면 <paramref name="entry"/>를 더해 돌려준다.</summary>
    private PresetEntry AddOrFind(PresetEntry entry)
    {
        var existing = Presets.FirstOrDefault(p => p.FilePath is not null && entry.FilePath is not null
            && SamePath(p.FilePath, entry.FilePath));
        if (existing is not null)
        {
            return existing;
        }

        Presets.Add(entry);
        return entry;
    }

    private string? VolumeOf(string path)
    {
        try
        {
            return _fileSystem.GetVolumeRoot(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(NormalizedWithSeparator(a), NormalizedWithSeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>한쪽이 다른 쪽 안에 있거나 같으면 true. 구분자까지 맞춰 비교해서 <c>D:\Photos2</c>는 <c>D:\Photos</c> 밖이다.</summary>
    private static bool Overlaps(string a, string b)
    {
        var first = NormalizedWithSeparator(a);
        var second = NormalizedWithSeparator(b);
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase)
            || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizedWithSeparator(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        return full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
    }
}
