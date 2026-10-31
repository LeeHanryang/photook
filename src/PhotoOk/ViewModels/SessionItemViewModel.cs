using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PhotoOk.ViewModels;

/// <summary>
/// 미리보기 왼쪽의 촬영 카드 하나. 하루 촬영이거나, 맨 끝의 "판단 필요" 묶음이다.
/// 이벤트 이름을 바꾸면 생성자로 받은 알림으로 부모 화면에 재계획을 요청한다.
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    private readonly Action<SessionItemViewModel>? _eventNameChanged;
    private readonly Action<SessionItemViewModel>? _thumbnailNeeded;
    private readonly Action<SessionItemViewModel>? _neverUseName;
    private readonly Action<SessionItemViewModel>? _includeToggled;
    private readonly bool _suggestedFromFormat;
    private int _excludedGroups;
    private int _excludedFiles;
    private int? _movableGroups;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;
    private bool _silent;
    private string? _filesKey;

    [ObservableProperty]
    private string eventName = "";

    /// <param name="suggestedEventName">폴더 이름에서 읽은 이름. 있으면 이벤트 칸을 이 이름으로 채워 시작한다(알림은 부르지 않는다).</param>
    /// <param name="files">카드에 속한 사진(묶음의 대표 파일) 경로. 촬영 시각 순서.</param>
    /// <param name="eventNameChanged">이벤트 이름이 바뀌었을 때 부를 알림. 판단 필요 묶음이면 null.</param>
    /// <param name="thumbnailNeeded">
    /// <see cref="Thumbnail"/>을 처음 읽을 때 한 번 부른다. 화면은 실제로 그려지는 카드만 읽으므로(가상화)
    /// 보이지 않는 카드의 썸네일은 요청하지 않는다. 결과는 <see cref="Thumbnail"/>에 넣어 준다.
    /// </param>
    /// <param name="neverUseName">"이 이름 다시 쓰지 않기"를 눌렀을 때 부를 알림.</param>
    /// <param name="note">제목 아래에 보일 안내(재정리의 판단 필요 카드). 없으면 null.</param>
    /// <param name="suggestedFromFormat">읽어 온 이름이 프리셋 형식에서 왔다. 제외 목록을 따르지 않는 이름이라 "이 이름 다시 쓰지 않기"를 보이지 않는다.</param>
    /// <param name="includeToggled">"이번에 옮기기" 체크박스를 눌렀을 때 부를 알림. 뺀 상태는 부모가 파일 묶음마다 기억한다.</param>
    /// <param name="dateMismatch">폴더 날짜와 사진 날짜가 다르다는 안내. 없으면 null.</param>
    /// <param name="showIncludeCheckBox">"이번에 옮기기" 체크박스를 보인다. 옵션 때문에 어차피 제자리인 판단 필요 카드에는 보이지 않는다.</param>
    public SessionItemViewModel(string sessionId, string title, string devices, bool isReviewGroup,
        IReadOnlyList<string> files, string? suggestedEventName, Action<SessionItemViewModel>? eventNameChanged,
        Action<SessionItemViewModel>? thumbnailNeeded = null, Action<SessionItemViewModel>? neverUseName = null,
        string? note = null, bool suggestedFromFormat = false, Action<SessionItemViewModel>? includeToggled = null,
        string? dateMismatch = null, bool showIncludeCheckBox = true)
    {
        ShowIncludeCheckBox = showIncludeCheckBox;
        _suggestedFromFormat = suggestedFromFormat;
        _includeToggled = includeToggled;
        DateMismatch = dateMismatch;
        SessionId = sessionId;
        Title = title;
        Devices = devices;
        IsReviewGroup = isReviewGroup;
        Files = files;
        _eventNameChanged = eventNameChanged;
        _thumbnailNeeded = thumbnailNeeded;
        _neverUseName = neverUseName;
        Note = note;
        SuggestedEventName = string.IsNullOrWhiteSpace(suggestedEventName) || isReviewGroup ? null : suggestedEventName;

        // 속성이 아니라 필드에 바로 넣는다: 처음 채우는 것은 사용자가 바꾼 것이 아니므로 재계획을 부르지 않는다.
        eventName = SuggestedEventName ?? "";
    }

    /// <summary>하루 촬영이면 시작일 <c>yyyy-MM-dd</c>.</summary>
    public string SessionId { get; }

    /// <summary>폴더 이름에서 읽은 이름. 없으면 null. 카드를 만들 때 정해지고 바뀌지 않는다.</summary>
    public string? SuggestedEventName { get; }

    /// <summary>이벤트 칸이 읽어 온 이름 그대로이면 true("폴더 이름에서 읽음"을 보인다).</summary>
    public bool NameFromFolder => SuggestedEventName is not null && string.Equals(EventName, SuggestedEventName, StringComparison.Ordinal);

    /// <summary>"이 이름 다시 쓰지 않기"를 보인다: 판별기가 읽은 이름 그대로일 때만. 형식에서 읽은 이름은 제외 목록을 따르지 않으므로 없다.</summary>
    public bool CanNeverUseName => NameFromFolder && !_suggestedFromFormat;

    /// <summary>사용자가 이름을 고쳤으면 true(읽어 온 이름과 다르거나, 읽어 온 이름이 없는데 뭔가 썼다).</summary>
    internal bool IsEdited => !string.Equals(EventName, SuggestedEventName ?? "", StringComparison.Ordinal);

    /// <summary>이 카드의 파일 묶음(<see cref="KeyOf"/>). 옵션을 바꿔 촬영 Id가 달라져도 파일이 같으면 같은 값이다.</summary>
    internal string FilesKey => _filesKey ??= KeyOf(Files);

    /// <summary>파일 묶음을 가리키는 값. 경로의 순서와 대소문자를 가리지 않는다.</summary>
    internal static string KeyOf(IEnumerable<string> files) =>
        string.Join('\n', files.Select(file => file.ToUpperInvariant()).Order(StringComparer.Ordinal));

    /// <summary>제목 아래 안내. 없으면 null.</summary>
    public string? Note { get; }

    /// <summary>예: "2026-10-14 · 128장".</summary>
    public string Title { get; }

    /// <summary>기기 이름들을 ", "로 이은 것. 모르면 빈 문자열.</summary>
    public string Devices { get; }

    /// <summary>판단 필요 묶음이면 true(이벤트 이름 입력 없음).</summary>
    public bool IsReviewGroup { get; }

    public IReadOnlyList<string> Files { get; }

    /// <summary>폴더 날짜와 사진 날짜가 다르다는 안내. 없으면 null.</summary>
    public string? DateMismatch { get; }

    /// <summary>"이번에 옮기기" 체크박스를 보이는가.</summary>
    public bool ShowIncludeCheckBox { get; }

    /// <summary>
    /// 이번에 옮기는가: 옮길 수 있는 묶음을 모두 옮기면 true, 모두 뺐으면 false, 일부만 뺐으면 null.
    /// 체크박스가 바꾼 값(마우스, 자동화 Toggle 모두)이 이 설정자로 온다. 어느 쪽으로 바꿀지는 받은 값이 아니라 부모가 지금 상태를 보고 정한다
    /// (일부일 때 누르면 체크박스는 꺼짐을 보내지만 모두 포함한다).
    /// </summary>
    public bool? IsIncluded
    {
        get => _excludedGroups == 0 ? true : _excludedGroups >= (_movableGroups ?? Files.Count) ? false : null;
        set
        {
            if (value != IsIncluded)
            {
                ToggleIncluded();
            }
        }
    }

    /// <summary>체크박스 옆 글자. 일부만 뺐으면 "일부".</summary>
    public string IncludeLabel => IsIncluded is null ? "일부" : "이번에 옮기기";

    /// <summary>체크박스가 보이는 카드에서 일부만 뺐으면 "뺀 파일 N개". 아니면 null.</summary>
    public string? ExcludedNote => ShowIncludeCheckBox && IsIncluded is null
        ? string.Create(CultureInfo.InvariantCulture, $"뺀 파일 {_excludedFiles}개")
        : null;

    /// <summary>첫 파일의 썸네일. 처음 읽을 때 비동기로 요청하고, 오기 전까지는 null.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                _thumbnailNeeded?.Invoke(this);
            }

            return _thumbnail;
        }
        set => SetProperty(ref _thumbnail, value);
    }

    [RelayCommand]
    private void NeverUseName() => _neverUseName?.Invoke(this);

    /// <summary>체크박스를 눌렀다. 어느 쪽으로 바꿀지는 부모가 지금 상태를 보고 정한다.</summary>
    [RelayCommand]
    private void ToggleIncluded() => _includeToggled?.Invoke(this);

    /// <summary>이 카드에서 뺀 묶음 수와 그 파일 수(대표·딸린 파일), 옮길 수 있는 묶음 수(모르면 카드의 모든 묶음)를 알린다.</summary>
    internal void SetExclusion(int excludedGroups, int excludedFiles, int? movableGroups = null)
    {
        _excludedGroups = excludedGroups;
        _excludedFiles = excludedFiles;
        _movableGroups = movableGroups;

        // 체크박스가 눌리며 스스로 값을 바꿔 두므로, 값이 같아도 늘 다시 읽게 한다.
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(IncludeLabel));
        OnPropertyChanged(nameof(ExcludedNote));
    }

    /// <summary>알림 없이 이벤트 칸을 비운다(여러 카드를 비운 뒤 재계획을 한 번만 하려고 부모가 쓴다).</summary>
    internal void ClearEventNameSilently() => SetEventNameSilently("");

    /// <summary>알림 없이 이벤트 칸을 채운다(카드를 다시 만들 때 고친 이름을 되살리려고 부모가 쓴다).</summary>
    internal void SetEventNameSilently(string name)
    {
        _silent = true;
        try
        {
            EventName = name;
        }
        finally
        {
            _silent = false;
        }
    }

    partial void OnEventNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameFromFolder));
        OnPropertyChanged(nameof(CanNeverUseName));
        if (_silent)
        {
            return;
        }

        if (!IsReviewGroup)
        {
            _eventNameChanged?.Invoke(this);
        }
    }
}
