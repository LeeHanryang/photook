namespace PhotoOk.Core.IO;

/// <summary>
/// 연결 폴더(바로가기·정션 같은 재분석 지점)라 들어가지 않고 건너뛰었다는 알림.
/// 연결 폴더를 따라가면 같은 사진을 두 번 세거나 순환에 빠질 수 있다.
/// </summary>
public sealed class LinkedFolderSkippedException(string path) : IOException(MessagePrefix + path)
{
    /// <summary>알림 문구의 앞부분. 스캔 오류 목록도 같은 문구를 쓴다.</summary>
    public const string MessagePrefix = "연결 폴더라 건너뛰었습니다: ";
}
