namespace PhotoOk.Tests.Fakes;

/// <summary>
/// 보고를 받은 즉시 같은 스레드에서 목록에 더한다. <see cref="Progress{T}"/>는 동기화 컨텍스트로 넘겨 나중에 부르므로
/// 테스트에서 순서와 횟수를 확인할 수 없다.
/// </summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value) => Reports.Add(value);
}
