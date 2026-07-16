using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Deblur.Engine;

namespace Deblur.Converters;

/// <summary>
/// Converts <see cref="AlgorithmType"/> to <see cref="Visibility.Visible"/> only when the
/// value is <see cref="AlgorithmType.BlindDeconvolution"/>. Drives visibility of the
/// PsfDisplay sidebar control, which only has anything meaningful to show for blind
/// deconvolution's estimated kernel.
/// </summary>
public sealed class AlgorithmTypeToBlindVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AlgorithmType.BlindDeconvolution ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
