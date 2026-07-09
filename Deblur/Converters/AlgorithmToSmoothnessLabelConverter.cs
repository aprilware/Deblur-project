using System.Globalization;
using System.Windows.Data;
using Deblur.Engine;

namespace Deblur.Converters;

public sealed class AlgorithmToSmoothnessLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            AlgorithmType.Tikhonov       => "Regularization (λ)",
            AlgorithmType.TotalVariation => "Regularization (λ)",
            _                            => "Smoothness (K)",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
