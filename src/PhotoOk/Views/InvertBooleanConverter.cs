using System.Globalization;
using System.Windows.Data;

namespace PhotoOk.Views;

/// <summary>참/거짓을 뒤집는다(작업 중에는 목록을 잠그는 데 쓴다).</summary>
public sealed class InvertBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
