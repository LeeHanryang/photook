namespace PhotoOk.ViewModels;

/// <summary>오래 걸리는 작업(스캔·실행·되돌리기)을 하는 화면. 창을 닫을 때 확인하는 데 쓴다.</summary>
public interface IBusyViewModel
{
    bool IsBusy { get; }

    /// <summary>지금 다루는 파일까지 마치고 멈추도록 요청한다. 실제로 멈추면 <see cref="BusyEnded"/>를 알린다.</summary>
    void Cancel();

    /// <summary>작업이 끝나거나 멈춰 <see cref="IsBusy"/>가 false가 되었을 때. 화면(UI) 스레드에서 알린다.</summary>
    event EventHandler? BusyEnded;
}
