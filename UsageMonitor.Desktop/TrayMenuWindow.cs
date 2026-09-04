using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using UsageMonitor.Core;
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
    Action<MonitorOption> SelectMonitor,
    bool HideFromScreenShare);

/// <summary>
/// A small native WPF tray surface that uses the same rounded charcoal language as the dashboard.
/// It intentionally does not use ContextMenuStrip, whose rectangular Win32 rendering cannot match
/// the rest of the app, and it does not reuse the app's implicit Button style: that template
/// centers its ContentPresenter, which turned every row into centered text, so rows carry their
/// own left-aligned template with leading stroke icons.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private const double MenuWidth = 248;
    private const double RowHeight = 29;
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static ControlTemplate? _rowTemplate;
    private readonly Border _surface;
    private readonly StackPanel _monitorPanel;
    private readonly List<WpfButton> _rows = [];
    private System.Windows.Shapes.Path? _taskbarChevron;
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
        SizeToContent = SizeToContent.Height;
        Width = MenuWidth;
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
            Margin = new Thickness(0, 0, 0, 2),
            Orientation = WpfOrientation.Vertical
        };
        foreach (var monitor in actions.Monitors)
        {
            var item = CreateRow(null, monitor.DisplayName, () =>
            {
                actions.SelectMonitor(monitor);
                CloseSafely();
            }, customContent: CreateMonitorRow(
                monitor.DisplayName,
                monitor.Id.Equals(actions.SelectedMonitor, StringComparison.OrdinalIgnoreCase)));
            // A real radio column rather than a "●  " string prefix: prefix alignment depended
            // on space characters in a proportional font, so marked and unmarked rows never lined up.
            _monitorPanel.Children.Add(item);
        }

        var content = new StackPanel { Orientation = WpfOrientation.Vertical };
        content.Children.Add(CreateBrandHeader());
        content.Children.Add(CreateSeparator(tightTop: true));
        content.Children.Add(CreateRow(Icons.OpenDashboard, "Open dashboard", actions.OpenDashboard));
        content.Children.Add(CreateRow(Icons.Refresh, "Refresh now", actions.Refresh));
        var taskbarButton = CreateRow(Icons.Display, "Taskbar display", ToggleMonitorPanel, chevron: true);
        content.Children.Add(taskbarButton);
        content.Children.Add(_monitorPanel);
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateRow(Icons.Sliders, "Settings", actions.Settings));
        content.Children.Add(CreateRow(Icons.Grid, "Customize", actions.Customize));
        content.Children.Add(CreateRow(Icons.Download, "Check for updates", actions.CheckForUpdates));
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateRow(Icons.Power, "Quit", actions.Quit));

        _surface = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("PanelStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Padding = new Thickness(4),
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
        SourceInitialized += (_, _) => SetScreenShareExcluded(actions.HideFromScreenShare);
    }

    private WpfButton CreateRow(string? iconData, string label, Action action, bool chevron = false,
        UIElement? customContent = null)
    {
        var button = new WpfButton
        {
            // Null the implicit style: its template centers content, which is what made the old
            // menu read as a wall of centered text, and resolving it mid-construction has
            // previously sealed styles under us. Everything the row needs is set right here.
            Style = null,
            Template = RowTemplate,
            MinHeight = RowHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
            Background = TransparentBrush,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 12,
            Foreground = Brush("TextPrimaryBrush"),
            Focusable = true,
            FocusVisualStyle = null,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = customContent ?? CreateRowContent(label, iconData, chevron)
        };
        _rows.Add(button);
        if (chevron)
        {
            // Last, not first: the row icon is also a Path and precedes the chevron in the grid.
            _taskbarChevron = ((Grid)button.Content).Children
                .OfType<System.Windows.Shapes.Path>()
                .LastOrDefault();
            if (_taskbarChevron is not null)
            {
                _taskbarChevron.RenderTransform = new RotateTransform();
                _taskbarChevron.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            }
        }

        // Cross-fade the background brush on hover instead of swapping it: same hue at zero
        // alpha while idle, so the fade never travels through transparent white.
        var hover = Brush("PanelRaisedBrush").Color;
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

    /// <summary>A left-aligned row template. The app's implicit Button template centers its
    /// ContentPresenter; menu rows must not, so they carry this one instead.</summary>
    private static ControlTemplate RowTemplate
    {
        get
        {
            if (_rowTemplate is not null) return _rowTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "row";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(WpfButton.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(WpfButton.PaddingProperty));
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            _rowTemplate = new ControlTemplate(typeof(WpfButton)) { VisualTree = border };
            return _rowTemplate;
        }
    }

    private static Grid CreateRowContent(string label, string? iconData, bool chevron)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (iconData is not null)
        {
            var icon = CreateIcon(iconData);
            grid.Children.Add(icon);
        }
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(9, 0, 8, 0)
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (chevron)
        {
            var chevronPath = CreateIcon(Icons.Chevron, size: 12, thickness: 1.6);
            chevronPath.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            chevronPath.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chevronPath, 2);
            grid.Children.Add(chevronPath);
        }
        return grid;
    }

    private static FrameworkElement CreateBrandHeader()
    {
        var header = new Grid
        {
            Margin = new Thickness(6, 3, 6, 3),
            MinHeight = 34
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new System.Windows.Controls.Image
        {
            Source = TokenBurnIconResources.LoadTrayMenuIcon(),
            Width = 18,
            Height = 18,
            Margin = new Thickness(0, 0, 8, 0),
            SnapsToDevicePixels = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(mark);
        var name = new TextBlock
        {
            Text = "TokenBurn",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var version = new TextBlock
        {
            Text = ProductInfo.Version,
            FontSize = 11,
            Foreground = Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 1, 0, 0)
        };
        Grid.SetColumn(version, 2);
        header.Children.Add(version);
        return header;
    }

    private static Grid CreateMonitorRow(string label, bool selected)
    {
        // The 25px radio column keeps monitor labels flush with the parent rows' labels
        // (16px icon + 9px gap).
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var radio = new System.Windows.Shapes.Ellipse
        {
            Width = 9,
            Height = 9,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (selected)
        {
            radio.Fill = Brush("AccentBlueBrush");
        }
        else
        {
            radio.Stroke = Brush("TextMutedBrush");
            radio.StrokeThickness = 1.2;
        }
        grid.Children.Add(radio);
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static System.Windows.Shapes.Path CreateIcon(string data, double size = 16, double thickness = 1.5)
        => new()
        {
            Data = Geometry.Parse(data),
            Stroke = Brush("TextSecondaryBrush"),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };

    private static Border CreateSeparator(bool tightTop = false)
        => new()
        {
            Height = 1,
            Margin = new Thickness(6, tightTop ? 0 : 5, 6, 5),
            Background = Brush("PanelStrokeBrush")
        };

    /// <summary>Minimal stroke glyphs drawn in a 16x16 box. Kept as plain path data so the menu
    /// needs no new assets and stays a pure-code surface.</summary>
    private static class Icons
    {
        public const string OpenDashboard =
            "M6.5 3.5 H4 A1.5 1.5 0 0 0 2.5 5 V12 A1.5 1.5 0 0 0 4 13.5 H11 A1.5 1.5 0 0 0 12.5 12 V9.5 " +
            "M9.5 2.5 H13.5 V6.5 M13.2 2.8 L7.9 8.1";
        public const string Refresh =
            "M13.5 8 A5.5 5.5 0 1 1 8 2.5 M6.1 0.9 L8 2.5 L6.1 4.1";
        public const string Display =
            "M2.5 4.5 A1.5 1.5 0 0 1 4 3 H12 A1.5 1.5 0 0 1 13.5 4.5 V9 A1.5 1.5 0 0 1 12 10.5 H4 " +
            "A1.5 1.5 0 0 1 2.5 9 Z M8 10.5 V13.2 M5.2 13.2 H10.8";
        public const string Sliders =
            "M2.5 4.5 H8.4 M12.2 4.5 H13.5 M2.5 8 H3.8 M7.6 8 H13.5 M2.5 11.5 H9.7 " +
            "M10.3 2.9 A1.6 1.6 0 1 1 10.3 6.1 A1.6 1.6 0 1 1 10.3 2.9 Z " +
            "M5.7 6.4 A1.6 1.6 0 1 1 5.7 9.6 A1.6 1.6 0 1 1 5.7 6.4 Z " +
            "M11.6 9.9 A1.6 1.6 0 1 1 11.6 13.1 A1.6 1.6 0 1 1 11.6 9.9 Z";
        public const string Grid =
            "M2.5 2.5 H6.8 V6.8 H2.5 Z M9.2 2.5 H13.5 V6.8 H9.2 Z " +
            "M2.5 9.2 H6.8 V13.5 H2.5 Z M9.2 9.2 H13.5 V13.5 H9.2 Z";
        public const string Download =
            "M8 2.5 V9.8 M4.9 6.7 L8 9.8 L11.1 6.7 M3 13.2 H13";
        public const string Power =
            "M8 2.2 V7.6 M4.46 5.26 A5 5 0 1 0 11.54 5.26";
        public const string Chevron =
            "M4.5 2.5 L9.5 7.5 L4.5 12.5";
    }

    private void PositionNear(System.Drawing.Point anchor)
    {
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromPoint(anchor);
        var area = screen.WorkingArea;
        var width = ActualWidth > 0 ? ActualWidth : MenuWidth;
        var height = ActualHeight > 0 ? ActualHeight : 380;
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

        RotateChevron(expanding, duration);
    }

    /// <summary>The disclosure chevron turns to point down while the monitor list is open, so the
    /// row reads as expanded rather than as a dead-end.</summary>
    private void RotateChevron(bool expanded, Duration duration)
    {
        if (_taskbarChevron?.RenderTransform is not RotateTransform rotate) return;
        if (!MotionEnabled)
        {
            rotate.Angle = expanded ? 90 : 0;
            return;
        }
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            To = expanded ? 90 : 0,
            Duration = duration,
            EasingFunction = EaseOut
        });
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSafely();
            return;
        }
        if (e.Key is not (Key.Up or Key.Down)) return;
        e.Handled = true;
        // Arrow keys walk the rows like a real menu. Collapsed monitor rows are skipped, and
        // both directions wrap so the list never dead-ends.
        var visible = _rows.Where(row => row.IsVisible).ToList();
        if (visible.Count == 0) return;
        var focused = Keyboard.FocusedElement as WpfButton;
        var index = focused is null ? -1 : visible.IndexOf(focused);
        var next = e.Key == Key.Down
            ? visible[(index + 1 + visible.Count) % visible.Count]
            : visible[(index <= 0 ? visible.Count - 1 : index - 1) % visible.Count];
        next.Focus();
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
