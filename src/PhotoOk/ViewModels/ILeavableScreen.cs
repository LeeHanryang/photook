namespace PhotoOk.ViewModels;

/// <summary>
/// 다른 화면으로 바뀔 때 알림을 받는 화면. <see cref="ShellViewModel"/>이 지금 화면을 바꿀 때(오류 뒤 시작 화면으로 돌아갈 때 포함)
/// 떠나는 화면의 <see cref="OnLeft"/>를 부른다. 화면은 여기서 남은 백그라운드 읽기(기록 읽기, 썸네일, 재계획)를 멈춘다.
/// 화면은 한 번 떠나면 다시 쓰이지 않는다(돌아갈 때는 새 화면을 만든다). 여러 번 불려도 안전해야 하고, 예외를 던지면 안 된다.
/// </summary>
public interface ILeavableScreen
{
    void OnLeft();
}
