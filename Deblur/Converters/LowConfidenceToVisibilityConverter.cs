using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Deblur.Converters;

/// <summary>
/// Converts a confidence value (float in [0, 1]) to <see cref="Visibility.Visible"/>
/// when it's BELOW 0.35, otherwise <see cref="Visibility.Collapsed"/>. Used to show
/// the "Low confidence — manual entry recommended" warning next to a suggestion
/// panel exactly when the Accept button is disabled for the same threshold. Keeps
/// the confidence-gate reasoning transparent to the user rather than a mystery
/// grey button.
/// </summary>
public sealed class LowConfidenceToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is float f && f < 0.35f) return Visibility.Visible;
        if (value is double d && d < 0.35) return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
