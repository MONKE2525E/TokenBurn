using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace UsageMonitor.Desktop;

/// <summary>
/// Gives the compact quota meter the same quiet interpolation as OpenUsage's SwiftUI capsule.
/// The fill width is still owned by <see cref="ProgressWidthConverter"/>; this behavior only
/// animates the presentation value after WPF has resolved that binding, so resizing and DPI
/// changes continue to use the real track width.
/// </summary>
public static class ProgressAnimationBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ProgressAnimationBehavior),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject target, bool value)
        => target.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject target)
        => (bool)target.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfProgressBar bar) return;
        if (e.NewValue is true)
        {
            bar.ValueChanged += OnValueChanged;
            bar.Loaded += OnLoaded;
            bar.Unloaded += OnUnloaded;
        }
        else
        {
            bar.ValueChanged -= OnValueChanged;
            bar.Loaded -= OnLoaded;
            bar.Unloaded -= OnUnloaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is WpfProgressBar bar) Animate(bar, immediate: true);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfProgressBar bar) return;
        var indicator = bar.Template.FindName("PART_Indicator", bar) as FrameworkElement;
        indicator?.BeginAnimation(FrameworkElement.WidthProperty, null);
    }

    private static void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is WpfProgressBar bar) Animate(bar, immediate: false);
    }

    private static void Animate(WpfProgressBar bar, bool immediate)
    {
        if (!bar.IsLoaded || bar.ActualWidth <= 0) return;
        if (bar.Template.FindName("PART_Indicator", bar) is not FrameworkElement indicator) return;

        var maximum = bar.Maximum;
        var fraction = maximum <= 0 || double.IsNaN(maximum)
            ? 0
            : Math.Clamp(bar.Value / maximum, 0, 1);
        var target = bar.ActualWidth * fraction;
        var current = double.IsNaN(indicator.ActualWidth) ? 0 : indicator.ActualWidth;
        if (immediate || Math.Abs(current - target) < 0.5)
        {
            indicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            return;
        }

        indicator.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        }, HandoffBehavior.SnapshotAndReplace);
    }
}
