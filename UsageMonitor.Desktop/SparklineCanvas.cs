using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace UsageMonitor.Desktop;

/// <summary>Small redacted bar sparkline used by chart metrics in the dashboard.</summary>
public sealed class SparklineCanvas : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<MetricChartDisplay>), typeof(SparklineCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<MetricChartDisplay>? Points
    {
        get => (IReadOnlyList<MetricChartDisplay>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(MediaBrush), typeof(SparklineCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MediaBrush? Accent
    {
        get => (MediaBrush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2) return;

        var points = Points;
        var track = new SolidColorBrush(MediaColor.FromRgb(47, 57, 69));
        track.Freeze();
        drawingContext.DrawRoundedRectangle(track, null, new Rect(0, height - 2, width, 2), 1, 1);
        if (points is not { Count: > 0 }) return;

        var max = points.Max(point => Math.Max(0, point.Value));
        if (max <= 0) max = 1;
        var gap = Math.Clamp(width / Math.Max(1, points.Count * 12), 1, 3);
        var barWidth = Math.Max(1, (width - gap * (points.Count - 1)) / points.Count);
        var brush = Accent ?? new SolidColorBrush(MediaColor.FromRgb(83, 210, 195));
        if (brush.CanFreeze && !brush.IsFrozen) brush = brush.Clone();
        for (var i = 0; i < points.Count; i++)
        {
            var ratio = Math.Clamp(points[i].Value / max, 0, 1);
            var barHeight = Math.Max(2, (height - 3) * ratio);
            var x = i * (barWidth + gap);
            drawingContext.DrawRoundedRectangle(brush, null,
                new Rect(x, height - barHeight - 2, barWidth, barHeight), 1, 1);
        }
    }
}
