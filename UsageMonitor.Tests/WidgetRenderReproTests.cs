using System.Windows.Media.Imaging;
using UsageMonitor.Desktop;

namespace UsageMonitor.Tests;

public sealed class WidgetRenderReproTests
{
    // The regression being protected: a provider row or metric text could silently overflow the
    // compact strip's bitmap (clipped content) or compute a broken layout (negative/infinite
    // width). Every case must render real content within its bitmap bounds.
    private static readonly (string Name, MetricDisplay[] Metrics)[] Cases =
    [
        ("opencode-dollar", [new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true)]),
        ("opencode-plain", [new MetricDisplay("Session", "9.00", "", 0.25, "normal", "OpenCode", null, true)]),
        ("opencode-percent", [new MetricDisplay("Session", "25%", "", 0.25, "normal", "OpenCode", null, true)]),
        ("codex-percent", [new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true)]),
        ("unknown-dollar", [new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "Zzz", null, true)]),
        ("codex-then-opencode",
        [
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
        ]),
        ("opencode-then-codex",
        [
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
        ]),
        ("claude-codex-opencode",
        [
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
            new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
        ]),
        ("antigravity-opencode",
        [
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
            new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
        ]),
        ("claude-antigravity-opencode",
        [
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
            new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
            new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
        ]),
        ("four-groups-opencode-replaced-with-zzz",
        [
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
            new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
            new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "Zzz", null, true),
        ]),
        ("five-groups",
        [
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
            new MetricDisplay("Weekly", "50%", "", 0.5, "normal", "Cursor", null, true),
        ]),
        ("three-groups-single-each",
        [
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
        ]),
        ("four-groups-single-each",
        [
            new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
            new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
            new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
            new MetricDisplay("Weekly", "50%", "", 0.5, "normal", "Cursor", null, true),
        ]),
    ];

    [Fact]
    public void StripRendersEveryProviderValueInsideItsBitmap()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var (name, metrics) in Cases)
                {
                    var widget = new WidgetWindow();
                    var host = new System.Windows.Window
                    {
                        Content = widget,
                        Width = 320,
                        Height = 80,
                        Left = -10000,
                        Top = -10000,
                        WindowStyle = System.Windows.WindowStyle.None,
                        ResizeMode = System.Windows.ResizeMode.NoResize,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Background = System.Windows.Media.Brushes.Transparent
                    };
                    host.Show();
                    widget.ResetTimeDisplay = "Countdown";
                    widget.SetMetrics(metrics);
                    host.Width = Math.Max(40, widget.IdealWidthDip);
                    host.Height = Math.Max(30, 60 / 1.25);
                    host.UpdateLayout();
                    var bitmap = widget.RenderToBitmap(3.75);

                    var stride = bitmap.PixelWidth * 4;
                    var pixels = new byte[bitmap.PixelHeight * stride];
                    bitmap.CopyPixels(pixels, stride, 0);
                    var rightmost = -1;
                    for (var x = bitmap.PixelWidth - 1; x >= 0 && rightmost < 0; x--)
                    for (var y = 0; y < bitmap.PixelHeight; y++)
                    {
                        if (pixels[y * stride + x * 4 + 3] > 1) { rightmost = x; break; }
                    }

                    Assert.True(double.IsFinite(widget.IdealWidthDip) && widget.IdealWidthDip > 0,
                        $"{name}: the layout must produce a sane positive width, got {widget.IdealWidthDip:0.##}");
                    Assert.True(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0,
                        $"{name}: the render must produce a non-empty bitmap");
                    Assert.True(rightmost >= 0,
                        $"{name}: the strip must actually paint content for every provider value");
                    Assert.True(rightmost < bitmap.PixelWidth,
                        $"{name}: the rightmost content pixel ({rightmost}) must fit inside the bitmap ({bitmap.PixelWidth}px)");
                    host.Close();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
