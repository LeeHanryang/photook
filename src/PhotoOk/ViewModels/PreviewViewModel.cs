using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoOk.Core.Execution;
using PhotoOk.Core.IO;
using PhotoOk.Core.Layout;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Planning;
using PhotoOk.Core.Presets;
using PhotoOk.Core.Scanning;
using PhotoOk.Core.Workflow;
using PhotoOk.Services;

namespace PhotoOk.ViewModels;

/// <summary>
/// 스캔 결과로 세운 계획을 보여 주는 화면: 요약, 촬영 카드(이벤트 이름 입력), 정리 후 폴더 트리, 선택 파일의 "왜 이 경로인가".
/// 이벤트 이름이 바뀌면 <c>replanDelay</c>만큼 기다렸다가 백그라운드에서 계획을 다시 세우고, 가장 나중 요청의 결과만 반영한다.
/// 계획을 세우는 동안(<see cref="IsReplanning"/>)과 실행 전 점검 오류가 있을 때는 실행할 수 없다. 디스크는 읽기만 한다.
/// 상태는 화면(UI) 스레드에서만 바꾼다.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject, IBusyViewModel
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
    private const string PlanFailedMessage = "계획을 세우지 못했습니다. 자세한 내용은 로그 폴더에 남겼습니다. 이벤트 이름을 고치거나 뒤로 가서 다시 시도해 주세요.";

    private readonly ShellViewModel _shell;
    private readonly ImportSettings _settings;
    private readonly ScanOutcome _scan;
    private readonly IFileSystem _fileSystem;
    private readonly IThumbnailProvider _thumbnails;
    private readonly IDialogService _dialogs;
    private readonly FileLog _log;
    private readonly Func<Plan, ScanFacts, ObservableObject> _createRun;
    private readonly Func<ObservableObject> _createBack;
    private readonly TimeSpan _replanDelay;
    private readonly Func<ImportSettings, ScanOutcome, IReadOnlyDictionary<string, string>, PlanOutcome> _buildPlan;
    private readonly LayoutRenderer _renderer;
    private readonly DeviceAliasResolver _aliases;

    /// <summary>스캔한 모든 파일(대표·딸린 파일)의 정보와 그 파일이 속한 묶음. 키는 전체 경로.</summary>
    private readonly Dictionary<string, (PhotoInfo Info, FileGroup Group)> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>화면을 떠나면 취소한다(썸네일 요청).</summary>
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource _thumbnailCts = new();
    private CancellationTokenSource? _replanCts;

    /// <summary>
    /// 가장 나중 요청의 계획 세우기가 끝나면 완료된다. 새 요청은 이것을 기다린 뒤에 계획을 세운다(스레드 풀에 계획 세우기가
    /// 여러 개 동시에 돌지 않게). 화면 스레드에서만 바꾼다.
    /// </summary>
    private Task _lastBuild = Task.CompletedTask;
    private int _generation;
    private int _appliedGeneration;
    private Plan? _plan;
    private Dictionary<string, PlannedOperation> _placementBySource = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PlannedSkip> _skipBySource = new(StringComparer.OrdinalIgnoreCase);

    private int _reviewCount;
    private int _duplicateCount;
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
    /// <param name="buildPlan">계획을 세우는 함수. 생략하면 <paramref name="workflow"/>의 것을 쓴다(스캔과 같은 인스턴스여야 해시를 다시 쓴다).</param>
    public PreviewViewModel(ShellViewModel shell, ImportWorkflow workflow, ImportSettings settings, ScanOutcome scan,
        IFileSystem fileSystem, IThumbnailProvider thumbnails, IDialogService dialogs, FileLog log,
        Func<Plan, ScanFacts, ObservableObject> createRun, Func<ObservableObject> createBack, TimeSpan replanDelay,
        Func<ImportSettings, ScanOutcome, IReadOnlyDictionary<string, string>, PlanOutcome>? buildPlan = null)
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
        _buildPlan = buildPlan ?? workflow.BuildPlan;
        _renderer = new LayoutRenderer(settings.Preset);
        _aliases = new DeviceAliasResolver(settings.Preset.DeviceAliases);

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

        // 판단이 필요한 묶음은 날짜 카드에서 빼고 맨 끝 판단 필요 카드에만 보인다. 사유는 계획과 같은 함수(ReviewDetector)로
        // 구하므로 계획 작업에 붙는 사유와 같다. 이벤트 이름과 관계없으므로 첫 계획을 기다리지 않고 두 갈래 카드를 함께 만든다.
        // 중복이라 건너뛰는 파일도 사유가 있으면 판단 필요 카드에 보인다. 카드의 "N장"은 대표·딸린 파일을 모두 센다.
        var reviewGroups = new List<FileGroup>();
        foreach (var session in scan.Sessions)
        {
            var dateGroups = new List<FileGroup>();
            foreach (var group in session.Groups)
            {
                (ReviewDetector.Detect(group, settings.Preset, _aliases).Count == 0 ? dateGroups : reviewGroups).Add(group);
            }

            if (dateGroups.Count > 0)
            {
                Sessions.Add(new SessionItemViewModel(session.Id, CardTitle(session.Id, FileCount(dateGroups)),
                    DevicesOf(dateGroups), isReviewGroup: false, dateGroups.Select(g => g.Primary.Path).ToList(), OnEventNameChanged,
                    RequestCardThumbnail));
            }
        }

        if (reviewGroups.Count > 0)
        {
            Sessions.Add(new SessionItemViewModel(ReviewGroupId, CardTitle(ReviewGroupId, FileCount(reviewGroups)),
                DevicesOf(reviewGroups), isReviewGroup: true, reviewGroups.Select(g => g.Primary.Path).ToList(),
                eventNameChanged: null, RequestCardThumbnail));
        }

        SelectedSession = Sessions.FirstOrDefault();
        RequestReplan(TimeSpan.Zero);
    }

    public event EventHandler? BusyEnded;

    /// <summary>날짜순 촬영 카드, 그 뒤에 판단 필요 묶음(있을 때).</summary>
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

    /// <summary>판단 필요 분기로 가는 파일 수.</summary>
    public int ReviewCount
    {
        get => _reviewCount;
        private set => SetProperty(ref _reviewCount, value);
    }

    /// <summary>같은 파일이 이미 있어 건너뛰는 파일 수(보관 폴더에 있거나 이번 가져오기 안에서 겹침).</summary>
    public int DuplicateCount
    {
        get => _duplicateCount;
        private set => SetProperty(ref _duplicateCount, value);
    }

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

    /// <summary>계획을 다시 세우는 중이면 true. 이때는 실행할 수 없다.</summary>
    public bool IsReplanning
    {
        get => _isReplanning;
        private set
        {
            if (SetProperty(ref _isReplanning, value))
            {
                ExecuteCommand.NotifyCanExecuteChanged();
            }
        }
    }

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

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private Task Execute()
    {
        // 명령의 Execute는 CanExecute를 확인하지 않으므로 여기서도 막는다.
        if (!CanExecute() || _plan is not { } plan)
        {
            return Task.CompletedTask;
        }

        if (_scan.Errors.Count > 0)
        {
            var message = string.Create(CultureInfo.InvariantCulture,
                $"읽지 못한 폴더나 파일 {_scan.Errors.Count}건이 있습니다. 그래도 실행할까요?");
            if (!_dialogs.Confirm(ConfirmTitle, message))
            {
                return Task.CompletedTask;
            }
        }

        Leave();
        _shell.Navigate(_createRun(plan, ScanFacts.From(_scan)));
        return Task.CompletedTask;
    }

    private bool CanExecute() =>
        _plan is not null && !IsReplanning && _appliedGeneration == _generation && BlockingReason is null;

    [RelayCommand]
    private void Back()
    {
        Cancel();
        Leave();
        _shell.Navigate(_createBack());
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

    private void OnEventNameChanged(SessionItemViewModel card) => RequestReplan(_replanDelay);

    /// <summary>
    /// 새 세대 번호로 계획을 요청한다. 앞선 요청은 취소하고, 앞선 요청의 결과가 늦게 와도 버린다.
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

        // 이름은 지금(화면 스레드에서) 읽어 둔다.
        var eventNames = Sessions
            .Where(card => !card.IsReviewGroup && !string.IsNullOrWhiteSpace(card.EventName))
            .ToDictionary(card => card.SessionId, card => card.EventName, StringComparer.Ordinal);

        // 앞선 요청의 계획 세우기가 끝난 뒤에야 이 요청의 계획을 세운다. 이 요청이 계획을 세우지 못하고 취소되어도,
        // 다음 요청은 앞선 계획 세우기가 끝나기를 기다린다.
        var previousBuild = _lastBuild;
        var buildDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastBuild = buildDone.Task;

        WhenPlanned = ReplanAsync(generation, eventNames, delay, previousBuild, buildDone, cts.Token);
    }

    private async Task ReplanAsync(int generation, IReadOnlyDictionary<string, string> eventNames, TimeSpan delay,
        Task previousBuild, TaskCompletionSource buildDone, CancellationToken ct)
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
            var build = Task.Run(() => PlanView.Create(_buildPlan(_settings, _scan, eventNames), fileSystem),
                CancellationToken.None);
            ownBuild = build;

            // 계획 세우기는 중간에 멈출 수 없다. 취소되면 기다리지 않고 결과만 버린다. 버린 작업의 오류도 관찰해 둔다.
            _ = build.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var view = await build.WaitAsync(ct);

            if (generation == _generation && !ct.IsCancellationRequested)
            {
                Apply(generation, view);
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

    /// <summary>백그라운드에서 정리해 둔 계획을 화면에 반영한다. 화면 스레드에서는 값과 목록을 바꿔 끼우기만 한다.</summary>
    private void Apply(int generation, PlanView view)
    {
        _plan = view.Outcome.Plan;
        _appliedGeneration = generation;
        _placementBySource = view.Placements;
        _skipBySource = view.Skips;

        ReviewCount = view.ReviewSources.Count;
        DuplicateCount = view.Duplicates;
        TransferCount = view.Copies + view.Moves;
        TransferBytes = view.Bytes;
        TransferText = DescribeTransfer(view.Copies, view.Moves);
        _freeSpaceKnown = view.FreeBytes is not null;
        FreeBytes = view.FreeBytes ?? 0;
        OnPropertyChanged(nameof(FreeSpaceText));
        PlanNotes = view.Notes;
        BlockingReason = view.BlockingReason ?? (TransferCount == 0 ? NothingToTransferMessage : null);
        FolderTree = view.Tree;

        UpdateDetail();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFailure()
    {
        _plan = null;
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
            _ = LoadThumbnailAsync(card.Files[0], CardThumbnailSize, image => card.Thumbnail = image, _lifetime.Token);
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
        var reasons = ReviewDetector.Detect(group, _settings.Preset, _aliases);
        var branch = _renderer.SelectBranch(reasons);
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
                _ => "건너뜁니다",
            };
        }
        else
        {
            destination = "";
        }

        Detail = new FileDetailViewModel(
            Path.GetFileName(info.Path),
            _aliases.Resolve(group.Primary) ?? "알 수 없음",
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

    private string DevicesOf(IEnumerable<FileGroup> groups) =>
        string.Join(", ", groups
            .Select(g => _aliases.Resolve(g.Primary))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static int FileCount(IEnumerable<FileGroup> groups) => groups.Sum(g => g.AllFiles.Count());

    private static string CardTitle(string label, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{label} · {count}장");

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

    /// <summary>
    /// 계획을 화면에 보이기 좋게 정리한 것. 계획을 세운 백그라운드 작업 안에서 만든다(폴더 트리, 원본별 작업 찾기표,
    /// 개수, 여유 공간 조회). 만든 뒤에는 바뀌지 않는다.
    /// </summary>
    private sealed record PlanView(
        PlanOutcome Outcome,
        Dictionary<string, PlannedOperation> Placements,
        Dictionary<string, PlannedSkip> Skips,
        IReadOnlyList<string> ReviewSources,
        int Copies,
        int Moves,
        long Bytes,
        int Duplicates,
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
            var reviewSources = new List<string>();
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
                if (op.Reasons.Count > 0)
                {
                    reviewSources.Add(source);
                }
            }

            var skips = new Dictionary<string, PlannedSkip>(StringComparer.OrdinalIgnoreCase);
            foreach (var skip in plan.Skipped)
            {
                skips.TryAdd(FullPath(skip.Source), skip);
            }

            var notes = plan.Notes
                .Concat(outcome.Issues.Where(i => i.Severity == Severity.Warning).Select(DescribeIssue))
                .ToList();

            return new PlanView(outcome, placements, skips, reviewSources, copies, moves, bytes,
                plan.Skipped.Count(skip => skip.Reason is SkipReason.DuplicateAtDestination or SkipReason.DuplicateInPlan),
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
