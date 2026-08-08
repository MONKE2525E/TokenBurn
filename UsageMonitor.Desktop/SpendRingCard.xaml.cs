using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UsageMonitor.LocalApi;
using WpfBrush = System.Windows.Media.Brush;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace UsageMonitor.Desktop;

/// <summary>OpenUsage-style spend summary card with local-history-only data.</summary>
public partial class SpendRingCard : WpfUserControl
{
    private IReadOnlyList<UsageSnapshotData> _snapshots = Array.Empty<UsageSnapshotData>();
    private IReadOnlyDictionary<string, string> _colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public SpendRingCard()
    {
        InitializeComponent();
        DataContext = this;
        PeriodPicker.SelectedIndex = 2;
        MetricPicker.SelectedIndex = 0;
        ThirtyDaysSegment.IsChecked = true;
        LegendItems = new ObservableCollection<SpendRingLegendItem>();
        Legend.ItemsSource = LegendItems;
        Loaded += (_, _) => Rebuild();
    }

    public ObservableCollection<SpendRingLegendItem> LegendItems { get; }

    public SpendRingPeriod Period { get; private set; } = SpendRingPeriod.ThirtyDays;
    public SpendRingMetric Metric { get; private set; } = SpendRingMetric.Cost;

    public void SetSnapshots(IEnumerable<UsageSnapshotData>? snapshots,
        IReadOnlyDictionary<string, string>? colors = null)
    {
        _snapshots = (snapshots ?? Array.Empty<UsageSnapshotData>()).ToArray();
        _colors = colors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Rebuild();
    }

    public SpendRingSummary CurrentSummary { get; private set; } = SpendRingModel.Build(null);

    public event EventHandler<SpendRingSummary>? SummaryChanged;
    public event EventHandler? ShareRequested;

    public void SetMetric(SpendRingMetric metric)
    {
        var index = metric switch
        {
            SpendRingMetric.CostPerMillionTokens => 1,
            SpendRingMetric.Tokens => 2,
            _ => 0
        };
        Metric = metric;
        if (MetricPicker.SelectedIndex != index)
            MetricPicker.SelectedIndex = index;
        else
        {
            Rebuild(animate: true);
        }
    }

    private void PeriodPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PeriodPicker.SelectedIndex < 0) return;
        Period = PeriodPicker.SelectedIndex switch
        {
            0 => SpendRingPeriod.Today,
            1 => SpendRingPeriod.Yesterday,
            _ => SpendRingPeriod.ThirtyDays
        };
        UpdatePeriodSegments();
        Rebuild(animate: true);
    }

    private void TodaySegment_OnClick(object sender, RoutedEventArgs e) => SetPeriod(SpendRingPeriod.Today);
    private void YesterdaySegment_OnClick(object sender, RoutedEventArgs e) => SetPeriod(SpendRingPeriod.Yesterday);
    private void ThirtyDaysSegment_OnClick(object sender, RoutedEventArgs e) => SetPeriod(SpendRingPeriod.ThirtyDays);

    private void SetPeriod(SpendRingPeriod period)
    {
        Period = period;
        var index = period switch
        {
            SpendRingPeriod.Today => 0,
            SpendRingPeriod.Yesterday => 1,
            _ => 2
        };
        if (PeriodPicker.SelectedIndex != index)
            PeriodPicker.SelectedIndex = index;
        else
        {
            UpdatePeriodSegments();
            Rebuild(animate: true);
        }
    }

    private void UpdatePeriodSegments()
    {
        TodaySegment.IsChecked = Period == SpendRingPeriod.Today;
        YesterdaySegment.IsChecked = Period == SpendRingPeriod.Yesterday;
        ThirtyDaysSegment.IsChecked = Period == SpendRingPeriod.ThirtyDays;
    }

    private void ShareButton_OnClick(object sender, RoutedEventArgs e)
        => ShareRequested?.Invoke(this, EventArgs.Empty);

    private void MetricPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || MetricPicker.SelectedIndex < 0) return;
        Metric = MetricPicker.SelectedIndex switch
        {
            1 => SpendRingMetric.CostPerMillionTokens,
            2 => SpendRingMetric.Tokens,
            _ => SpendRingMetric.Cost
        };
        MetricLabel.Text = Metric switch
        {
            SpendRingMetric.CostPerMillionTokens => "Cost/MTok",
            SpendRingMetric.Tokens => "Tokens",
            _ => "Cost"
        };
        Rebuild(animate: true);
    }

    private void Rebuild(bool animate = false)
    {
        if (!IsInitialized) return;
        CurrentSummary = SpendRingModel.Build(_snapshots, Period, Metric, colors: _colors);
        Ring.Summary = CurrentSummary;
        if (animate && IsLoaded)
        {
            Ring.AnimationProgress = 0;
            Ring.BeginAnimation(SpendRingCanvas.AnimationProgressProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            Ring.BeginAnimation(SpendRingCanvas.AnimationProgressProperty, null);
            Ring.AnimationProgress = 1;
        }
        LegendItems.Clear();
        foreach (var segment in CurrentSummary.Segments.Take(5))
            LegendItems.Add(new SpendRingLegendItem(segment.DisplayName, FormatLegendValue(segment), segment.Color));
        EmptyText.Visibility = CurrentSummary.HasData ? Visibility.Collapsed : Visibility.Visible;
        EstimateNotice.Visibility = CurrentSummary.HasEstimatedValues ? Visibility.Visible : Visibility.Collapsed;
        SummaryChanged?.Invoke(this, CurrentSummary);
        if (animate) AnimateSpendContent();
    }

    /// <summary>
    /// Mirrors OpenUsage's springy ring/legend morph without animating layout. The WPF canvas
    /// redraws synchronously when the selected period or metric changes, so a short scale/opacity
    /// settle makes the new slice ordering legible while keeping the controls immediately usable.
    /// </summary>
    private void AnimateSpendContent()
    {
        if (!IsLoaded) return;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        SpendContent.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0.72,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(170),
            EasingFunction = easing
        });
        SpendContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
        {
            From = 0.965,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = easing
        });
        SpendContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation
        {
            From = 0.965,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = easing
        });
    }

    private string FormatLegendValue(SpendRingSegment segment)
    {
        return Metric switch
        {
            SpendRingMetric.Tokens => SpendRingModel.FormatTokens(segment.Tokens),
            SpendRingMetric.CostPerMillionTokens => $"${segment.Value:0.00}",
            _ => segment.Value >= 1000 ? $"${segment.Value / 1000:0.0}k" : $"${segment.Value:0.00}"
        };
    }
}

public sealed record SpendRingLegendItem(string DisplayName, string ValueLabel, WpfBrush Color);
