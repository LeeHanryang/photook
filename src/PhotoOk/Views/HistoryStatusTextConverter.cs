using System.Globalization;
using System.Windows.Data;
using PhotoOk.Core.History;
using PhotoOk.ViewModels;

namespace PhotoOk.Views;

/// <summary>작업 기록 화면에 보일 작업 상태 문구(<see cref="HistoryViewModel.StatusOf"/>)로 바꾼다.</summary>
public sealed class HistoryStatusTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RunStatus status ? HistoryViewModel.StatusOf(status) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
