using PhotoOk.Core.History;

namespace PhotoOk.Tests.Fakes;

/// <summary>
/// 작업 기록 목록을 흉내 낸다. <see cref="Gate"/>가 있으면 열릴 때까지 기다리고(느린 기록, 취소하면 그 자리에서
/// <see cref="OperationCanceledException"/>), <see cref="Failure"/>가 있으면 던진다.
/// 부른 스레드와 취소로 끝난 횟수를 기록한다. 돌려주는 목록은 부른 순간의 것이다.
/// </summary>
public sealed class FakeRunHistory : IRunHistory
{
    private readonly List<int> _threads = [];
    private int _cancelled;

    public List<RunSummary> Runs { get; } = [];

    public List<HistoryError> Errors { get; } = [];

    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>읽기가 시작되면 켜진다.</summary>
    public ManualResetEventSlim Started { get; } = new();

    public Exception? Failure { get; set; }

    /// <summary>취소되어 끝난 읽기 수.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    public IReadOnlyList<int> CallingThreads
    {
        get
        {
            lock (_threads)
            {
                return [.. _threads];
            }
        }
    }

    public (IReadOnlyList<RunSummary> Runs, IReadOnlyList<HistoryError> Errors) List(CancellationToken ct = default)
    {
        lock (_threads)
        {
            _threads.Add(Environment.CurrentManagedThreadId);
        }

        // 목록은 부른 때의 것을 돌려준다(게이트에서 기다리는 사이에 바뀐 목록은 다음 호출의 것).
        var runs = Runs.ToList();
        var errors = Errors.ToList();
        Started.Set();
        try
        {
            if (Gate is { } gate && !gate.Wait(TimeSpan.FromSeconds(10), ct))
            {
                throw new TimeoutException("테스트 게이트가 열리지 않았습니다.");
            }

            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return (runs, errors);
    }
}
