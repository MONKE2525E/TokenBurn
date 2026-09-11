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
/// own left-aligned template so rows do not inherit the dashboard button styling.
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private const double MenuWidth = 220;
    private const double RowHeight = 28;
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
            var item = CreateRow(monitor.DisplayName, () =>
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
        // Keep the tray surface task-focused. The icon and version belong to the dashboard,
        // not to every transient shell menu, so the menu can stay close to the compact Windows
        // context-menu proportions.
        content.Children.Add(CreateRow("Open dashboard", actions.OpenDashboard));
        content.Children.Add(CreateRow("Refresh now", actions.Refresh));
        var taskbarButton = CreateRow("Taskbar display", ToggleMonitorPanel, chevron: true);
        content.Children.Add(taskbarButton);
        content.Children.Add(_monitorPanel);
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateRow("Settings", actions.Settings));
        content.Children.Add(CreateRow("Customize", actions.Customize));
        content.Children.Add(CreateSeparator());
        content.Children.Add(CreateRow("Quit", actions.Quit));

        _surface = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("PanelStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Padding = new Thickness(4, 5, 4, 5),
            Child = content,
            RenderTransform = _surfaceSlide,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = 0.28,
                BlurRadius = 12,
                ShadowDepth = 2
            }
        };
        Content = _surface;
        Loaded += (_, _) => PositionNear(anchor);
        SourceInitialized += (_, _) => SetScreenShareExcluded(actions.HideFromScreenShare);
    }

    private WpfButton CreateRow(string label, Action action, bool chevron = false,
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
            Padding = new Thickness(9, 4, 9, 4),
            FontSize = 13,
            Foreground = Brush("TextPrimaryBrush"),
            Focusable = true,
            FocusVisualStyle = null,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = customContent ?? CreateRowContent(label, chevron)
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
            // Let the menu finish closing before another popup or an async refresh starts. A
            // background-priority callback can race WPF focus teardown, which made tray actions
            // appear to do nothing even though the click was received.
            Dispatcher.BeginInvoke(action, DispatcherPriority.ApplicationIdle);
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

    private static Grid CreateRowContent(string label, bool chevron)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        grid.Children.Add(text);
        if (chevron)
        {
            var chevronPath = CreateIcon("M4.5 2.5 L9.5 7.5 L4.5 12.5", size: 12, thickness: 1.6);
            chevronPath.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            chevronPath.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chevronPath, 1);
            grid.Children.Add(chevronPath);
        }
        return grid;
    }

    private static Grid CreateMonitorRow(string label, bool selected)
    {
        // Keep the radio control in a stable column so monitor labels remain aligned while the
        // parent menu stays text-first.
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
            Margin = new Thickness(6, tightTop ? 1 : 4, 6, 4),
            Background = Brush("PanelStrokeBrush")
        };


    private void PositionNear(System.Drawing.Point anchor)
    {
        UpdateLayout();
        var screen = System.Windows.Forms.Screen.FromPoint(anchor);
        var area = screen.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = Math.Max(.1, dpi.DpiScaleX);
        var scaleY = Math.Max(.1, dpi.DpiScaleY);

        // Cursor and Screen.WorkingArea are physical pixels. Positioning a WPF window through
        // Left/Top here mixes those with device-independent pixels, which detaches the menu on
        // scaled displays. SetWindowPos takes physical pixels and keeps the panel on the tray.
        var edgeX = (int)Math.Round(8 * scaleX);
        var edgeY = (int)Math.Round(8 * scaleY);
        var width = (int)Math.Ceiling((ActualWidth > 0 ? ActualWidth : MenuWidth) * scaleX);
        var height = (int)Math.Ceiling((ActualHeight > 0 ? ActualHeight : 280) * scaleY);
        var left = Math.Clamp(anchor.X - width + edgeX, area.Left + edgeX, area.Right - width - edgeX);
        var preferredTop = anchor.Y - height - edgeY;
        _openedUpward = preferredTop >= area.Top + edgeY;
        var top = _openedUpward ? preferredTop : anchor.Y + edgeY;
        top = Math.Clamp(top, area.Top + edgeY, area.Bottom - height - edgeY);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
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
