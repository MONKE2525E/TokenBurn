using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace UsageMonitor.Desktop;

internal sealed record TrayMenuActions(
    Action OpenDashboard,
    Action Refresh,
    Action Settings,
    Action Customize,
    Action CheckForUpdates,
    Action About,
    Action Quit,
    IReadOnlyList<MonitorOption> Monitors,
    string SelectedMonitor,
    Action<MonitorOption> SelectMonitor);

/// <summary>
/// A small native WPF tray surface that uses the same rounded charcoal language as the dashboard.
/// It intentionally does not use ContextMenuStrip, whose rectangular Win32 rendering cannot match
/// the rest of the app and was the source of the old screenshot's visual mismatch.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private readonly Border _surface;
    private readonly StackPanel _monitorPanel;
    private bool _closing;
    private bool _menuReady;

    public TrayMenuWindow(TrayMenuActions actions, System.Drawing.Point anchor)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = TransparentBrush;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Opacity = 0;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        PreviewKeyDown += OnPreviewKeyDown;
         Deactivated += (_, _) =>
         {
             if (_menuReady) CloseSafely();
         };

        _monitorPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(8, 0, 0, 4),
            Orientation = WpfOrientation.Vertical
        };
        foreach (var monitor in actions.Monitors)
        {
            var item = CreateMenuButton(monitor.DisplayName, () =>
            {
                actions.SelectMonitor(monitor);
                CloseSafely();
            }, 11);
            item.Content = new TextBlock
            {
                Text = (monitor.Id.Equals(actions.SelectedMonitor, StringComparison.OrdinalIgnoreCase) ? "●  " : "    ") + monitor.DisplayName,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _monitorPanel.Children.Add(item);
        }

        var content = new StackPanel { Orientation = WpfOrientation.Vertical };
        content.Children.Add(new TextBlock
        {
            Text = "Usage Monitor",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(12, 5, 12, 5)
        });
        content.Children.Add(CreateMenuButton("Open dashboard", actions.OpenDashboard));
        content.Children.Add(CreateMenuButton("Refresh now", actions.Refresh));
        var taskbarButton = CreateMenuButton("Taskbar display", () =>
        {
            _monitorPanel.Visibility = _monitorPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }, 12, true);
        content.Children.Add(taskbarButton);
        content.Children.Add(_monitorPanel);
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateMenuButton("Settings", actions.Settings));
        content.Children.Add(CreateMenuButton("Customize", actions.Customize));
        content.Children.Add(CreateMenuButton("Check for updates", actions.CheckForUpdates));
        content.Children.Add(CreateMenuButton("About Usage Monitor", actions.About));
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateMenuButton("Quit", actions.Quit));

        _surface = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("PanelStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(6),
            Child = content,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.42,
                BlurRadius = 18,
                ShadowDepth = 4
            }
        };
        Content = _surface;
        Loaded += (_, _) => PositionNear(anchor);
        SourceInitialized += (_, _) => SetScreenShareExcluded(App.CurrentApp.Settings.HideFromScreenShare);
    }

    private WpfButton CreateMenuButton(string label, Action action, double fontSize = 12, bool chevron = false)
    {
        var button = new WpfButton
        {
            MinWidth = 274,
            MinHeight = 32,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = TransparentBrush,
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = fontSize,
            Foreground = Brush("TextPrimaryBrush"),
            Focusable = true,
            Content = CreateButtonContent(label, chevron)
        };
        var style = new Style(typeof(WpfButton), System.Windows.Application.Current.TryFindResource(typeof(WpfButton)) as Style);
        style.Setters.Add(new Setter(WpfButton.BackgroundProperty, TransparentBrush));
        style.Setters.Add(new Setter(WpfButton.BorderBrushProperty, TransparentBrush));
        style.Setters.Add(new Setter(WpfButton.ForegroundProperty, Brush("TextPrimaryBrush")));
        style.Triggers.Add(new Trigger
        {
            Property = WpfButton.IsMouseOverProperty,
            Setters = { new Setter(WpfButton.BackgroundProperty, Brush("PanelRaisedBrush")) }
        });
        style.Triggers.Add(new Trigger
        {
            Property = WpfButton.IsKeyboardFocusWithinProperty,
            Setters = { new Setter(WpfButton.BackgroundProperty, Brush("PanelRaisedBrush")) }
        });
        button.Style = style;
        button.Click += (_, _) => action();
        return button;
    }

    private static Grid CreateButtonContent(string label, bool chevron)
    {
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (chevron)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "›",
                FontSize = 17,
                Foreground = Brush("TextSecondaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 1)
            });
        }
        return grid;
    }

    private static Border CreateSeparator()
        => new()
        {
            Height = 1,
            Margin = new Thickness(10, 5, 10, 5),
            Background = Brush("PanelStrokeBrush")
        };

    private void PositionNear(System.Drawing.Point anchor)
    {
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromPoint(anchor);
        var area = screen.WorkingArea;
        var width = ActualWidth > 0 ? ActualWidth : 286;
        var height = ActualHeight > 0 ? ActualHeight : 360;
        var left = Math.Clamp(anchor.X - width + 10, area.Left + 8, area.Right - width - 8);
        var top = anchor.Y - height - 8;
        if (top < area.Top + 8) top = anchor.Y + 8;
        top = Math.Clamp(top, area.Top + 8, area.Bottom - height - 8);
        Left = left;
        Top = top;
        Opacity = 1;
        Activate();
        Keyboard.Focus(this);
        _menuReady = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseSafely();
    }

    internal void CloseSafely()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch (Exception) { }
    }

    private void SetScreenShareExcluded(bool excluded)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        try
        {
            NativeMethods.SetWindowDisplayAffinity(hwnd,
                excluded ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static SolidColorBrush Brush(string key)
        => (System.Windows.Application.Current.TryFindResource(key) as SolidColorBrush)?.Clone()
           ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 36, 38));
}
