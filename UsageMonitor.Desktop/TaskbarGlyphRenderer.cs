using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageMonitor.Core;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace UsageMonitor.Desktop;

/// <summary>
/// Renders the compact status glyph used by the supported WPF taskbar button.
///
    /// The menu-bar renderer deliberately uses a small, bounded set of bars rather than
/// trying to squeeze long values into a 16-24 pixel shell surface. Windows has the same constraint
/// on a taskbar button. This renderer follows that rule, but keeps a small provider colour marker at
/// the leading edge so a stack of bars still has a recognizable identity at a glance.
/// </summary>
public static class TaskbarGlyphRenderer
{
    /// <summary>
    /// The renderer leaves a visible tail on near-full bars. Without this quantization, 96-99% reads as
    /// a solid bar at taskbar scale and the user can miss that the quota is nearly exhausted.
    /// </summary>
    public static double VisualFraction(double fraction)
    {
        if (!double.IsFinite(fraction)) return 0;
        var clamped = Math.Clamp(fraction, 0, 1);
        if (clamped > 0.7 && clamped < 1)
        {
            var remainder = 1 - clamped;
            var quantized = Math.Min(1, Math.Ceiling(remainder / 0.15) * 0.15);
            return Math.Max(0, 1 - quantized);
        }

        return clamped;
    }

    /// <summary>Returns true when a metric can safely be shown as a real shell status row.</summary>
    public static bool HasRenderableData(MetricDisplay metric)
    {
        if (metric is null || string.IsNullOrWhiteSpace(metric.Value)) return false;
        var value = metric.Value.Trim();
        if (value is "\u2014" or "\u2013" or "\u2212" or "\u2026" ||
            value.Equals("\u00e2\u20ac\u201d", StringComparison.Ordinal)) return false;
        return !value.Equals("No data", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the text supplied to Explorer for the taskbar button description. It is intentionally
    /// more verbose than the glyph because the description is the accessible, exact-value fallback.
    /// </summary>
    public static string BuildTooltip(IReadOnlyList<MetricDisplay>? metrics, string? resetTimeDisplay = "Countdown")
    {
        var items = (metrics ?? Array.Empty<MetricDisplay>()).Where(HasRenderableData).ToArray();
        if (items.Length == 0) return "TokenBurn | no provider data yet";

        var lines = items.Select(metric =>
        {
            var reset = metric.ResetAt is { } resetAt
                ? $" ({ResetTimeFormatter.FormatSurface(resetAt, resetTimeDisplay)})"
                : string.Empty;
            var provider = string.IsNullOrWhiteSpace(metric.Provider) ? string.Empty : $"{metric.Provider} ";
            return $"{provider}{metric.Label}: {metric.Value}{reset}";
        });

        var tooltip = "TokenBurn | " + string.Join("  |  ", lines);
        return tooltip.Length <= 240 ? tooltip : tooltip[..237] + "...";
    }

    /// <summary>Renders a provider-only, DPI-independent image for the taskbar strip.</summary>
    public static ImageSource Render(IReadOnlyList<MetricDisplay>? metrics)
    {
        var items = (metrics ?? Array.Empty<MetricDisplay>())
            .Where(HasRenderableData)
            .Where(metric => metric.IsMeter)
            .ToArray();

        const int pixels = 48;
        if (items.Length == 0) return CreateTransparentBitmap(pixels);

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) DrawBars(drawing, items, pixels);

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateTransparentBitmap(int pixels)
    {
        var visual = new DrawingVisual();
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawBars(DrawingContext drawing, IReadOnlyList<MetricDisplay> metrics, int pixels)
    {
        const double dotCenterX = 5.5;
        const double dotRadius = 2.1;
        const double trackX = 11;
        const double trackWidth = 32;
        var barHeight = metrics.Count > 7 ? 3.0 : metrics.Count > 5 ? 4.0 : 5.0;
        var gap = metrics.Count > 7 ? 1.5 : metrics.Count > 5 ? 2.0 : 3.0;
        var totalHeight = metrics.Count * barHeight + (metrics.Count - 1) * gap;
        var top = (pixels - totalHeight) / 2.0;

        for (var i = 0; i < metrics.Count; i++)
        {
            var metric = metrics[i];
            var y = top + i * (barHeight + gap);
            var accent = AccentFor(metric);
            var accentBrush = new SolidColorBrush(accent);
            var trackBrush = new SolidColorBrush(MediaColor.FromArgb(72, 180, 190, 201));
            accentBrush.Freeze();
            trackBrush.Freeze();

            // The marker is the provider identity cue that the original macOS text style gets from
            // its provider icon. It remains visible even when the bounded quota is exactly 0%.
            drawing.DrawEllipse(accentBrush, null, new WpfPoint(dotCenterX, y + barHeight / 2), dotRadius, dotRadius);
            drawing.DrawRoundedRectangle(trackBrush, null, new WpfRect(trackX, y, trackWidth, barHeight), 2.5, 2.5);

            var progress = Math.Clamp(metric.Progress, 0, 1);
            if (!double.IsFinite(progress) || progress <= 0) continue;

            var visualFraction = VisualFraction(progress);
            var fillWidth = Math.Max(2, trackWidth * visualFraction);
            var fillBrush = new SolidColorBrush(accent);
            fillBrush.Freeze();
            drawing.DrawRoundedRectangle(fillBrush, null,
                new WpfRect(trackX, y, Math.Min(trackWidth, fillWidth), barHeight), 2.5, 2.5);

            // Keep a subtle coloured remainder, mirroring MenuBarBarGeometry's divider and making
            // a near-full quota legible without introducing a second detached line.
            if (visualFraction < 1)
            {
                var remainderBrush = new SolidColorBrush(MediaColor.FromArgb(60, accent.R, accent.G, accent.B));
                remainderBrush.Freeze();
                var remainderX = trackX + fillWidth;
                drawing.DrawRoundedRectangle(remainderBrush, null,
                    new WpfRect(remainderX, y, Math.Max(1, trackWidth - fillWidth), barHeight), 1, 2.5);
            }
        }
    }

    private static MediaColor AccentFor(MetricDisplay metric)
    {
        var state = metric.State?.Trim().ToLowerInvariant();
        if (state == "danger") return MediaColor.FromRgb(246, 113, 125);
        if (state == "warn") return MediaColor.FromRgb(244, 190, 89);
        if (state == "neutral") return MediaColor.FromRgb(144, 157, 177);

        return metric.Provider?.Trim().ToLowerInvariant() switch
        {
            "codex" => MediaColor.FromRgb(61, 130, 246),
            "claude" or "claude code" => MediaColor.FromRgb(218, 119, 86),
            "antigravity" => MediaColor.FromRgb(52, 168, 83),
            "opencode" => MediaColor.FromRgb(255, 255, 255),
            "cursor" => MediaColor.FromRgb(108, 123, 255),
            "copilot" => MediaColor.FromRgb(137, 87, 229),
            "devin" => MediaColor.FromRgb(255, 180, 84),
            "grok" => MediaColor.FromRgb(201, 206, 214),
            _ => MediaColor.FromRgb(83, 210, 195)
        };
    }

}
