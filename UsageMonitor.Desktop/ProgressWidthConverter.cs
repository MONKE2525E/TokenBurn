using System.Globalization;
using System.Windows.Data;

namespace UsageMonitor.Desktop;

/// <summary>
/// Converts a normalized ProgressBar value into the pixel width of the filled portion.
/// WPF's default template does this internally, but the compact dashboard uses a custom
/// template so the track and fill keep the rounded treatment. Without an explicit
/// width binding the custom indicator renders at zero width, which looks like an empty bar.
/// </summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 ||
            values[0] is not double value ||
            values[1] is not double maximum ||
            values[2] is not double actualWidth ||
            maximum <= 0 ||
            actualWidth <= 0 ||
            double.IsNaN(value) || double.IsInfinity(value) ||
            double.IsNaN(maximum) || double.IsInfinity(maximum) ||
            double.IsNaN(actualWidth) || double.IsInfinity(actualWidth))
        {
            return 0d;
        }

        return Math.Clamp(value / maximum, 0d, 1d) * actualWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
