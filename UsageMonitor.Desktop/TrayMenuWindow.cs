using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace UsageMonitor.Desktop;

internal sealed record TrayMenuActions(
    Action OpenDashboard,
    Action Refresh,
    Action Settings,
    Action Customize,
    Action CheckForUpdates,
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
    private readonly TranslateTransform _surfaceSlide = new();
    private bool _closing;
    private bool _menuReady;
    private bool _openedUpward;
    private bool _monitorPanelAnimating;

    // Windows' own reduced-motion equivalent. Honour it the same way the popup honours
    // prefers-reduced-motion.
    private static bool MotionEnabled => SystemParameters.ClientAreaAnimation;

    private static CubicEase EaseOut => new() { EasingMode = EasingMode.EaseOut };

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
            // A real column rather than a "●  " / "    " string prefix: prefix alignment depended
            // on space characters in a proportional font, so marked and unmarked rows never lined up.
            item.Content = CreateMonitorRow(
                monitor.DisplayName,
                monitor.Id.Equals(actions.SelectedMonitor, StringComparison.OrdinalIgnoreCase));
            _monitorPanel.Children.Add(item);
        }

        var content = new StackPanel { Orientation = WpfOrientation.Vertical };
        content.Children.Add(CreateBrandHeader());
        content.Children.Add(CreateMenuButton("Open dashboard", actions.OpenDashboard));
        content.Children.Add(CreateMenuButton("Refresh now", actions.Refresh));
        var taskbarButton = CreateMenuButton("Taskbar display", ToggleMonitorPanel, 12, true);
        content.Children.Add(taskbarButton);
        content.Children.Add(_monitorPanel);
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateMenuButton("Settings", actions.Settings));
        content.Children.Add(CreateMenuButton("Customize", actions.Customize));
        content.Children.Add(CreateMenuButton("Check for updates", actions.CheckForUpdates));
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateMenuButton("Quit", actions.Quit));

        _surface = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("PanelStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Padding = new Thickness(5),
            Child = content,
            RenderTransform = _surfaceSlide,
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
            MinWidth = 232,
            MinHeight = 28,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = TransparentBrush,
            BorderBrush = TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = fontSize,
            Foreground = Brush("TextPrimaryBrush"),
            Focusable = true,
            Content = CreateButtonContent(label, chevron)
        };
        // Avoid composing a new style from the app's implicit Button style here. WPF can seal
        // that implicit style while the tray menu is being constructed, which previously threw
        // on IsMouseOver and forced the ugly white fallback menu. Keeping the handler approach,
        // but cross-fading the brush instead of swapping it outright.
        var hover = Brush("PanelRaisedBrush").Color;
        // Same hue at zero alpha, so the fade does not travel through transparent white.
        var idle = System.Windows.Media.Color.FromArgb(0, hover.R, hover.G, hover.B);
        var background = new SolidColorBrush(idle);
        button.Background = background;

        void Fade(System.Windows.Media.Color to, int milliseconds)
        {
            if (!MotionEnabled)
            {
                background.BeginAnimation(SolidColorBrush.ColorProperty, null);
                background.Color = to;
                return;
            }
            background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = EaseOut
            });
        }

        button.MouseEnter += (_, _) => Fade(hover, 90);
        button.MouseLeave += (_, _) => Fade(idle, 130);
        button.GotKeyboardFocus += (_, _) => Fade(hover, 90);
        button.LostKeyboardFocus += (_, _) => Fade(idle, 130);
        button.Click += (_, _) =>
        {
            if (chevron)
            {
                action();
                return;
            }
            CloseSafely();
            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        };
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

    private static FrameworkElement CreateBrandHeader()
    {
        var header = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 5, 10, 5)
        };
        header.Children.Add(new System.Windows.Controls.Image
        {
            Source = TokenBurnIconResources.LoadTrayMenuIcon(),
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 7, 0),
            SnapsToDevicePixels = true
        });
        header.Children.Add(new TextBlock
        {
            Text = "TokenBurn",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return header;
    }

    private static Grid CreateMonitorRow(string label, bool selected)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new TextBlock
        {
            Text = selected ? "●" : string.Empty,
            FontSize = 9,
            Foreground = Brush("AccentBlueBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(marker);
        grid.Children.Add(text);
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
        // NotifyIcon and Screen report physical pixels. WPF Window.Left/Top use device
        // independent pixels. Mixing the two made the menu clamp to the wrong edge on a 125%
        // display, which is exactly where the tray lives on the development machine.
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = Math.Max(.1, dpi.DpiScaleX);
        var scaleY = Math.Max(.1, dpi.DpiScaleY);
        var anchorX = anchor.X / scaleX;
        var anchorY = anchor.Y / scaleY;
        var areaLeft = area.Left / scaleX;
        var areaTop = area.Top / scaleY;
        var areaRight = area.Right / scaleX;
        var areaBottom = area.Bottom / scaleY;
        var left = Math.Clamp(anchorX - width + 10, areaLeft + 8, areaRight - width - 8);
        var preferredTop = anchorY - height - 8;
        _openedUpward = preferredTop >= areaTop + 8;
        var top = _openedUpward ? preferredTop : anchorY + 8;
        top = Math.Clamp(top, areaTop + 8, areaBottom - height - 8);
        Left = left;
        Top = top;
        Activate();
        Keyboard.Focus(this);
        _menuReady = true;
        PlayEntrance();
    }

    /// <summary>
    /// The menu used to be assigned Opacity = 1 directly, so it blinked into existence. It now
    /// emerges from the tray icon: a menu placed above the anchor rises into position, one placed
    /// below descends. The direction is the same above/below decision PositionNear just made.
    /// </summary>
    private void PlayEntrance()
    {
        if (!MotionEnabled)
        {
            Opacity = 1;
            return;
        }
        Opacity = 0;
        _surfaceSlide.Y = _openedUpward ? 6 : -6;
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = EaseOut
        });
        _surfaceSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(110),
            EasingFunction = EaseOut
        });
    }

    /// <summary>
    /// Expanding the monitor list used to snap the window to its new size under the cursor. The
    /// height animates, and when the menu opened upward Top moves with it so the bottom edge stays
    /// pinned to the tray instead of the panel growing down over the taskbar.
    /// </summary>
    private void ToggleMonitorPanel()
    {
        if (_monitorPanelAnimating) return;
        var expanding = _monitorPanel.Visibility != Visibility.Visible;
        if (!MotionEnabled)
        {
            _monitorPanel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            _monitorPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            _monitorPanel.MaxHeight = double.PositiveInfinity;
            _monitorPanel.Opacity = 1;
            return;
        }

        double target;
        if (expanding)
        {
            _monitorPanel.MaxHeight = 0;
            _monitorPanel.Opacity = 0;
            _monitorPanel.Visibility = Visibility.Visible;
            _monitorPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            target = _monitorPanel.DesiredSize.Height;
        }
        else
        {
            target = 0;
        }

        var delta = expanding ? target : -_monitorPanel.ActualHeight;
        _monitorPanelAnimating = true;
        var duration = TimeSpan.FromMilliseconds(160);

        var height = new DoubleAnimation
        {
            From = expanding ? 0 : _monitorPanel.ActualHeight,
            To = target,
            Duration = duration,
            EasingFunction = EaseOut
        };
        height.Completed += (_, _) =>
        {
            _monitorPanelAnimating = false;
            if (expanding)
            {
                // Release the cap so the panel can size itself normally afterwards.
                _monitorPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                _monitorPanel.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                _monitorPanel.Visibility = Visibility.Collapsed;
            }
        };
        _monitorPanel.BeginAnimation(FrameworkElement.MaxHeightProperty, height);
        // Opacity trails the height on the way in and leads it on the way out, so the rows never
        // look squashed against the edge of the panel.
        _monitorPanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = expanding ? 1 : 0,
            BeginTime = TimeSpan.FromMilliseconds(expanding ? 40 : 0),
            Duration = TimeSpan.FromMilliseconds(expanding ? 120 : 90),
            EasingFunction = EaseOut
        });

        if (_openedUpward && Math.Abs(delta) > 0.5)
        {
            BeginAnimation(TopProperty, new DoubleAnimation
            {
                To = Top - delta,
                Duration = duration,
                EasingFunction = EaseOut
            });
        }
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
