using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using UsageMonitor.Core;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPath = System.Windows.Shapes.Path;
using WpfSize = System.Windows.Size;

namespace UsageMonitor.Desktop;

/// <summary>
/// Windows shell host for the compact taskbar overlay. It uses a real provider mark, then either
/// one bold metric or a compact two-line session/weekly stack. It deliberately has no provider
/// labels, state colours, quota bars, card border, or dashboard chrome.
/// </summary>
public partial class WidgetWindow : System.Windows.Controls.UserControl
{
    // Kept only so older settings and stale-window cleanup can recognize the retired WPF surface.
    internal const string OverlayMarker = "UsageMonitor.TaskbarOverlay";
    private const double ProviderGlyphSize = 16;
    private static readonly SolidColorBrush ForegroundBrush = CreateFrozenBrush(0xF1, 0xF2, 0xF4);
    private static readonly Regex SvgPathPattern = new("d=\"(?<path>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, Geometry?> ProviderGeometryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ProviderGeometryLock = new();
    private IReadOnlyList<MetricDisplay> _metrics = Array.Empty<MetricDisplay>();
    private double _availableWidthDip;

    public string ResetTimeDisplay { get; set; } = "Countdown";

    public WidgetWindow()
    {
        InitializeComponent();
    }

    /// <summary>Measured after every metric update so the native host only occupies its content.</summary>
    public double IdealWidthDip { get; private set; } = 40;

    public void SetMetrics(IEnumerable<MetricDisplay> values)
    {
        _metrics = (values ?? []).ToArray();
        RenderMetrics();
    }

    public void SetAvailableWidthDip(double availableWidthDip)
    {
        _availableWidthDip = Math.Max(40, availableWidthDip);
        RenderMetrics();
    }

    internal BitmapSource RenderToBitmap(double scale)
    {
        var width = Math.Max(40, Root.Width > 0 ? Root.Width : IdealWidthDip);
        var height = Math.Max(30, Height);
        Measure(new WpfSize(width, height));
        Arrange(new Rect(0, 0, width, height));
        Root.Measure(new WpfSize(width, height));
        Root.Arrange(new Rect(0, 0, width, height));
        UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(this);
        bitmap.Freeze();
        return bitmap;
    }

    private void RenderMetrics()
    {
        // This follows MenuBarContent.swift: group pinned metrics by provider and omit values
        // that cannot represent real usage. A provider has one big value or two tight values.
        var groups = _metrics
            .GroupBy(item => item.Provider ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => new ProviderMetricGroup(group.Key, group.Take(2).ToArray()))
            .ToArray();

        MetricStrip.LayoutTransform = null;
        RenderGroups(groups);
        MetricStrip.Measure(new WpfSize(double.PositiveInfinity, Height));
        // Explorer rounds the child HWND independently from WPF's text layout. Reserve a small
        // right-edge allowance so the second line of a provider pair is never clipped at 125–200%
        // DPI. The result remains a compact status item, not a fixed dashboard-width rectangle.
        var desiredWidth = Math.Max(40, MetricStrip.DesiredSize.Width + 16);
        if (_availableWidthDip > 0 && desiredWidth > _availableWidthDip)
        {
            // Keep every provider mark visible, but drop the secondary value before shrinking the
            // entire strip. This preserves useful identity at high DPI and avoids unreadable text.
            var compactGroups = groups
                .Select(group => new ProviderMetricGroup(group.Provider, group.Metrics.Take(1).ToArray()))
                .ToArray();
            RenderGroups(compactGroups);
            MetricStrip.Measure(new WpfSize(double.PositiveInfinity, Height));
            var compactWidth = Math.Max(40, MetricStrip.DesiredSize.Width + 16);
            if (compactWidth > _availableWidthDip)
            {
                var scale = Math.Clamp(_availableWidthDip / compactWidth, 0.55, 1);
                MetricStrip.LayoutTransform = new ScaleTransform(scale, 1);
                desiredWidth = Math.Min(_availableWidthDip, compactWidth * scale);
            }
            else
            {
                desiredWidth = compactWidth;
            }
        }
        Root.Width = desiredWidth;
        IdealWidthDip = desiredWidth;
    }

    private void RenderGroups(IReadOnlyList<ProviderMetricGroup> groups)
    {
        MetricStrip.Children.Clear();
        for (var index = 0; index < groups.Count; index++)
        {
            if (index > 0)
                MetricStrip.Children.Add(new Border { Width = 11, Height = 1, Background = WpfBrushes.Transparent });
            MetricStrip.Children.Add(CreateProviderGroup(groups[index], ResetTimeDisplay));
        }
    }

    private static FrameworkElement CreateProviderGroup(ProviderMetricGroup group, string resetTimeDisplay)
    {
        var providerStack = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        providerStack.Children.Add(CreateProviderGlyph(group.Provider));
        providerStack.Children.Add(new Border { Width = 4, Height = 1, Background = WpfBrushes.Transparent });

        var values = new StackPanel
        {
            Orientation = WpfOrientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        if (group.Metrics.Length == 1)
        {
            values.Children.Add(CreateValueText(group.Metrics[0].Value, 14, FontWeights.Bold, 16));
        }
        else
        {
            foreach (var metric in group.Metrics)
                values.Children.Add(CreateValueText(metric.Value, 10, FontWeights.SemiBold, 9));
        }

        providerStack.Children.Add(values);
        return providerStack;
    }

    private static TextBlock CreateValueText(string value, double size, FontWeight weight, double lineHeight)
    {
        return new TextBlock
        {
            Text = value,
            Foreground = ForegroundBrush,
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = size,
            FontWeight = weight,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
    }

    private static FrameworkElement CreateProviderGlyph(string provider)
    {
        var geometry = GetProviderGeometry(provider);
        if (geometry is null)
        {
            // This only covers a future provider without a packaged mark. Never replace known
            // provider logos with a hand-drawn approximation.
            return new Ellipse
            {
                Width = ProviderGlyphSize,
                Height = ProviderGlyphSize,
                Fill = ForegroundBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new WpfPath
        {
            Data = geometry,
            Fill = ForegroundBrush,
            Width = ProviderGlyphSize,
            Height = ProviderGlyphSize,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
    }

    internal static Geometry? GetProviderGeometry(string provider)
    {
        var asset = ProviderAssetName(provider);
        if (asset is null) return null;

        lock (ProviderGeometryLock)
        {
            if (ProviderGeometryCache.TryGetValue(asset, out var cached)) return cached;

            Geometry? geometry = null;
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ProviderIcons", asset + ".svg");
                var svg = File.ReadAllText(path);
                var paths = SvgPathPattern.Matches(svg)
                    .Select(match => Geometry.Parse(match.Groups["path"].Value))
                    .ToArray();
                if (paths.Length > 0)
                {
                    var compound = new GeometryGroup();
                    foreach (var svgPath in paths) compound.Children.Add(svgPath);
                    compound.Freeze();
                    geometry = compound;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }

            ProviderGeometryCache[asset] = geometry;
            return geometry;
        }
    }

    private static string? ProviderAssetName(string provider)
    {
        return provider.Trim().ToLowerInvariant() switch
        {
            "codex" => "codex",
            "claude" or "claude code" => "claude",
            "antigravity" => "antigravity",
            "cursor" => "cursor",
            "copilot" => "copilot",
            "devin" => "devin",
            "grok" => "grok",
            "opencode" or "open code" => "opencode",
            _ => null
        };
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(WpfColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record ProviderMetricGroup(string Provider, MetricDisplay[] Metrics);
}

public sealed class WidgetDragDeltaEventArgs(double deltaXDip, double deltaYDip, System.Drawing.Point currentPoint) : EventArgs
{
    public double DeltaXDip { get; } = deltaXDip;
    public double DeltaYDip { get; } = deltaYDip;
    public System.Drawing.Point CurrentPoint { get; } = currentPoint;
}
