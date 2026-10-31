using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.Execution;
using PhotoOk.Core.IO;
using PhotoOk.Core.Layout;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 스캔 결과로 세운 계획을 보여 주는 화면: 요약, 촬영 카드(이벤트 이름 입력), 정리 후 폴더 트리, 선택 파일의 "왜 이 경로인가".
/// 이벤트 이름이 바뀌면 <c>replanDelay</c>만큼 기다렸다가 백그라운드에서 계획을 다시 세우고, 가장 나중 요청의 결과만 반영한다.
/// 위쪽 옵션을 바꾸면 같은 경로로, 다시 스캔하지 않고 촬영 묶기부터 다시 해서 카드와 트리를 함께 바꿔 끼운다.
/// 계획을 세우는 동안(<see cref="IsReplanning"/>)과 실행 전 점검 오류가 있을 때는 실행할 수 없다. 디스크는 읽기만 한다.
/// 상태는 화면(UI) 스레드에서만 바꾼다.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject, IBusyViewModel, ILeavableScreen
{
    private const string ReviewGroupId = "판단 필요";
    private const int ThumbnailLimit = 12;
    private const int CardThumbnailSize = 96;
    private const int GridThumbnailSize = 160;
    private const int BlockingLineLimit = 5;
    private const int IgnoredLineLimit = 200;
    private const string ConfirmTitle = "확인이 필요한 오류가 있습니다";
    private const string NoCaptureTimeText = "촬영 시각 없음 — 파일 수정 시각으로 대체";
    private const string NothingToTransferMessage = "옮길 파일이 없습니다 — 모두 보관 폴더에 있거나 중복입니다";
    private const string NothingToMoveInPlaceMessage = "옮길 파일이 없습니다 — 모두 제자리에 있습니다";
    private const string PlanningMessage = "정리 계획을 세우는 중…";
    private const string ReviewKeptNote = "자동으로 정할 수 없어 옮기지 않습니다";
    private const string NeverUseNameFailedTitle = "이름을 저장하지 못했습니다";
    private const string NeverUseNameFailedMessage = "이 이름을 제외 목록에 저장하지 못했습니다. 자세한 내용은 로그 폴더에 남겼습니다.";
    private const string PlanFailedMessage = "계획을 세우지 못했습니다. 자세한 내용은 로그 폴더에 남겼습니다. 이벤트 이름을 고치거나 뒤로 가서 다시 시도해 주세요.";

    private readonly ShellViewModel _shell;

    /// <summary>스캔한 그대로의 결과. 옵션을 바꾸면 이것에서 촬영을 다시 묶는다.</summary>
    private readonly ScanOutcome _scan;
    private readonly IFileSystem _fileSystem;
    private readonly IThumbnailProvider _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly FileLog _log;
    private readonly Func<Plan, ScanFacts, ObservableObject> _createRun;
    private readonly Func<ImportSettings, ObservableObject> _createBack;
    private readonly TimeSpan _replanDelay;
    private readonly Func<ImportSettings, ScanOutcome, IReadOnlyDictionary<string, string>, PlanOutcome> _buildPlan;
    private readonly Func<ImportSettings, ScanOutcome, ScanOutcome> _regroup;
    private readonly Action<ImportSettings> _saveChoices;
    private readonly string _ignoredNamesPath;

    /// <summary>스캔한 모든 파일(대표·딸린 파일)의 정보와 그 파일이 속한 묶음. 키는 전체 경로.</summary>
    private readonly Dictionary<string, (PhotoInfo Info, FileGroup Group)> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>화면을 떠나면 취소한다(썸네일 요청).</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource _thumbnailCts = new();
    private CancellationTokenSource _cardThumbnailCts = new();
    private CancellationTokenSource? _replanCts;

    /// <summary>지금 고른 값. 옵션을 바꾸면 바로 바뀐다(계획은 아직 옛 값일 수 있다). 화면 스레드에서만 바꾼다.</summary>
    private ImportSettings _settings;
    private PresetEntry _selectedPreset;

    /// <summary>지금 보이는 카드와 그 카드를 만든 값. 반영된 계획도 이 값으로 세운 것이다. 화면 스레드에서만 바꾼다.</summary>
    private Grouping _shown;

    /// <summary>
    /// 사용자가 고친 이름(지운 이름은 빈 문자열). 이름은 촬영 Id가 아니라 카드의 파일 묶음에 속한다: Id에는 폴더 이름이 들어 있어 옵션에 따라 바뀌고,
    /// 같은 Id로 남아도 파일이 달라질 수 있다. 반영된 카드 가운데 같은 파일 묶음이 없으면 버린다. 화면 스레드에서만 바꾼다.
    /// </summary>
    private readonly Dictionary<string, string> _edits = new(StringComparer.Ordinal);

    /// <summary>실행 화면으로 넘어갔다(또는 실행 전 확인을 묻는 중이다). 저장과 실행 화면 만들기가 두 번 일어나지 않게 막는다.</summary>
    private bool _executed;

    /// <summary>
    /// 가장 나중 요청의 계획 세우기가 끝나면 완료된다. 새 요청은 이것을 기다린 뒤에 계획을 세운다(스레드 풀에 계획 세우기가
    /// 여러 개 동시에 돌지 않게). 화면 스레드에서만 바꾼다.
    /// </summary>
    private Task _lastBuild = Task.CompletedTask;
    private int _generation;
    private int _appliedGeneration;
    private Plan? _plan;
    private bool _hasPlan;
    private Dictionary<string, PlannedOperation> _placementBySource = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PlannedSkip> _skipBySource = new(StringComparer.OrdinalIgnoreCase);

    private int _reviewCount;
    private int _duplicateCount;
    private int _keptInPlaceCount;
    private int _transferCount;
    private long _transferBytes;
    private long _freeBytes;
    private string _transferText = "";
    private IReadOnlyList<string> _planNotes = [];
    private string? _blockingReason;
    private bool _isReplanning;
    private FileDetailViewModel? _detail;
    private IReadOnlyList<FolderNodeViewModel> _folderTree = [];
    private bool _freeSpaceKnown;

    [ObservableProperty]
    private SessionItemViewModel? selectedSession;

    [ObservableProperty]
    private ThumbnailItemViewModel? selectedThumbnail;

    /// <param name="replanDelay">이벤트 이름 입력이 멈춘 뒤 다시 계획하기까지 기다리는 시간(앱 0.5초, 테스트 0).</param>
    /// <param name="ignoredNamesPath">"이 이름 다시 쓰지 않기"로 고른 폴더 이름을 덧붙이는 앱 자체의 글 파일.</param>
    /// <param name="presets">위쪽에서 고를 수 있는 정리 방식. 지금 쓰는 방식이 없으면(파일에서 연 프리셋) 뒤에 더한다.</param>
    /// <param name="saveChoices">실행할 때 최종 선택을 저장한다. 옵션을 바꿀 때는 부르지 않는다.</param>
    /// <param name="buildPlan">계획을 세우는 함수. 생략하면 <paramref name="workflow"/>의 것을 쓴다(스캔과 같은 인스턴스여야 해시를 다시 쓴다).</param>
    /// <param name="regroup">사진 정보를 다시 읽지 않고 촬영을 다시 묶는 함수. 생략하면 <paramref name="workflow"/>의 것을 쓴다.</param>
    public PreviewViewModel(ShellViewModel shell, ImportWorkflow workflow, ImportSettings settings, ScanOutcome scan,
        IFileSystem fileSystem, IThumbnailProvider thumbnails, IDialogService dialogs, FileLog log,
        Func<Plan, ScanFacts, ObservableObject> createRun, Func<ImportSettings, ObservableObject> createBack, TimeSpan replanDelay,
        string ignoredNamesPath, IReadOnlyList<PresetEntry> presets, Action<ImportSettings> saveChoices,
        Func<ImportSettings, ScanOutcome, IReadOnlyDictionary<string, string>, PlanOutcome>? buildPlan = null,
        Func<ImportSettings, ScanOutcome, ScanOutcome>? regroup = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(scan);

        _shell = shell;
        _settings = settings;
        _scan = scan;
        _fileSystem = fileSystem;
        _thumbnails = thumbnails;
        _dialogs = dialogs;
        _log = log;
        _createRun = createRun;
        _createBack = createBack;
        _replanDelay = replanDelay;
        _ignoredNamesPath = ignoredNamesPath;
        _buildPlan = buildPlan ?? workflow.BuildPlan;
        _regroup = regroup ?? workflow.Regroup;
        _saveChoices = saveChoices;
        (Presets, _selectedPreset) = PresetChoices(presets, settings);

        foreach (var session in scan.Sessions)
        {
            foreach (var group in session.Groups)
            {
                foreach (var file in group.AllFiles)
                {
                    _files[FullPath(file.Path)] = (file, group);
                }
            }
        }

        ScanErrors = scan.Errors.Select(DescribeScanError).ToList();
        IgnoredFiles = scan.Ignored.Count <= IgnoredLineLimit
            ? [.. scan.Ignored]
            : [.. scan.Ignored.Take(IgnoredLineLimit),
                string.Create(CultureInfo.InvariantCulture, $"외 {scan.Ignored.Count - IgnoredLineLimit}건")];

        _shown = Grouping.Create(settings, scan);
        ShowCards(_shown);
        RequestReplan(TimeSpan.Zero);
    }

    public event EventHandler? BusyEnded;

    /// <summary>고를 수 있는 정리 방식.</summary>
    public IReadOnlyList<PresetEntry> Presets { get; }

    /// <summary>정리 방식. 바꾸면 판단 필요 카드와 폴더 형식도 새 방식을 따른다. null은 무시한다(목록 컨트롤이 잠깐 비울 때 온다).</summary>
    public PresetEntry? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is not null && !ReferenceEquals(value, _selectedPreset))
            {
                _selectedPreset = value;
                ChangeOptions(_settings with { Preset = value.Preset, PresetFile = value.FilePath });
            }
        }
    }

    /// <summary>폴더 이름을 이벤트 이름으로 쓴다.</summary>
    public bool UseFolderNames
    {
        get => _settings.UseFolderNames;
        set => ChangeOptions(_settings with { UseFolderNames = value });
    }

    /// <summary>읽어 온 이름의 <c>_</c>를 폴더로 나눈다.</summary>
    public bool SplitUnderscores
    {
        get => _settings.SplitUnderscores;
        set => ChangeOptions(_settings with { SplitUnderscores = value });
    }

    /// <summary>뜻 있는 하위 폴더를 유지할지, 제자리에 둘지, 합칠지.</summary>
    public SubfolderPolicy Subfolders
    {
        get => _settings.Subfolders;
        set => ChangeOptions(_settings with { Subfolders = value });
    }

    /// <summary>재정리에서 판단 필요 파일을 제자리에 둔다(끄면 판단 필요 폴더로 옮긴다).</summary>
    public bool KeepReviewInPlace
    {
        get => _settings.KeepReviewInPlace;
        set => ChangeOptions(_settings with { KeepReviewInPlace = value });
    }

    /// <summary>폴더 이름·<c>_</c> 나누기·하위 폴더 옵션을 보인다. 가져오기는 폴더 이름을 읽지 않으므로 숨긴다.</summary>
    public bool ShowFolderOptions => _settings.Kind != JobKind.Import;

    /// <summary>판단 필요 파일 옵션을 보인다. 재정리에서만 뜻이 있다.</summary>
    public bool ShowReviewOption => _settings.Kind == JobKind.Reorganize;

    /// <summary>날짜순 촬영 카드, 그 뒤에 판단 필요 묶음(있을 때). 옵션을 바꾸면 새 카드로 통째로 바뀐다.</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>아카이브 기준 정리 후 폴더 트리. 계획이 바뀔 때마다 백그라운드에서 만든 새 목록으로 통째로 바뀐다.</summary>
    public IReadOnlyList<FolderNodeViewModel> FolderTree
    {
        get => _folderTree;
        private set => SetProperty(ref _folderTree, value);
    }

    /// <summary>선택 촬영의 처음 12장.</summary>
    public ObservableCollection<ThumbnailItemViewModel> Thumbnails { get; } = [];

    /// <summary>선택 파일(기본은 선택 촬영의 첫 파일) 정보. 계획이 아직 없으면 null.</summary>
    public FileDetailViewModel? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>판단 필요 카드의 파일 수(대표·딸린 파일, 중복이라 건너뛰는 파일 포함). 이벤트 이름과 관계없어 카드를 만들 때 정한다.</summary>
    public int ReviewCount
    {
        get => _reviewCount;
        private set => SetProperty(ref _reviewCount, value);
    }

    /// <summary>
    /// 같은 파일이 이미 있어 건너뛰는 파일 수(보관 폴더의 대상 자리에 있거나, 이번 작업 안에서 겹치거나,
    /// 흡수에서 아카이브의 다른 폴더에 같은 내용이 있음).
    /// </summary>
    public int DuplicateCount
    {
        get => _duplicateCount;
        private set => SetProperty(ref _duplicateCount, value);
    }

    /// <summary>
    /// 재정리에서 제자리에 두는 파일 수(이미 제자리인 파일, 판단 필요라 두는 파일, 하위 폴더·다른 볼륨이라 두는 파일).
    /// 같은 파일이 이미 있어 두는 것은 <see cref="DuplicateCount"/>에 센다. 재정리가 아니면 0.
    /// </summary>
    public int KeptInPlaceCount
    {
        get => _keptInPlaceCount;
        private set
        {
            if (SetProperty(ref _keptInPlaceCount, value))
            {
                OnPropertyChanged(nameof(KeptInPlaceText));
            }
        }
    }

    /// <summary>재정리에서만 "그대로 N개". 그 밖의 작업은 null.</summary>
    public string? KeptInPlaceText => _settings.Kind == JobKind.Reorganize
        ? string.Create(CultureInfo.InvariantCulture, $"그대로 {KeptInPlaceCount}개")
        : null;

    /// <summary>복사·이동할 파일 수.</summary>
    public int TransferCount
    {
        get => _transferCount;
        private set => SetProperty(ref _transferCount, value);
    }

    public long TransferBytes
    {
        get => _transferBytes;
        private set
        {
            if (SetProperty(ref _transferBytes, value))
            {
                OnPropertyChanged(nameof(TransferSizeText));
            }
        }
    }

    /// <summary>보관 폴더 드라이브의 여유 공간. 알 수 없으면 0(<see cref="FreeSpaceText"/>는 "알 수 없음").</summary>
    public long FreeBytes
    {
        get => _freeBytes;
        private set
        {
            if (SetProperty(ref _freeBytes, value))
            {
                OnPropertyChanged(nameof(FreeSpaceText));
            }
        }
    }

    /// <summary>예: "복사 120개", "복사 3개 · 이동 117개".</summary>
    public string TransferText
    {
        get => _transferText;
        private set => SetProperty(ref _transferText, value);
    }

    public string TransferSizeText => FormatBytes(TransferBytes);

    /// <summary>여유 공간. 알 수 없으면 "알 수 없음".</summary>
    public string FreeSpaceText => _freeSpaceKnown ? FormatBytes(FreeBytes) : "알 수 없음";

    /// <summary>스캔 중 읽지 못한 폴더·파일. 예외 원문 없이 사유와 경로만.</summary>
    public IReadOnlyList<string> ScanErrors { get; }

    /// <summary>스캔 오류가 있으면 "오류 N건 확인 필요".</summary>
    public string? ScanErrorNotice => ScanErrors.Count == 0
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"오류 {ScanErrors.Count}건 확인 필요");

    /// <summary>사진·동영상이 아니어서 가져오지 않는 파일 수.</summary>
    public int IgnoredCount => _scan.Ignored.Count;

    /// <summary>가져오지 않는 파일이 있으면 "가져오지 않는 파일 N개".</summary>
    public string? IgnoredNotice => IgnoredCount == 0
        ? null
        : string.Create(CultureInfo.InvariantCulture, $"가져오지 않는 파일 {IgnoredCount}개");

    /// <summary>가져오지 않는 파일의 경로. 최대 200줄 뒤에 "외 N건".</summary>
    public IReadOnlyList<string> IgnoredFiles { get; }

    /// <summary>계획 메모와 실행 전 점검 경고.</summary>
    public IReadOnlyList<string> PlanNotes
    {
        get => _planNotes;
        private set => SetProperty(ref _planNotes, value);
    }

    /// <summary>실행할 수 없는 이유(실행 전 점검 오류를 줄바꿈으로 이은 것). 실행할 수 있으면 null.</summary>
    public string? BlockingReason
    {
        get => _blockingReason;
        private set
        {
            if (SetProperty(ref _blockingReason, value))
            {
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>계획을 세우는 중이면 true(첫 계획과 다시 계획 모두). 이때는 실행할 수 없다.</summary>
    public bool IsReplanning
    {
        get => _isReplanning;
        private set
        {
            if (SetProperty(ref _isReplanning, value))
            {
                ExecuteCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PlanningText));
            }
        }
    }

    /// <summary>
    /// 반영된 계획이 있으면 true. 첫 계획 전(또는 계획 실패 뒤)에는 중복·옮길 파일·용량·여유 공간 합계를 숨긴다
    /// (값이 0이라 "용량 0 B · 여유 공간 알 수 없음"처럼 잘못 보이므로).
    /// </summary>
    public bool HasPlan
    {
        get => _hasPlan;
        private set => SetProperty(ref _hasPlan, value);
    }

    /// <summary>계획을 세우는 동안 위쪽 요약에 보일 안내. 아니면 null.</summary>
    public string? PlanningText => IsReplanning ? PlanningMessage : null;

    /// <summary>
    /// 늘 false. 계획 세우기는 디스크를 읽기만 하므로 창을 닫을 때 "지금 파일까지 마치고 멈출까요?"를 묻지 않는다.
    /// 재계획 중에 화면을 떠나면 <see cref="Cancel"/>로 멈추고, 끝나면 <see cref="BusyEnded"/>를 알린다.
    /// </summary>
    public bool IsBusy => false;

    /// <summary>가장 나중 계획 요청이 끝나면(반영·취소·실패) 완료된다.</summary>
    public Task WhenPlanned { get; private set; } = Task.CompletedTask;

    /// <summary>진행 중인 재계획을 멈춘다. 결과는 반영하지 않는다. 예외를 던지지 않는다.</summary>
    public void Cancel()
    {
        try
        {
            _replanCts?.Cancel();
        }
        catch (AggregateException ex)
        {
            _log.Error("계획 취소 중 오류", ex);
        }
    }

    /// <summary>
    /// 다른 화면으로 바뀌었다(뒤로·정리 시작뿐 아니라 오류 뒤 시작 화면으로 돌아갈 때도). 남은 재계획과 썸네일 요청을 멈춘다.
    /// 여러 번 불려도 안전하고 예외를 던지지 않는다.
    /// </summary>
    public void OnLeft()
    {
        Cancel();
        Leave();
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private Task Execute()
    {
        // 명령의 Execute는 CanExecute를 확인하지 않으므로 여기서도 막는다.
        if (!CanExecute() || _plan is not { } plan)
        {
            return Task.CompletedTask;
        }

        _executed = true;
        ExecuteCommand.NotifyCanExecuteChanged();
        var screen = _shell.Current;
        try
        {
            if (_scan.Errors.Count > 0)
            {
                var message = string.Create(CultureInfo.InvariantCulture,
                    $"읽지 못한 폴더나 파일 {_scan.Errors.Count}건이 있습니다. 그래도 실행할까요?");
                if (!_dialogs.Confirm(ConfirmTitle, message))
                {
                    return Task.CompletedTask;
                }
            }

            SaveChoices();

            // 실행 화면을 만들다 실패해도 이 화면은 그대로 쓸 수 있도록, 다 만든 뒤에 떠난다.
            var run = _createRun(plan, ScanFacts.From(_scan, _settings.Kind, plan));
            Leave();
            _shell.Navigate(run);
            return Task.CompletedTask;
        }
        finally
        {
            // 화면을 바꾸지 못했으면(취소, 뜻밖의 오류) 다시 실행할 수 있게 푼다. 바꾼 뒤의 오류라면 실행 화면이 이미 넘어갔으므로 그대로 둔다.
            if (ReferenceEquals(_shell.Current, screen))
            {
                _executed = false;
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>마지막 선택을 기억하지 못할 뿐이므로 저장에 실패해도 실행은 막지 않는다.</summary>
    private void SaveChoices()
    {
        try
        {
            _saveChoices(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("마지막 선택을 저장하지 못했습니다", ex);
        }
    }

    private bool CanExecute() =>
        !_executed && _plan is not null && !IsReplanning && _appliedGeneration == _generation && BlockingReason is null;

    [RelayCommand]
    private void Back()
    {
        Cancel();
        Leave();
        _shell.Navigate(_createBack(_settings));
    }

    partial void OnSelectedSessionChanged(SessionItemViewModel? value)
    {
        CancelAndReplace(ref _thumbnailCts);
        var ct = _thumbnailCts.Token;

        Thumbnails.Clear();
        foreach (var path in value?.Files.Take(ThumbnailLimit) ?? [])
        {
            var item = new ThumbnailItemViewModel(path);
            Thumbnails.Add(item);
            _ = LoadThumbnailAsync(path, GridThumbnailSize, image => item.Image = image, ct);
        }

        SelectedThumbnail = Thumbnails.FirstOrDefault();
        UpdateDetail();
    }

    partial void OnSelectedThumbnailChanged(ThumbnailItemViewModel? value) => UpdateDetail();

    /// <summary>
    /// 고친 이름을 그 카드의 파일 묶음에 기억하고 계획을 다시 요청한다. 읽어 온 이름으로 되돌리면 손대지 않은 것과 같다.
    /// 바꿔 끼워 버려진 카드의 이름 변경은 무시한다.
    /// </summary>
    private void OnEventNameChanged(SessionItemViewModel card)
    {
        if (!Sessions.Contains(card))
        {
            return;
        }

        if (card.IsEdited)
        {
            _edits[card.FilesKey] = card.EventName;
        }
        else
        {
            _edits.Remove(card.FilesKey);
        }

        RequestReplan(_replanDelay);
    }

    /// <summary>옵션을 바꿨다. 값이 그대로면 아무것도 하지 않는다. 입력을 기다릴 일이 없으므로 바로 계획을 다시 요청한다.</summary>
    private void ChangeOptions(ImportSettings changed, [CallerMemberName] string? option = null)
    {
        if (changed == _settings)
        {
            return;
        }

        _settings = changed;
        OnPropertyChanged(option);
        RequestReplan(TimeSpan.Zero);
    }

    /// <summary>
    /// 카드 목록을 <paramref name="grouping"/>대로 새로 만든다. 고친 이름은 같은 파일 묶음의 카드에만 되살린다. 읽어 온 이름을 그대로 둔 카드는 새 값을 따른다.
    /// 이전 카드의 썸네일 요청은 취소한다. 고른 카드는 같은 파일 묶음, 없으면 같은 Id, 그것도 없으면 첫 카드다.
    /// </summary>
    private void ShowCards(Grouping grouping)
    {
        CancelAndReplace(ref _cardThumbnailCts);

        // 목록을 비우면 화면의 선택이 풀리면서 SelectedSession이 null이 될 수 있으므로 먼저 읽어 둔다.
        var selected = SelectedSession;
        Sessions.Clear();
        foreach (var spec in grouping.Cards)
        {
            var card = new SessionItemViewModel(spec.Id, spec.Title, spec.Devices, spec.IsReview, spec.Files, spec.SuggestedName,
                spec.IsReview ? null : OnEventNameChanged, RequestCardThumbnail, spec.IsReview ? null : OnNeverUseName, spec.Note,
                spec.SuggestedFromFormat);
            if (!spec.IsReview && _edits.TryGetValue(spec.FilesKey, out var edited))
            {
                card.SetEventNameSilently(edited);
            }

            Sessions.Add(card);
        }

        ReviewCount = grouping.ReviewCount;
        SelectedSession = Sessions.FirstOrDefault(card => card.FilesKey == selected?.FilesKey)
            ?? Sessions.FirstOrDefault(card => card.SessionId == selected?.SessionId)
            ?? Sessions.FirstOrDefault();
    }

    /// <summary>주어진 목록에서 지금 쓰는 방식을 찾는다. 사용자 프리셋은 파일로, 내장 프리셋은 이름으로 찾고, 없으면 뒤에 더한다.</summary>
    private static (IReadOnlyList<PresetEntry> Presets, PresetEntry Selected) PresetChoices(IReadOnlyList<PresetEntry> presets,
        ImportSettings settings)
    {
        var found = presets.FirstOrDefault(entry => settings.PresetFile is { } file
            ? entry.FilePath is not null && string.Equals(FullPath(entry.FilePath), FullPath(file), StringComparison.OrdinalIgnoreCase)
            : entry.FilePath is null && entry.Name == settings.Preset.Name);
        if (found is not null)
        {
            return (presets, found);
        }

        var added = new PresetEntry(settings.Preset.Name, settings.Preset, "", settings.PresetFile);
        return ([.. presets, added], added);
    }

    /// <summary>
    /// "이 이름 다시 쓰지 않기": 판별기가 읽은 이름을 제외 목록 파일에 한 줄 덧붙이고(앱 자체 파일만 쓴다), 같은 이름이 든 카드를 모두 비운 뒤
    /// 계획을 한 번만 다시 세운다. 형식에서 읽은 이름은 제외 목록을 따르지 않으므로 대상이 아니고, 그 카드는 비우지 않는다.
    /// 쓰지 못하면 오류를 알리고 이름은 그대로 둔다. 이름의 <c>\</c>는 읽을 때 <c>_</c>를 나눈 것이므로 <c>_</c>로 되돌려 적는다.
    /// </summary>
    private void OnNeverUseName(SessionItemViewModel card)
    {
        if (!Sessions.Contains(card) || !card.CanNeverUseName || card.SuggestedEventName is not { } name)
        {
            return;
        }

        try
        {
            IgnoredFolderNames.Add(_fileSystem, _ignoredNamesPath, name.Replace('\\', '_'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("제외할 폴더 이름을 저장하지 못했습니다", ex);
            _dialogs.ShowError(NeverUseNameFailedTitle, NeverUseNameFailedMessage);
            return;
        }

        // 같은 이름(앞뒤 공백·대소문자 무시)을 판별기에서 읽어 온 채로 둔 카드만 비운다. 사용자가 직접 쓰거나 고친 이름은 그대로 둔다.
        var target = name.Trim();
        foreach (var other in Sessions.Where(s => !s.IsReviewGroup && s.CanNeverUseName
                     && string.Equals(s.SuggestedEventName?.Trim(), target, StringComparison.OrdinalIgnoreCase)))
        {
            other.ClearEventNameSilently();
            _edits[other.FilesKey] = "";
        }

        RequestReplan(_replanDelay);
    }

    /// <summary>
    /// 새 세대 번호로 계획을 요청한다. 앞선 요청은 취소하고, 앞선 요청의 결과가 늦게 와도 버린다.
    /// 지금 보이는 카드가 지금 값으로 만든 것이 아니면(옵션을 바꿨는데 아직 반영되지 않았으면) 이 요청이 촬영 묶기부터 다시 한다.
    /// 이벤트 이름만 고쳤을 때도 마찬가지여서, 앞선 옵션 변경의 결과를 버려도 그 옵션이 빠지지 않는다.
    /// </summary>
    private void RequestReplan(TimeSpan delay)
    {
        if (_replanCts is { } previous)
        {
            try
            {
                previous.Cancel();
            }
            catch (AggregateException ex)
            {
                _log.Error("계획 취소 중 오류", ex);
            }

            previous.Dispose();
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _replanCts = cts;
        var generation = ++_generation;
        IsReplanning = true;
        ExecuteCommand.NotifyCanExecuteChanged();

        // 값과 고친 이름은 지금(화면 스레드에서) 읽어 둔다. 이 뒤에 값이나 이름이 또 바뀌면 새 요청이 생겨 이 결과는 버려진다.
        var settings = _settings;
        var grouping = settings == _shown.Settings ? _shown : null;
        var edits = new Dictionary<string, string>(_edits, StringComparer.Ordinal);

        // 앞선 요청의 계획 세우기가 끝난 뒤에야 이 요청의 계획을 세운다. 이 요청이 계획을 세우지 못하고 취소되어도,
        // 다음 요청은 앞선 계획 세우기가 끝나기를 기다린다.
        var previousBuild = _lastBuild;
        var buildDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastBuild = buildDone.Task;

        WhenPlanned = ReplanAsync(generation, settings, grouping, edits, delay, previousBuild, buildDone, cts.Token);
    }

    /// <param name="grouping">다시 쓸 촬영 묶음. null이면 <paramref name="settings"/>로 다시 묶는다.</param>
    private async Task ReplanAsync(int generation, ImportSettings settings, Grouping? grouping,
        IReadOnlyDictionary<string, string> edits, TimeSpan delay, Task previousBuild, TaskCompletionSource buildDone, CancellationToken ct)
    {
        var ownBuild = previousBuild;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            await WaitQuietlyAsync(previousBuild, ct);
            ct.ThrowIfCancellationRequested();
            var fileSystem = _fileSystem;
            var build = Task.Run(() =>
            {
                var grouped = grouping ?? Grouping.Create(settings, _regroup(settings, _scan));
                return (Grouped: grouped, View: PlanView.Create(_buildPlan(settings, grouped.Scan, grouped.EventNames(edits)), fileSystem));
            }, CancellationToken.None);
            ownBuild = build;

            // 계획 세우기는 중간에 멈출 수 없다. 취소되면 기다리지 않고 결과만 버린다. 버린 작업의 오류도 관찰해 둔다.
            _ = build.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var (grouped, view) = await build.WaitAsync(ct);

            if (generation == _generation && !ct.IsCancellationRequested)
            {
                Apply(generation, grouped, view);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 더 새 요청이 왔거나 화면을 떠났다.
        }
        catch (Exception ex) when (generation == _generation)
        {
            _log.Error("계획을 세우지 못했습니다", ex);
            ApplyFailure();
        }
        catch (Exception)
        {
            // 더 새 요청이 있으므로 이 요청의 오류는 의미가 없다.
        }
        finally
        {
            _ = CompleteAfterAsync(ownBuild, buildDone);
            if (generation == _generation)
            {
                IsReplanning = false;
                BusyEnded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>앞선 계획 세우기가 끝나기를 기다린다. 그 작업의 오류는 그 요청의 몫이므로 여기서는 무시한다. 취소되면 던진다.</summary>
    private static async Task WaitQuietlyAsync(Task task, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 앞선 요청의 실패는 그 요청이 다뤘다(또는 버려졌다).
        }
    }

    /// <summary><paramref name="task"/>가 끝나면(성공·실패 상관없이) <paramref name="done"/>을 완료한다.</summary>
    private static async Task CompleteAfterAsync(Task task, TaskCompletionSource done)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 오류는 계획을 요청한 쪽이 다룬다. 여기서는 끝났다는 것만 알린다.
        }
        finally
        {
            done.TrySetResult();
        }
    }

    /// <summary>
    /// 백그라운드에서 정리해 둔 계획을 화면에 반영한다. 화면 스레드에서는 값과 목록을 바꿔 끼우기만 한다. 촬영을 다시 묶었으면 카드도 함께 바꾸되,
    /// 카드 내용이 그대로면 입력 칸의 포커스·스크롤·썸네일을 지키려고 카드 객체를 그대로 둔다.
    /// </summary>
    private void Apply(int generation, Grouping grouped, PlanView view)
    {
        _plan = view.Outcome.Plan;
        _appliedGeneration = generation;
        _placementBySource = view.Placements;
        _skipBySource = view.Skips;

        if (!ReferenceEquals(grouped, _shown))
        {
            var sameCards = grouped.HasSameCards(_shown);
            _shown = grouped;

            // 세대가 같으면 요청한 뒤로 이름이 바뀌지 않았으므로 이 결과가 고친 이름을 다 반영했다. 파일 묶음이 사라진 이름은 여기서 버린다.
            foreach (var key in _edits.Keys.Where(key => !grouped.HasDateCard(key)).ToList())
            {
                _edits.Remove(key);
            }

            if (!sameCards)
            {
                ShowCards(grouped);
            }
        }

        DuplicateCount = view.Duplicates;
        KeptInPlaceCount = _settings.Kind == JobKind.Reorganize ? view.KeptInPlace : 0;
        TransferCount = view.Copies + view.Moves;
        TransferBytes = view.Bytes;
        TransferText = DescribeTransfer(view.Copies, view.Moves);
        _freeSpaceKnown = view.FreeBytes is not null;
        FreeBytes = view.FreeBytes ?? 0;
        OnPropertyChanged(nameof(FreeSpaceText));
        PlanNotes = view.Notes;
        BlockingReason = view.BlockingReason ?? (TransferCount != 0 ? null
            : _settings.Kind == JobKind.Reorganize ? NothingToMoveInPlaceMessage : NothingToTransferMessage);
        FolderTree = view.Tree;
        HasPlan = true;

        UpdateDetail();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFailure()
    {
        _plan = null;
        HasPlan = false;
        FolderTree = [];
        Detail = null;
        BlockingReason = PlanFailedMessage;
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>카드가 처음 그려질 때(썸네일을 처음 읽을 때) 대표 한 장을 요청한다. 보이지 않는 카드는 요청하지 않는다.</summary>
    private void RequestCardThumbnail(SessionItemViewModel card)
    {
        if (card.Files.Count > 0)
        {
            _ = LoadThumbnailAsync(card.Files[0], CardThumbnailSize, image => card.Thumbnail = image, _cardThumbnailCts.Token);
        }
    }

    private void UpdateDetail()
    {
        var path = SelectedThumbnail?.Path ?? SelectedSession?.Files.FirstOrDefault();
        if (_plan is null || path is null || !_files.TryGetValue(FullPath(path), out var entry))
        {
            Detail = null;
            return;
        }

        var (info, group) = entry;
        var reasons = ReviewDetector.Detect(group, _shown.Settings.Preset, _shown.Aliases);
        var branch = _shown.Renderer.SelectBranch(reasons);
        var key = FullPath(path);

        string destination;
        string? skipNote = null;
        if (_placementBySource.TryGetValue(key, out var op))
        {
            destination = op.Destination;
        }
        else if (_skipBySource.TryGetValue(key, out var skip))
        {
            destination = skip.Destination;
            skipNote = skip.Reason switch
            {
                SkipReason.AlreadyInPlace => "이미 제자리에 있어 건너뜁니다",
                SkipReason.DuplicateAtDestination => "보관 폴더에 같은 파일이 있어 건너뜁니다",
                SkipReason.DuplicateInPlan => "같은 파일을 이번에 이미 가져오므로 건너뜁니다",
                SkipReason.DuplicateInArchive => "이미 있음: " + skip.ExistingPath,
                SkipReason.ReviewKeptInPlace => "판단 필요라 제자리에 둡니다",
                SkipReason.SubfolderKeptInPlace => "하위 폴더라 제자리에 둡니다",
                SkipReason.OtherVolume => "다른 볼륨에 있어 옮기지 않습니다",
                _ => "건너뜁니다",
            };
        }
        else
        {
            destination = "";
        }

        Detail = new FileDetailViewModel(
            Path.GetFileName(info.Path),
            _shown.Aliases.Resolve(group.Primary) ?? "알 수 없음",
            // 촬영 시각이 없으면 한 문장으로 알리고 출처는 비운다.
            info.CaptureTime?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? NoCaptureTimeText,
            info.CaptureTime is null
                ? ""
                : info.CaptureSource switch
                {
                    CaptureTimeSource.Photo => "(사진 정보)",
                    CaptureTimeSource.Video => "(동영상 정보)",
                    CaptureTimeSource.FileName => "(파일 이름)",
                    _ => "",
                },
            destination,
            branch.Name,
            string.Join('\\', branch.Levels.Select(level => level.Template)),
            reasons.Select(ReviewReasonText.Label).ToList(),
            skipNote);
    }

    private async Task LoadThumbnailAsync(string path, int size, Action<ImageSource> apply, CancellationToken ct)
    {
        try
        {
            var image = await _thumbnails.GetAsync(path, size, ct);
            if (image is not null && !ct.IsCancellationRequested)
            {
                apply(image);
            }
        }
        catch (OperationCanceledException)
        {
            // 화면을 떠났거나 다른 촬영을 골랐다.
        }
        catch (Exception ex)
        {
            _log.Error("미리보기 그림을 만들지 못했습니다", ex);
        }
    }

    /// <summary>화면을 떠날 때 썸네일 요청과 남은 재계획을 멈춘다.</summary>
    private void Leave()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException ex)
        {
            _log.Error("미리보기 화면 정리 중 오류", ex);
        }
    }

    private void CancelAndReplace(ref CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (AggregateException ex)
        {
            _log.Error("썸네일 요청 취소 중 오류", ex);
        }

        cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
    }

    private static string DescribeTransfer(int copies, int moves) => (copies, moves) switch
    {
        (0, 0) => "옮길 파일 없음",
        (_, 0) => string.Create(CultureInfo.InvariantCulture, $"복사 {copies}개"),
        (0, _) => string.Create(CultureInfo.InvariantCulture, $"이동 {moves}개"),
        _ => string.Create(CultureInfo.InvariantCulture, $"복사 {copies}개 · 이동 {moves}개"),
    };

    /// <summary>스캔 오류 사유의 첫 부분(한국어)과 경로만 보인다. 사유 뒤에 붙은 예외 원문은 화면에 보이지 않는다.</summary>
    private static string DescribeScanError(ScanError error)
    {
        var separator = error.Message.IndexOf(": ", StringComparison.Ordinal);
        var reason = separator < 0 ? error.Message : error.Message[..separator];
        return reason + ": " + error.Path;
    }

    private static string DescribeIssue(PreflightIssue issue) =>
        issue.Path is null ? issue.Message : issue.Message + ": " + issue.Path;

    private static string? DescribeBlocking(IReadOnlyList<PreflightIssue> errors)
    {
        if (errors.Count == 0)
        {
            return null;
        }

        var lines = errors.Take(BlockingLineLimit).Select(DescribeIssue).ToList();
        if (errors.Count > BlockingLineLimit)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"외 {errors.Count - BlockingLineLimit}건"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatBytes(long bytes)
    {
        const double Kb = 1024;
        const double Mb = Kb * 1024;
        const double Gb = Mb * 1024;
        return bytes switch
        {
            < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / Kb:0.0} KB"),
            < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / Mb:0.0} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / Gb:0.0} GB"),
        };
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>카드 한 장의 내용. 백그라운드에서 만들고, 화면 스레드에서 이것으로 카드를 만든다.</summary>
    private sealed record CardSpec(string Id, string Title, string Devices, bool IsReview, IReadOnlyList<string> Files,
        string? SuggestedName, string? Note, bool SuggestedFromFormat = false)
    {
        public string FilesKey { get; } = SessionItemViewModel.KeyOf(Files);

        public bool HasSameContent(CardSpec other) =>
            this with { Files = other.Files } == other && Files.SequenceEqual(other.Files, StringComparer.Ordinal);
    }

    /// <summary>
    /// 어떤 값(<paramref name="Settings"/>)으로 묶은 촬영과 그 카드, 그 값의 프리셋을 따르는 부품. 처음에는 화면 스레드에서, 옵션을 바꾼 뒤에는
    /// 백그라운드에서 만든다. 만든 뒤에는 바뀌지 않는다.
    /// </summary>
    private sealed record Grouping(ImportSettings Settings, ScanOutcome Scan, IReadOnlyList<CardSpec> Cards, int ReviewCount,
        LayoutRenderer Renderer, DeviceAliasResolver Aliases)
    {
        /// <summary>
        /// 판단이 필요한 묶음은 날짜 카드에서 빼고 맨 끝 판단 필요 카드에만 보인다. 사유는 계획과 같은 함수(ReviewDetector)로
        /// 구하므로 계획 작업에 붙는 사유와 같다. 이벤트 이름과 관계없으므로 첫 계획을 기다리지 않고 두 갈래 카드를 함께 만든다.
        /// 중복이라 건너뛰는 파일도 사유가 있으면 판단 필요 카드에 보인다. 카드의 "N장"은 대표·딸린 파일을 모두 센다.
        /// </summary>
        public static Grouping Create(ImportSettings settings, ScanOutcome scan)
        {
            var aliases = new DeviceAliasResolver(settings.Preset.DeviceAliases);
            var cards = new List<CardSpec>();
            var reviewGroups = new List<FileGroup>();
            foreach (var session in scan.Sessions)
            {
                var dateGroups = new List<FileGroup>();
                foreach (var group in session.Groups)
                {
                    (ReviewDetector.Detect(group, settings.Preset, aliases).Count == 0 ? dateGroups : reviewGroups).Add(group);
                }

                if (dateGroups.Count > 0)
                {
                    cards.Add(new CardSpec(session.Id,
                        CardTitle(session.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FileCount(dateGroups)),
                        DevicesOf(dateGroups, aliases), IsReview: false, dateGroups.Select(g => g.Primary.Path).ToList(),
                        session.SuggestedEventName, Note: null, session.SuggestedFromFormat));
                }
            }

            // 위쪽 요약의 "판단 필요 N개"는 이 카드의 "N장"과 같은 수다(중복이라 건너뛰는 파일도 센다).
            var reviewCount = FileCount(reviewGroups);
            if (reviewGroups.Count > 0)
            {
                // 재정리에서 판단 필요 파일을 제자리에 두면 카드에도 그렇게 보인다. 옵션이 꺼져 있으면 보통의 판단 필요 카드다.
                var kept = settings.Kind == JobKind.Reorganize && settings.KeepReviewInPlace;
                cards.Add(new CardSpec(ReviewGroupId, CardTitle(ReviewGroupId, reviewCount) + (kept ? " (그대로 둠)" : ""),
                    DevicesOf(reviewGroups, aliases), IsReview: true, reviewGroups.Select(g => g.Primary.Path).ToList(),
                    SuggestedName: null, kept ? ReviewKeptNote : null));
            }

            return new Grouping(settings, scan, cards, reviewCount, new LayoutRenderer(settings.Preset), aliases);
        }

        /// <summary>
        /// 계획에 넘길 이벤트 이름. 같은 파일 묶음에 고친 이름이 있으면 그것, 없으면 읽어 온 이름이다. 읽어 온 이름을 지운 카드는 빈 문자열로 넘겨 이름 없음으로 굳힌다.
        /// </summary>
        public Dictionary<string, string> EventNames(IReadOnlyDictionary<string, string> edits)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var card in Cards.Where(card => !card.IsReview))
            {
                var name = edits.GetValueOrDefault(card.FilesKey) ?? card.SuggestedName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names[card.Id] = name;
                }
                else if (card.SuggestedName is not null)
                {
                    names[card.Id] = "";
                }
            }

            return names;
        }

        public bool HasSameCards(Grouping other) =>
            Cards.Count == other.Cards.Count && Cards.Zip(other.Cards).All(pair => pair.First.HasSameContent(pair.Second));

        public bool HasDateCard(string filesKey) => Cards.Any(card => !card.IsReview && card.FilesKey == filesKey);

        private static string DevicesOf(IEnumerable<FileGroup> groups, DeviceAliasResolver aliases) =>
            string.Join(", ", groups
                .Select(g => aliases.Resolve(g.Primary))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase));

        private static int FileCount(IEnumerable<FileGroup> groups) => groups.Sum(g => g.AllFiles.Count());

        private static string CardTitle(string label, int count) =>
            string.Create(CultureInfo.InvariantCulture, $"{label} · {count}장");
    }

    /// <summary>
    /// 계획을 화면에 보이기 좋게 정리한 것. 계획을 세운 백그라운드 작업 안에서 만든다(폴더 트리, 원본별 작업 찾기표,
    /// 개수, 여유 공간 조회). 만든 뒤에는 바뀌지 않는다.
    /// </summary>
    private sealed record PlanView(
        PlanOutcome Outcome,
        Dictionary<string, PlannedOperation> Placements,
        Dictionary<string, PlannedSkip> Skips,
        int Copies,
        int Moves,
        long Bytes,
        int Duplicates,
        int KeptInPlace,
        long? FreeBytes,
        IReadOnlyList<string> Notes,
        string? BlockingReason,
        IReadOnlyList<FolderNodeViewModel> Tree)
    {
        public static PlanView Create(PlanOutcome outcome, IFileSystem fileSystem)
        {
            var plan = outcome.Plan;

            // 자리 바꾸기에서는 원본을 먼저 임시 이름으로 옮긴 뒤 그 임시 파일을 최종 자리로 옮긴다. 원래 원본 경로로 찾게 한다.
            var originalOfTemp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var placements = new Dictionary<string, PlannedOperation>(StringComparer.OrdinalIgnoreCase);
            var copies = 0;
            var moves = 0;
            long bytes = 0;
            foreach (var op in plan.Operations)
            {
                if (op.Kind == OperationKind.CreateDirectory || op.Source is null)
                {
                    continue;
                }

                if (!FolderNodeViewModel.IsPlacement(op))
                {
                    originalOfTemp[op.Destination] = op.Source;
                    continue;
                }

                var source = originalOfTemp.GetValueOrDefault(op.Source, op.Source);
                placements[source] = op;
                if (op.Kind == OperationKind.Copy)
                {
                    copies++;
                }
                else
                {
                    moves++;
                }

                bytes += op.Length;
            }

            var skips = new Dictionary<string, PlannedSkip>(StringComparer.OrdinalIgnoreCase);
            foreach (var skip in plan.Skipped)
            {
                skips.TryAdd(FullPath(skip.Source), skip);
            }

            var notes = plan.Notes
                .Concat(outcome.Issues.Where(i => i.Severity == Severity.Warning).Select(DescribeIssue))
                .ToList();

            return new PlanView(outcome, placements, skips, copies, moves, bytes,
                plan.Skipped.Count(skip => skip.Reason is SkipReason.DuplicateAtDestination or SkipReason.DuplicateInPlan
                    or SkipReason.DuplicateInArchive),
                plan.Skipped.Count(skip => skip.Reason is SkipReason.AlreadyInPlace or SkipReason.ReviewKeptInPlace
                    or SkipReason.SubfolderKeptInPlace or SkipReason.OtherVolume),
                FreeSpace(fileSystem, plan.ArchiveRoot), notes,
                DescribeBlocking(outcome.Issues.Where(i => i.Severity == Severity.Error).ToList()),
                FolderNodeViewModel.Build(plan));
        }

        private static long? FreeSpace(IFileSystem fileSystem, string archiveRoot)
        {
            try
            {
                return fileSystem.GetAvailableFreeSpace(archiveRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }
}
