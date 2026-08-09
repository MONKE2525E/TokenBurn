using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfPen = System.Windows.Media.Pen;
using WpfSize = System.Windows.Size;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace UsageMonitor.Desktop;

/// <summary>Lightweight vector renderer for the segmented spend ring.</summary>
public sealed class SpendRingCanvas : FrameworkElement
{
    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(SpendRingSummary), typeof(SpendRingCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public SpendRingSummary? Summary
    {
        get => (SpendRingSummary?)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public static readonly DependencyProperty AnimationProgressProperty = DependencyProperty.Register(
        nameof(AnimationProgress), typeof(double), typeof(SpendRingCanvas),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Progressive sweep used when a new period or metric is selected.</summary>
    public double AnimationProgress
    {
        get => (double)GetValue(AnimationProgressProperty);
        set => SetValue(AnimationProgressProperty, value);
    }

    public double RingThickness { get; set; } = 18;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = new WpfSize(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var center = new WpfPoint(size.Width / 2, size.Height / 2);
        var radius = Math.Max(1, Math.Min(size.Width, size.Height) / 2 - RingThickness / 2 - 2);
        var trackPen = new WpfPen(Brush("#34383B"), RingThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var summary = Summary;
        if (summary is null || !summary.HasData)
        {
            DrawCenter(drawingContext, center, radius, "No data", summary?.UnitLabel ?? "local history");
            return;
        }

        var ringTotal = summary.Segments.Sum(segment => segment.Value);
        if (ringTotal <= 0 || double.IsNaN(ringTotal))
        {
            DrawCenter(drawingContext, center, radius, summary.TotalLabel, summary.UnitLabel);
            return;
        }

        var angle = -90d;
        const double gap = 2.5;
        // A provider can legitimately account for a fraction of a degree of the total. Floor its
        // drawn sweep to the smallest sliver that still reads as a wedge instead of disappearing.
        const double minSliverDegrees = 3d;
        foreach (var segment in summary.Segments)
        {
            var sweep = segment.Value / ringTotal * 360d;
            if (sweep <= 0) continue;
            var floorSweep = Math.Min(sweep, minSliverDegrees);
            var visibleSweep = Math.Max(floorSweep, sweep - gap) * Math.Clamp(AnimationProgress, 0, 1);
            DrawArc(drawingContext, center, radius, angle + gap / 2, visibleSweep,
                new WpfPen(segment.Color, RingThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            angle += sweep;
        }

        // Estimation is already called out in the card header. Keep the center unit short when
        // one is useful, so it cannot collide with the primary value in the hole.
        DrawCenter(drawingContext, center, radius, summary.TotalLabel, summary.UnitLabel);
    }

    private static void DrawArc(DrawingContext drawingContext, WpfPoint center, double radius,
        double startAngle, double sweepAngle, WpfPen pen)
    {
        if (sweepAngle <= 0) return;
        if (sweepAngle >= 359.99)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweepAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new WpfSize(radius, radius), 0, sweepAngle > 180,
                SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private void DrawCenter(DrawingContext drawingContext, WpfPoint center, double radius, string total, string unit)
    {
        // Use a two-line center label only when the selected metric has a useful unit. Cost
        // includes "$", and the metric picker identifies the per-token rate, so both render as
        // a single value in the ring.
        var primarySize = 12d;
        var secondarySize = 8d;
        // Bound the label by the ring's actual hole, not just a canvas-relative guess. A fixed
        // 40-92dip range let long values (e.g. "$204.14") overlap the ring on a small card.
        var holeDiameter = Math.Max(20d, radius * 2d - RingThickness);
        var availableWidth = holeDiameter * 0.82d;
        var primary = CreateText(total, primarySize, "Segoe UI Semibold", "#E8EDF5");
        var hasSecondary = !string.IsNullOrWhiteSpace(unit);
        var secondary = hasSecondary
            ? CreateText(unit, secondarySize, "Segoe UI Semibold", "#9CA9BC")
            : null;

        // Reduce the value before truncating it. The unit is intentionally kept short by the
        // caller, but it still needs a bounded width when the ring is rendered in a compact card.
        while (primary.Width > availableWidth && primarySize > 9)
        {
            primarySize -= 0.5;
            primary = CreateText(total, primarySize, "Segoe UI Semibold", "#E8EDF5");
        }

        while (secondary is not null && secondary.Width > availableWidth && secondarySize > 7)
        {
            secondarySize -= 0.5;
            secondary = CreateText(unit, secondarySize, "Segoe UI Semibold", "#9CA9BC");
        }

        if (secondary is null)
        {
            drawingContext.DrawText(primary, new WpfPoint(center.X - primary.Width / 2, center.Y - primary.Height / 2));
            return;
        }

        // Lay the two rows out from their actual line boxes instead of guessing two baseline
        // coordinates. FormattedText includes font leading, and the old fixed baselines could
        // collapse that leading into the next row at a different DPI or font fallback. Centering
        // the measured block and keeping an explicit gap makes long values plus "USD" impossible
        // to overlap while preserving the compact SwiftUI-style hole label.
        var gap = Math.Max(2d, secondarySize * 0.3d);
        var blockHeight = primary.Height + gap + secondary.Height;
        // The width loop alone doesn't stop a tall block from poking into the ring on a very
        // small card; shrink both rows together until the block also fits the hole's height.
        while (blockHeight > holeDiameter * 0.86d && primarySize > 9 && secondarySize > 7)
        {
            primarySize -= 0.5;
            secondarySize -= 0.5;
            primary = CreateText(total, primarySize, "Segoe UI Semibold", "#E8EDF5");
            secondary = CreateText(unit, secondarySize, "Segoe UI Semibold", "#9CA9BC");
            gap = Math.Max(2d, secondarySize * 0.3d);
            blockHeight = primary.Height + gap + secondary.Height;
        }
        var top = center.Y - blockHeight / 2d;
        drawingContext.DrawText(primary, new WpfPoint(center.X - primary.Width / 2, top));
        drawingContext.DrawText(secondary, new WpfPoint(center.X - secondary.Width / 2, top + primary.Height + gap));
    }

    private static FormattedText CreateText(string text, double size, string family, string color)
        => new(text, CultureInfo.CurrentCulture, WpfFlowDirection.LeftToRight,
            new Typeface(family), size, Brush(color), 1.0);

    private static WpfPoint PointOnCircle(WpfPoint center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new WpfPoint(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static SolidColorBrush Brush(string hex)
    {
        try
        {
            var color = (WpfColor)WpfColorConverter.ConvertFromString(hex)!;
            return new SolidColorBrush(color);
        }
        catch
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }
}
