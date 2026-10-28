using System.Globalization;
using System.Windows.Data;
using PhotoOk.Core.History;
using PhotoOk.ViewModels;

namespace PhotoOk.Views;

/// <summary>작업 기록 줄의 파일 수 문구(<see cref="HistoryViewModel.CountsOf"/>)로 바꾼다.</summary>
public sealed class HistoryCountsTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RunSummary run ? HistoryViewModel.CountsOf(run) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
