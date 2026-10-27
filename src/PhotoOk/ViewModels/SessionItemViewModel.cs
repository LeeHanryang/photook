using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoOk.ViewModels;

/// <summary>
/// 미리보기 왼쪽의 촬영 카드 하나. 하루 촬영이거나, 맨 끝의 "판단 필요" 묶음이다.
/// 이벤트 이름을 바꾸면 생성자로 받은 알림으로 부모 화면에 재계획을 요청한다.
/// </summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    private readonly Action<SessionItemViewModel>? _eventNameChanged;
    private readonly Action<SessionItemViewModel>? _thumbnailNeeded;
    private ImageSource? _thumbnail;
    private bool _thumbnailRequested;

    [ObservableProperty]
    private string eventName = "";

    /// <param name="files">카드에 속한 사진(묶음의 대표 파일) 경로. 촬영 시각 순서.</param>
    /// <param name="eventNameChanged">이벤트 이름이 바뀌었을 때 부를 알림. 판단 필요 묶음이면 null.</param>
    /// <param name="thumbnailNeeded">
    /// <see cref="Thumbnail"/>을 처음 읽을 때 한 번 부른다. 화면은 실제로 그려지는 카드만 읽으므로(가상화)
    /// 보이지 않는 카드의 썸네일은 요청하지 않는다. 결과는 <see cref="Thumbnail"/>에 넣어 준다.
    /// </param>
    public SessionItemViewModel(string sessionId, string title, string devices, bool isReviewGroup,
        IReadOnlyList<string> files, Action<SessionItemViewModel>? eventNameChanged,
        Action<SessionItemViewModel>? thumbnailNeeded = null)
    {
        SessionId = sessionId;
        Title = title;
        Devices = devices;
        IsReviewGroup = isReviewGroup;
        Files = files;
        _eventNameChanged = eventNameChanged;
        _thumbnailNeeded = thumbnailNeeded;
    }

    /// <summary>하루 촬영이면 시작일 <c>yyyy-MM-dd</c>.</summary>
    public string SessionId { get; }

    /// <summary>예: "2026-10-14 · 128장".</summary>
    public string Title { get; }

    /// <summary>기기 이름들을 ", "로 이은 것. 모르면 빈 문자열.</summary>
    public string Devices { get; }

    /// <summary>판단 필요 묶음이면 true(이벤트 이름 입력 없음).</summary>
    public bool IsReviewGroup { get; }

    public IReadOnlyList<string> Files { get; }

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

    partial void OnEventNameChanged(string value)
    {
        if (!IsReviewGroup)
        {
            _eventNameChanged?.Invoke(this);
        }
    }
}
