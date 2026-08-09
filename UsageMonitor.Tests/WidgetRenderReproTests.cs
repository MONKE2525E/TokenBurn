using System.IO;
using System.Windows.Media.Imaging;
using UsageMonitor.Desktop;

namespace UsageMonitor.Tests;

public sealed class WidgetRenderReproTests
{
    [Fact]
    public void StripShowsEveryProviderValue()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var report = new System.Text.StringBuilder();
                var cases = new (string Name, MetricDisplay[] Metrics)[]
                {
                    ("opencode-dollar", new[] { new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true) }),
                    ("opencode-plain", new[] { new MetricDisplay("Session", "9.00", "", 0.25, "normal", "OpenCode", null, true) }),
                    ("opencode-percent", new[] { new MetricDisplay("Session", "25%", "", 0.25, "normal", "OpenCode", null, true) }),
                    ("codex-percent", new[] { new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true) }),
                    ("unknown-dollar", new[] { new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "Zzz", null, true) }),
                    ("codex-then-opencode", new[]
                    {
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("opencode-then-codex", new[]
                    {
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                    }),
                    ("claude-codex-opencode", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("antigravity-opencode", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("claude-antigravity-opencode", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("all-four-again", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("four-groups-opencode-replaced-with-zzz", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "Zzz", null, true),
                    }),
                    ("four-groups-reordered", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "29%", "", 0.71, "normal", "Claude Code", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Weekly", "99%", "", 0.01, "normal", "Antigravity", null, true),
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("five-groups", new[]
                    {
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Claude Code", null, true),
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                        new MetricDisplay("Weekly", "50%", "", 0.5, "normal", "Cursor", null, true),
                    }),
                    ("three-groups-single-each", new[]
                    {
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                    }),
                    ("four-groups-single-each", new[]
                    {
                        new MetricDisplay("Weekly", "0%", "", 1.0, "normal", "Codex", null, true),
                        new MetricDisplay("Session", "94%", "", 0.06, "normal", "Antigravity", null, true),
                        new MetricDisplay("Session", "$9.00", "", 0.25, "normal", "OpenCode", null, true),
                        new MetricDisplay("Weekly", "50%", "", 0.5, "normal", "Cursor", null, true),
                    }),
                };

                foreach (var (name, metrics) in cases)
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

                    var path = Path.Combine(Path.GetTempPath(), $"widget-probe-{name}.png");
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(path)) encoder.Save(stream);

                    report.AppendLine($"{name}: idealDip={widget.IdealWidthDip:0.##} bitmapPx={bitmap.PixelWidth} rightmostContentPx={rightmost} png={path}");
                    host.Close();
                }
                Console.WriteLine(report.ToString());
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
