using PhotoOk.Core.IO;

namespace PhotoOk.Tests.Fakes;

/// <summary>정해 둔 이동식 드라이브 목록을 돌려준다. 목록은 테스트가 바꿀 수 있다. <see cref="Failure"/>가 있으면 던진다.</summary>
public sealed class FakeDrives : IRemovableDrives
{
    public List<DriveChoice> Choices { get; } = [];

    public int ListCalls { get; private set; }

    public Exception? Failure { get; set; }

    public IReadOnlyList<DriveChoice> List()
    {
        ListCalls++;
        if (Failure is { } failure)
        {
            throw failure;
        }

        return [.. Choices];
    }
}
