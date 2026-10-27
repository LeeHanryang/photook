using System.Windows.Media;

namespace PhotoOk.Services;

/// <summary>파일의 작은 미리보기 그림.</summary>
public interface IThumbnailProvider
{
    /// <summary>
    /// 한 변이 <paramref name="size"/> 픽셀 안쪽인 미리보기. 어느 스레드에서나 쓸 수 있게 고정(Freeze)된 그림을 돌려준다.
    /// 만들 수 없거나 취소되면 예외 대신 null.
    /// </summary>
    Task<ImageSource?> GetAsync(string path, int size, CancellationToken ct);
}
