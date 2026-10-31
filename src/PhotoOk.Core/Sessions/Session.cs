using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Sessions;

/// <summary>
/// 하루 단위 촬영 묶음. <paramref name="Id"/>는 이름이 없으면 시작일 <c>yyyy-MM-dd</c>, 있으면 <c>yyyy-MM-dd|이름</c>이다.
/// <paramref name="SuggestedEventName"/>은 폴더 이름에서 읽은 이벤트 이름이다.
/// <paramref name="SuggestedFromFormat"/>은 그 이름을 프리셋 형식에서 읽은 파일이 하나라도 있으면 true다(제외 목록을 따르지 않는 이름).
/// </summary>
public sealed record Session(string Id, DateOnly Start, DateOnly End, IReadOnlyList<FileGroup> Groups, string? SuggestedEventName = null,
    bool SuggestedFromFormat = false);
