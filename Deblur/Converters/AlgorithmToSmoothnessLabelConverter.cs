using System.Globalization;
using System.Windows.Data;
using Deblur.Engine;

namespace Deblur.Converters;

public sealed class AlgorithmToSmoothnessLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AlgorithmType.Tikhonov ? "Regularization (λ)" : "Smoothness (K)";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
