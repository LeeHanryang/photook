using System.Globalization;
using System.Windows.Data;

namespace PhotoOk.Views;

/// <summary>
/// 라디오 버튼 하나를 열거형 값 하나에 묶는다. <c>ConverterParameter</c>에 그 값의 이름을 쓴다.
/// 켜지면 그 값으로 바꾸고, 꺼지는 쪽은 건드리지 않는다.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
