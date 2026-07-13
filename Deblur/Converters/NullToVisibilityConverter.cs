using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Deblur.Converters;

/// <summary>
/// Converts null to <see cref="Visibility.Collapsed"/> and any non-null value to
/// <see cref="Visibility.Visible"/>. Used to show inline suggestion panels only once
/// an estimator has produced a result.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
