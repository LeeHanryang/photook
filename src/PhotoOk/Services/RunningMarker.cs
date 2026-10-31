using System.IO;
using System.Threading;

namespace PhotoOk.Services;

/// <summary>
/// 앱이 실행 중이라는 표시. 설치 파일이 이 이름의 뮤텍스로 실행 중인지 알아보고 설치와 제거를 막는다(파일을 옮기는 도중일 수 있다).
/// 소유하지 않고 열어 두기만 하므로 앱을 둘 띄워도 된다. 앱이 끝나면 운영체제가 치운다.
/// </summary>
public static class RunningMarker
{
    public const string MutexName = @"Local\PhotoOkRunning";

    /// <summary>표시를 만들어 돌려준다. 만들지 못하면 null: 설치 파일의 다른 확인 방법이 남아 있으므로 앱 시작을 막지 않는다.</summary>
    public static Mutex? TryHold(string name = MutexName)
    {
        try
        {
            return new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or ArgumentException)
        {
            return null;
        }
    }
}
