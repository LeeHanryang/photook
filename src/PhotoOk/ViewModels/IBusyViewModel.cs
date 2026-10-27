namespace PhotoOk.ViewModels;

/// <summary>오래 걸리는 작업(스캔·실행·되돌리기)을 하는 화면. 창을 닫을 때 확인하는 데 쓴다.</summary>
public interface IBusyViewModel
{
    bool IsBusy { get; }

    /// <summary>
    /// 지금 다루는 파일까지 마치고 멈추도록 요청한다. 실제로 멈추면 <see cref="BusyEnded"/>를 알린다.
    /// 요청만 하고 바로 돌아오며, 예외를 던지면 안 된다.
    /// </summary>
    void Cancel();

    /// <summary>
    /// 작업이 끝나거나 멈춰 <see cref="IsBusy"/>가 false가 되었을 때. 화면(UI) 스레드에서 알린다.
    /// 정상 종료뿐 아니라 오류·취소로 끝날 때도 반드시 알려야 하므로 작업을 감싼 <c>finally</c>에서 <see cref="IsBusy"/>를
    /// false로 바꾼 뒤 알린다. 알리지 않으면 닫기를 승인한 창이 사용자가 다시 묻기 전까지 닫히지 않는다.
    /// </summary>
    event EventHandler? BusyEnded;
}
