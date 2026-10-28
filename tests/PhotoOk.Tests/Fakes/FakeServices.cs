using System.IO;
using System.Windows.Media;
using PhotoOk.Services;

namespace PhotoOk.Tests.Fakes;

/// <summary>대기열에 넣어 둔 경로를 차례로 돌려준다. 대기열이 비면 취소(null)로 본다.</summary>
public sealed class FakeFolderPicker : IFolderPicker
{
    public Queue<string?> Answers { get; } = new();

    public List<(string Title, string? InitialFolder)> Calls { get; } = [];

    public string? PickFolder(string title, string? initialFolder)
    {
        Calls.Add((title, initialFolder));
        return Answers.Count > 0 ? Answers.Dequeue() : null;
    }
}

/// <summary>대기열에 넣어 둔 파일 경로를 차례로 돌려준다. 대기열이 비면 취소(null)로 본다.</summary>
public sealed class FakeFileOpenPicker : IFileOpenPicker
{
    public Queue<string?> Answers { get; } = new();

    public List<(string Title, string Filter)> Calls { get; } = [];

    public string? PickFile(string title, string filter)
    {
        Calls.Add((title, filter));
        return Answers.Count > 0 ? Answers.Dequeue() : null;
    }
}

/// <summary>Confirm 응답을 미리 정해 두고, 받은 메시지를 모두 기록한다.</summary>
public sealed class FakeDialogService : IDialogService
{
    /// <summary>Confirm이 차례로 돌려줄 응답. 비어 있는데 Confirm이 불리면 테스트가 실패하도록 예외를 던진다.</summary>
    public Queue<bool> ConfirmAnswers { get; } = new();

    public List<(string Title, string Message)> Confirms { get; } = [];

    public List<(string Title, string Message)> Infos { get; } = [];

    public List<(string Title, string Message)> Errors { get; } = [];

    public bool Confirm(string title, string message)
    {
        Confirms.Add((title, message));
        if (ConfirmAnswers.Count == 0)
        {
            throw new InvalidOperationException($"예상하지 못한 확인 창: {title} / {message}");
        }

        return ConfirmAnswers.Dequeue();
    }

    public void ShowInfo(string title, string message) => Infos.Add((title, message));

    public void ShowError(string title, string message) => Errors.Add((title, message));
}

/// <summary>항상 null(미리보기 없음)을 돌려주고 요청만 기록한다.</summary>
public sealed class FakeThumbnailProvider : IThumbnailProvider
{
    public List<string> Requests { get; } = [];

    /// <summary>요청마다 받은 취소 토큰(<see cref="Requests"/>와 같은 순서).</summary>
    public List<CancellationToken> Tokens { get; } = [];

    public Task<ImageSource?> GetAsync(string path, int size, CancellationToken ct)
    {
        lock (Requests)
        {
            Requests.Add(path);
            Tokens.Add(ct);
        }

        return Task.FromResult<ImageSource?>(null);
    }
}
