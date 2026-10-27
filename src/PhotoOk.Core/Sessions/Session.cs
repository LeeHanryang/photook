using PhotoOk.Core.Scanning;

namespace PhotoOk.Core.Sessions;

/// <summary>하루 단위 촬영 묶음. <paramref name="Id"/>는 시작일 <c>yyyy-MM-dd</c>이다.</summary>
public sealed record Session(string Id, DateOnly Start, DateOnly End, IReadOnlyList<FileGroup> Groups);
