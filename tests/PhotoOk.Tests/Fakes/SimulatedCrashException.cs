using System.IO;
namespace PhotoOk.Tests.Fakes;

/// <summary>
/// <see cref="FakeFileSystem.CrashAfter"/>가 던지는, 프로세스가 갑자기 죽은 상황의 모의 예외.
/// <see cref="IOException"/>이 아니므로 작업별 오류 처리에 잡히지 않고 호출자까지 전파된다.
/// </summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("모의 크래시: 프로세스가 이 시점에서 종료된 것으로 간주한다.")
    {
    }
}
