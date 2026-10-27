using System.Globalization;
using System.Windows.Data;
using PhotoOk.Core.History;

namespace PhotoOk.Views;

/// <summary>작업 상태를 화면에 보일 한국어로 바꾼다.</summary>
public sealed class RunStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RunStatus.Completed => "완료",
        RunStatus.CompletedWithFailures => "일부 실패",
        RunStatus.Unfinished => "끝나지 않음",
        RunStatus.PlanIncomplete => "계획이 끝나지 않음",
        RunStatus.UndoInProgress => "되돌리는 중이었음",
        RunStatus.Undone => "되돌림",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
