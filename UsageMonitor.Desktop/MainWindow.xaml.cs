using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Globalization;
using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http;
using Microsoft.Win32;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfButton = System.Windows.Controls.Button;
using WpfPasswordBox = System.Windows.Controls.PasswordBox;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using UsageMonitor.Core;
using UsageMonitor.Core.Providers.Claude;
using UsageMonitor.Core.Providers.Codex;
using UsageMonitor.Core.Providers.Antigravity;
using UsageMonitor.Core.Providers.OpenCode;
using UsageMonitor.LocalApi;
using DrawingPoint = System.Drawing.Point;

namespace UsageMonitor.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProviderCardDisplay> _providerCards = new();
    private TaskbarOverlayController? _taskbar;
    private TrayIconService? _tray;
    private TauriPopupBridge? _tauriPopup;
    private UserSettings _settings = UserSettings.Default;
    private CoreUsageSnapshotSource? _snapshotSource;
    private JsonFileUsageCache? _cache;
    private ISecretStore? _secretStore;
    private CachedModelCatalog? _modelCatalog;
    private readonly ResetNotificationScheduler _resetNotifications = new();
    private DateTimeOffset _sessionStartedAt = DateTimeOffset.UtcNow;
    private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
    private Task? _refreshTask;
    private bool _refreshInFlight;
    private bool _forceRefreshQueued;
    private bool _refreshLoopStarted;
    // Provider refreshes have their own deadlines, but the desktop lifecycle also needs a
    // deadline around the complete snapshot batch. This is the final guard against a provider,
    // cache, or coordination task leaving the visible dashboard in "Refreshing..." forever.
    internal static readonly TimeSpan DesktopRefreshTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DesktopRefreshRetryDelay = TimeSpan.FromMinutes(1);
    // Keep the last successful provider envelope in memory so an auth or network failure cannot
    // erase a previously visible quota bar during the next five-minute refresh.
    private readonly Dictionary<string, UsageSnapshotData> _lastGoodSnapshots = new(StringComparer.OrdinalIgnoreCase);
    // Providers that already surfaced a re-authentication notification for the current failure
    // episode. Cleared when the provider recovers so a future failure notifies again.
    private readonly HashSet<string> _authFailureNotified = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<UsageSnapshotData> _latestSnapshots = Array.Empty<UsageSnapshotData>();
    private DateTimeOffset _nextRefreshAt = DateTimeOffset.Now;
    internal IUsageProviderCatalog? SnapshotCatalog { get; private set; }
    internal IUsageCache? SnapshotCache => _cache;
    internal CoreUsageSnapshotSource? SnapshotSource => _snapshotSource;
    private bool _allowShutdown;

    public MainWindow()
    {
        // Apply the persisted palette before XAML resolves StaticResource brushes.
        // WPF freezes those resources for performance once the visual tree exists.
        _settings = SettingsStore.Load();
        InitializeComponent();
        Icon = TokenBurnIconResources.LoadWpfAppIcon();
        TopMetricPicker.SelectedIndex = SpendMetricIndex(_settings.SpendMetric);
        ProviderCards = _providerCards;
        DataContext = this;
        // Keep the timer on a one-second heartbeat and derive the next refresh from one
        // timestamp. A five-minute DispatcherTimer drifts after a manual refresh, which made
        // the embedded strip and the popup disagree about when the next update would happen.
        _refreshTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshTimerTick();
        Closed += (_, _) => _refreshTimer.Stop();
        SpendCard.ShareRequested += (_, _) => ShareScreenshotButton_OnClick(SpendCard, new RoutedEventArgs());
        StateChanged += MainWindow_OnStateChanged;
        Activated += MainWindow_OnActivated;
        Deactivated += MainWindow_OnDeactivated;
        SourceInitialized += (_, _) =>
        {
            ApplyScreenSharePrivacy();
        };
    }

    public ObservableCollection<ProviderCardDisplay> ProviderCards { get; }

    internal DateTimeOffset NextRefreshAt => _nextRefreshAt;
    internal bool IsRefreshInFlight => _refreshInFlight;

    private void RefreshTimerTick()
    {
        UpdateRefreshCountdown();
        _resetNotifications.Tick(DateTimeOffset.UtcNow, notification =>
            _tray?.ShowQuotaNotification($"{notification.DisplayName} {notification.MetricLabel} reset."));
        // A reset notification fires for user awareness; the quota data is refreshed by the
        // scheduled stale-while-revalidate cycle. A forced refresh here used to re-run every
        // provider's full network + history work on each reset boundary and immediately after
        // startup, which is where the multi-second forced refreshes and the CPU spikes came from.
        if (!_refreshInFlight && DateTimeOffset.Now >= _nextRefreshAt)
            RefreshData();
    }

    private void UpdateRefreshCountdown()
    {
        if (_refreshInFlight)
        {
            LastUpdatedText.Text = "Refreshing...";
            return;
        }

        var remaining = _nextRefreshAt - DateTimeOffset.Now;
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        var compact = seconds >= 60
            ? $"{Math.Max(1, (int)Math.Ceiling(seconds / 60d))}m"
            : $"{seconds}s";
        LastUpdatedText.Text = $"Next update in {compact}";
    }

    internal static readonly IReadOnlyList<string> CustomizableMetricNames =
        [
            "claude-code:session", "claude-code:weekly",
            "codex:session", "codex:weekly",
            "antigravity:session", "antigravity:weekly", "antigravity:claude weekly", "antigravity:claude",
            "cursor:usage", "copilot:credits", "devin:daily", "devin:weekly", "grok:weekly",
            "opencode:session", "opencode:weekly", "opencode:monthly"
        ];

    public void Initialize(UserSettings settings, TaskbarOverlayController taskbar, TrayIconService tray, TauriPopupBridge? tauriPopup = null)
    {
        _settings = settings.Clone();
        _taskbar = taskbar;
        _tray = tray;
        _tauriPopup = tauriPopup;
        _taskbar.StateChanged += (_, state) =>
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => UpdateTaskbarSurfaceStatus(state));
                return;
            }
            UpdateTaskbarSurfaceStatus(state);
        };
        var logger = FileDiagnosticsLogger.Default;
        _modelCatalog = new CachedModelCatalog(pricingDirectory: UsageMonitorPaths.Current.PricingDirectory, logger: logger);
        _ = WarmModelCatalogAsync(_modelCatalog);
        var catalog = ProviderCatalog.CreateDefault([
            new CodexProvider(catalog: _modelCatalog),
            new ClaudeProvider(catalog: _modelCatalog),
            new AntigravityProvider(),
            new OpenCodeProvider()
        ]);
        SnapshotCatalog = catalog;
        _cache = new JsonFileUsageCache(logger: logger);
        _secretStore = new CredentialManagerSecretStore(logger: logger);
        _snapshotSource = new CoreUsageSnapshotSource(catalog, _cache,
            new ProviderContext
            {
                Secrets = _secretStore,
                Logger = logger,
                ModelCatalog = _modelCatalog,
                CacheDirectory = UsageMonitorPaths.Current.CacheDirectory
            });
        ApplySettings(_settings);
        StartRefreshLoop();
    }

    private void StartRefreshLoop()
    {
        if (_refreshLoopStarted) return;
        _refreshLoopStarted = true;
        _refreshTimer.Start();
        RefreshData();
    }

    private void UpdateTaskbarSurfaceStatus(TaskbarStateChangedEventArgs state)
    {
        if (_settings.StatusSurface != StatusSurfaceMode.TaskbarWidget) return;
        SurfaceFooterText.Text = state.Attached
            ? "Taskbar status strip active + tray"
            : "Tray fallback / taskbar strip retrying";
    }

    public void ApplySettings(UserSettings settings)
    {
        _settings = settings.Clone();
        _settings.StatusSurface = StatusSurfaceMode.TaskbarWidget;
        _resetNotifications.Observe(_latestSnapshots, _settings.NotificationsEnabled,
            _settings.NotificationProviderIds, DateTimeOffset.UtcNow);
        ApplyScreenSharePrivacy();
        _taskbar?.ApplyScreenSharePrivacy(_settings.HideFromScreenShare);
        _taskbar?.ApplyPositionLock(_settings.TaskbarPositionLocked);
            ApplyDensity(true);
        SpendCard.SetMetric(ParseSpendMetric(_settings.SpendMetric));
        SurfaceFooterText.Text = "Taskbar status strip + tray";
        PrivacyFooter.Text = _settings.NotificationsEnabled ? "Local cache / loopback API only / alerts enabled" : "Local cache / loopback API only";
        SpendCard.Visibility = Visibility.Visible;
        // The taskbar button and tray own dismissal when the dashboard is linked to a
        // shell surface. Keeping duplicate Windows caption controls in that mode made the
        // compact popover look unlike the dashboard and encouraged accidental app termination.
        MinimizeButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Collapsed;
    }

    private void ApplyDensity(bool compact)
    {
        // The macOS popover stays deliberately small. Keep a readable floor for Windows DPI
        // scaling, but make the existing compact-density setting affect the actual shell size
        // instead of only changing a preference that the layout ignored.
        Width = compact ? 440 : 500;
        Height = compact ? 600 : 680;
        MinWidth = compact ? 400 : 440;
        MinHeight = compact ? 480 : 520;
        MaxWidth = compact ? 540 : 600;
        MaxHeight = compact ? 740 : 820;
    }

    public void ShowFromTray() => ShowFromTray(null);

    public void ShowFromTray(DrawingPoint? requestedAnchor, bool useWidgetAvoidRect = true)
        => _ = ShowFromTrayAsync(requestedAnchor, useWidgetAvoidRect);

    /// <summary>
    /// Opens the compact Tauri popup at the requested anchor. Returns true when the popup host
    /// accepted the show. The wait for a slow first launch happens off the UI thread, so the
    /// taskbar-strip WndProc (and with it the whole dispatcher) is never blocked.
    /// </summary>
    public async Task<bool> ShowFromTrayAsync(DrawingPoint? requestedAnchor, bool useWidgetAvoidRect = true)
    {
        try
        {
            if (_tauriPopup is not null &&
                await _tauriPopup.TryShowAsync(requestedAnchor ?? ResolvePopupAnchor(),
                    useWidgetAvoidRect ? GetWidgetAvoidRect() : null).ConfigureAwait(true))
            {
                // The WPF dashboard remains loaded as a fallback and for settings, but it must not
                // compete with the Tauri popover or create a second taskbar button.
                if (IsVisible && WindowState == WindowState.Normal)
                    WindowState = WindowState.Minimized;
                return true;
            }
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("The compact dashboard could not be shown", exception: exception);
        }
        // Do not reveal the legacy WPF dashboard. It is an integration host for the native
        // taskbar strip and tray only; presenting it as a fallback made two wildly different
        // dashboards compete for the same action. The notification fires on both a failed show
        // and a thrown exception so the user always gets feedback.
        _tray?.ShowFallbackNotification("The compact dashboard could not be opened. TokenBurn is still running in the tray; try again in a moment.");
        return false;
    }

    internal Task<bool> TryToggleTauriPopupAsync(DrawingPoint anchor, bool useWidgetAvoidRect = true)
        => _tauriPopup is not null
            ? _tauriPopup.TryToggleAsync(anchor, useWidgetAvoidRect ? GetWidgetAvoidRect() : null)
            : Task.FromResult(false);

    internal async void ToggleFromTaskbarIndicator(DrawingPoint anchor)
    {
        try
        {
            // Use the same native toggle as the tray. Focus loss can hide the popup just before this
            // mouse-up reaches the overlay; the Rust-side suppression window treats that race as one
            // dismissal instead of hiding and immediately showing the popup again.
            if (!await TryToggleTauriPopupAsync(anchor).ConfigureAwait(true))
                await ShowFromTrayAsync(anchor).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("The taskbar indicator could not toggle the popup", exception: exception);
        }
    }

    private DrawingPoint ResolvePopupAnchor()
    {
        // Anchor to the indicator the user actually clicked. This keeps dragged taskbar strips,
        // the supported taskbar button, and tray clicks in the same coordinate space. The widget
        // bounds remain a fallback for the brief interval when Explorer moves the cursor during a
        // shell recreation.
        if (NativeMethods.GetCursorPos(out var cursor))
            return new DrawingPoint(cursor.X, cursor.Y);
        if (_taskbar?.TryGetWidgetBounds(out var widgetBounds) == true && !widgetBounds.IsEmpty)
            return new DrawingPoint(widgetBounds.Left + widgetBounds.Width / 2, widgetBounds.Top + widgetBounds.Height / 2);
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return new DrawingPoint(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
    }

    private System.Drawing.Rectangle? GetWidgetAvoidRect()
        => _taskbar?.TryGetWidgetBounds(out var bounds) == true && !bounds.IsEmpty ? bounds : null;

    private void MainWindow_OnStateChanged(object? sender, EventArgs e)
    {
        // A normal WPF taskbar button restores the window without going through ShowFromTray().
        // Re-assert activation here so a click on the supported taskbar target never leaves the
        // dashboard behind another window, especially after Explorer has recreated its button.
        if (WindowState != WindowState.Normal || !IsVisible) return;
        if (_settings.UseTauriPopup && _tauriPopup is not null)
        {
            // The WPF host is intentionally minimized while the compact Tauri popup is active.
            // Restoring it from a jump-list/taskbar launch should open the same popup rather than
            // expose a second oversized dashboard window.
            Dispatcher.BeginInvoke(new Action(() => ShowFromTray(ResolvePopupAnchor())),
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (WindowState != WindowState.Normal || !IsVisible) return;
            try
            {
                PositionPopoverNearTaskbar();
                Activate();
                Focus();
                PlayEntranceAnimation();
            }
            catch (InvalidOperationException) { }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void MainWindow_OnActivated(object? sender, EventArgs e)
    {
        // The WPF host is never a quota taskbar surface. The custom overlay owns that job.
    }

    private void MainWindow_OnDeactivated(object? sender, EventArgs e)
    {
        // The dashboard is a popover, not a normal document window. Clicking elsewhere dismisses
        // it while the monitor keeps running in the tray and taskbar strip.
        if (WindowState != WindowState.Normal || !IsVisible)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsActive || WindowState != WindowState.Normal || !IsVisible) return;
            if (OptionsButton.ContextMenu?.IsOpen == true || TopMetricPicker.IsDropDownOpen) return;
            if (OwnedWindows.Cast<Window>().Any(window => window.IsVisible)) return;
            WindowState = WindowState.Minimized;
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ChromeBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        if (IsChromeControl(e.OriginalSource as DependencyObject)) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private static bool IsChromeControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.ComboBox)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // The compact dashboard behaves like the original popover: Escape dismisses it,
        // but does not terminate the monitor. Context menus and native dialogs consume
        // Escape first, so this only runs when the dashboard itself has focus.
        if (e.Key != Key.Escape || !IsVisible || WindowState == WindowState.Minimized) return;
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void InfoButton_OnClick(object sender, RoutedEventArgs e)
    {
        // The small info affordance explains the spend scope; the full About dialog
        // remains in Options.  A transient ToolTip keeps this interaction non-modal and lets the
        // user continue changing the period or metric without losing the dashboard context.
        var tip = new System.Windows.Controls.ToolTip
        {
            Content = "Spend includes local provider history only. Estimates are labeled and are not a bill.",
            PlacementTarget = InfoButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            HasDropShadow = true,
            FontSize = 12,
            Padding = new Thickness(10, 7, 10, 7),
            MaxWidth = 280
        };
        ToolTipService.SetShowDuration(tip, 3200);
        tip.IsOpen = true;

        // Dispatcher.BeginInvoke does not have a delay overload. Passing the TimeSpan as
        // the third argument queues it as an Action parameter and crashes WPF with
        // TargetParameterCountException when the dispatcher invokes the callback.
        var dismissTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.2),
            IsEnabled = true
        };
        dismissTimer.Tick += (_, _) =>
        {
            dismissTimer.Stop();
            tip.IsOpen = false;
        };
    }

    private void TopMetricPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TopMetricPicker.SelectedIndex < 0) return;
        var metric = TopMetricPicker.SelectedIndex switch
        {
            1 => "cost-mtok",
            2 => "tokens",
            _ => "cost"
        };
        _settings.SpendMetric = metric;
        App.CurrentApp.SaveSettings(_settings);
        SpendCard.SetMetric(ParseSpendMetric(metric));
    }

    internal bool ApplySpendMetric(string metric)
    {
        var normalized = SettingsMigration.NormalizeSpendMetric(metric);
        _settings.SpendMetric = normalized;
        App.CurrentApp.SaveSettings(_settings);
        SpendCard.SetMetric(ParseSpendMetric(normalized));
        return true;
    }

    private static int SpendMetricIndex(string? metric)
        => SettingsMigration.NormalizeSpendMetric(metric) switch
        {
            "cost-mtok" => 1,
            "tokens" => 2,
            _ => 0
        };

    private static SpendRingMetric ParseSpendMetric(string? metric)
        => SettingsMigration.NormalizeSpendMetric(metric) switch
        {
            "cost-mtok" => SpendRingMetric.CostPerMillionTokens,
            "tokens" => SpendRingMetric.Tokens,
            _ => SpendRingMetric.Cost
        };

    private void MetricValueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MetricDisplay metric } || !metric.IsMeter) return;
        _settings.UsageDisplay = _settings.UsageDisplay.Equals("Remaining", StringComparison.OrdinalIgnoreCase)
            ? "Used"
            : "Remaining";
        App.CurrentApp.SaveSettings(_settings);
        RefreshData();
    }

    private void MetricDetailButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MetricDisplay metric } || !metric.IsMeter) return;
        _settings.ResetTimeDisplay = _settings.ResetTimeDisplay.Equals("Exact time", StringComparison.OrdinalIgnoreCase)
            ? "Countdown"
            : "Exact time";
        App.CurrentApp.SaveSettings(_settings);
        RefreshData();
    }

    private void PlayEntranceAnimation()
    {
        if (!IsLoaded) return;
        DashboardSurface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        if (DashboardSurface.RenderTransform is TranslateTransform slide)
        {
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
            {
                From = 10,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    public async void RefreshData(bool force = false, string? reason = null)
    {
        try
        {
            await RefreshDataAsync(force, reason);
        }
        catch (Exception exception)
        {
            // RefreshData is called by WPF event/timer callbacks, so an exception escaping this
            // async-void boundary can terminate the dispatcher and take the popup down with it.
            FileDiagnosticsLogger.Default.Warning("Desktop refresh task escaped its failure handler",
                new Dictionary<string, object?>
                {
                    ["force"] = force,
                    ["reason"] = reason
                }, exception);
        }
    }

    internal Task RefreshDataAsync(bool force = false, string? reason = null)
    {
        if (_refreshTask is { IsCompleted: false })
        {
            // A popup refresh or post-login refresh can race the five-minute schedule. Joining the
            // in-flight run is correct; record it so a support bundle can tell the two apart.
            FileDiagnosticsLogger.Default.Debug("Refresh already in flight; joining the existing refresh",
                new Dictionary<string, object?> { ["reason"] = reason, ["force"] = force });
            // A force request must not silently degrade into the joined (possibly cache-served)
            // run: promote the intent so a real network refresh follows once this one finishes.
            if (force) _forceRefreshQueued = true;
            return _refreshTask;
        }
        _refreshTask = RefreshDataCoreAsync(force, reason);
        return _refreshTask;
    }

    private async Task RefreshDataCoreAsync(bool force, string? reason)
    {
        _refreshInFlight = true;
        RefreshButton.IsEnabled = false;
        UpdateRefreshCountdown();
        var stopwatch = Stopwatch.StartNew();
        // One id links the started/completed envelope with every provider and scanner entry.
        var refreshId = Guid.NewGuid().ToString("N")[..8];
        FileDiagnosticsLogger.Default.Info("Provider refresh started",
            new Dictionary<string, object?>
            {
                ["force"] = force,
                ["reason"] = reason,
                ["refreshId"] = refreshId
            });
        try
        {
            IReadOnlyList<UsageSnapshotData> snapshots;
            if (_snapshotSource is null)
            {
                snapshots = Array.Empty<UsageSnapshotData>();
            }
            else
            {
                CancellationTokenSource? refreshCancellation = new();
                var snapshotTask = _snapshotSource.GetSnapshotsAsync(
                    null, force, refreshCancellation.Token, refreshId);
                try
                {
                    snapshots = await snapshotTask.WaitAsync(DesktopRefreshTimeout);
                }
                catch (TimeoutException) when (!snapshotTask.IsCompleted)
                {
                    refreshCancellation.Cancel();
                    ObserveDetachedTask(snapshotTask, refreshCancellation);
                    refreshCancellation = null;
                    throw new TimeoutException(
                        $"The desktop refresh exceeded its {DesktopRefreshTimeout.TotalSeconds:0}-second deadline.");
                }
                finally
                {
                    refreshCancellation?.Dispose();
                }
            }
            var snapshotFetchMs = stopwatch.ElapsedMilliseconds;
            var effectiveSnapshots = snapshots.Select(snapshot =>
            {
                if (snapshot.Error is null)
                {
                    _lastGoodSnapshots[snapshot.ProviderId] = snapshot;
                    return snapshot;
                }

                if (_lastGoodSnapshots.TryGetValue(snapshot.ProviderId, out var lastGood))
                {
                    var warning = string.IsNullOrWhiteSpace(snapshot.Error)
                        ? "Refresh failed. Showing the last good limits."
                        : snapshot.Error;
                    // Carry the error badge next to the last good bars. The taskbar filter treats
                    // an errored snapshot with no badge as a placeholder and drops the provider
                    // until the next clean refresh; that is exactly the reported "Antigravity
                    // disappears after a manual refresh" bug.
                    var lines = lastGood.Lines;
                    if (!lines.Any(line => line is BadgeMetricData badge &&
                        badge.Label.Equals(MetricLine.ErrorBadgeLabel, StringComparison.OrdinalIgnoreCase)))
                    {
                        lines = lines.Append(new BadgeMetricData(MetricLine.ErrorBadgeLabel, snapshot.Error, "#EF4444"))
                            .ToArray();
                    }
                    return lastGood with
                    {
                        Lines = lines,
                        Error = snapshot.Error,
                        ErrorCategory = snapshot.ErrorCategory,
                        Warning = warning,
                        FetchedAt = snapshot.FetchedAt
                    };
                }

                return snapshot;
            }).ToArray();
            _latestSnapshots = effectiveSnapshots;
            _resetNotifications.Observe(effectiveSnapshots, _settings.NotificationsEnabled,
                _settings.NotificationProviderIds, DateTimeOffset.UtcNow);
            NotifyAuthFailures(effectiveSnapshots);
            var enabledProviders = DashboardData.ActiveProviders.Where(provider => IsProviderEnabled(provider.Id)).ToList();
            var refreshedCards = new List<ProviderCardDisplay>(enabledProviders.Count);
            foreach (var provider in enabledProviders)
            {
                var snapshot = effectiveSnapshots.FirstOrDefault(x => x.ProviderId.Equals(provider.Id, StringComparison.OrdinalIgnoreCase));
                refreshedCards.Add(ToCard(provider, snapshot));
            }
            // Keep the last good cards visible while a network/auth refresh is in flight. Replacing
            // the collection only after all providers return avoids the jarring blank dashboard that
            // made the refresh button look broken.
            _providerCards.Clear();
            foreach (var card in refreshedCards) _providerCards.Add(card);
            var enabledSnapshots = effectiveSnapshots.Where(snapshot => IsProviderEnabled(snapshot.ProviderId)).ToArray();
            SpendCard.SetSnapshots(
                enabledSnapshots,
                DashboardData.ActiveProviders.ToDictionary(provider => provider.DisplayName, provider => provider.Accent, StringComparer.OrdinalIgnoreCase));
            var connected = _providerCards.Count(card =>
                !string.Equals(card.Status, "Not connected", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(card.Status, "Unavailable", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(card.Status, "Not configured", StringComparison.OrdinalIgnoreCase));
            ConnectedTextBlock.Text = $"{connected} of {enabledProviders.Count}";
            _nextRefreshAt = DateTimeOffset.Now.AddMinutes(5);
            var hasUnknownPricing = enabledSnapshots.Any(snapshot => snapshot.UsageHistory?.UnknownModels.Count > 0);
            SpendEstimateText.Text = hasUnknownPricing && SpendCard.CurrentSummary.HasEstimatedValues
                ? "ESTIMATED • UNKNOWN PRICING"
                : hasUnknownPricing
                    ? "UNKNOWN PRICING"
                    : "ESTIMATED";
            SpendEstimateBadge.ToolTip = hasUnknownPricing
                ? "Some local history uses models without a known price. Tokens are included, but those costs are not fabricated."
                : "Local spend is estimated from session history, not a bill.";
            SpendEstimateBadge.Visibility = SpendCard.CurrentSummary.HasEstimatedValues || hasUnknownPricing
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateRefreshCountdown();
            var statusMetrics = BuildStatusMetrics(enabledSnapshots);
            var taskbarStopwatch = Stopwatch.StartNew();
            _tray?.UpdateMetrics(statusMetrics);
            _taskbar?.UpdateMetrics(statusMetrics);
            var taskbarMs = taskbarStopwatch.ElapsedMilliseconds;
            LogProviderResults(effectiveSnapshots, stopwatch.ElapsedMilliseconds, statusMetrics.Count,
                snapshotFetchMs, taskbarMs, refreshId);
        }
        catch (TimeoutException ex)
        {
            ScheduleRefreshRetry(force, reason, refreshId, stopwatch, timedOut: true);
            _tray?.ShowFallbackNotification("TokenBurn's usage refresh timed out. Your last known values are still shown and it will retry automatically.");
            FileDiagnosticsLogger.Default.Warning("Desktop refresh timed out",
                new Dictionary<string, object?>
                {
                    ["force"] = force,
                    ["reason"] = reason,
                    ["refreshId"] = refreshId,
                    ["timeoutMs"] = DesktopRefreshTimeout.TotalMilliseconds,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["retryAt"] = _nextRefreshAt
                }, ex);
        }
        catch (Exception ex)
        {
            ScheduleRefreshRetry(force, reason, refreshId, stopwatch, timedOut: false);
            _tray?.ShowFallbackNotification("TokenBurn could not refresh usage data. Your last known values are still shown and it will retry automatically.");
            FileDiagnosticsLogger.Default.Warning("Desktop refresh failed",
                new Dictionary<string, object?>
                {
                    ["force"] = force,
                    ["reason"] = reason,
                    ["refreshId"] = refreshId,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["retryAt"] = _nextRefreshAt
                }, ex);
        }
        finally
        {
            _refreshInFlight = false;
            RefreshButton.IsEnabled = true;
            // Drain a force request that joined this run: the joined run may have served cached
            // data, so the promoted intent must still get its real network refresh. One promote
            // per in-flight run keeps the chain bounded (a new force must arrive to extend it).
            if (_forceRefreshQueued)
            {
                _forceRefreshQueued = false;
                _refreshTask = RefreshDataCoreAsync(true, $"{reason ?? "refresh"}-promoted");
            }
        }
    }

    private void ScheduleRefreshRetry(bool force, string? reason, string refreshId, Stopwatch stopwatch, bool timedOut)
    {
        // Clear the in-flight state in finally below, then let the heartbeat retry after a short
        // backoff. Logging the scheduled time makes a stuck-refresh report diagnosable from one
        // support log without needing a live debugger.
        _nextRefreshAt = DateTimeOffset.Now.Add(DesktopRefreshRetryDelay);
        UpdateRefreshCountdown();
        FileDiagnosticsLogger.Default.Info("Desktop refresh retry scheduled",
            new Dictionary<string, object?>
            {
                ["force"] = force,
                ["reason"] = reason,
                ["refreshId"] = refreshId,
                ["timedOut"] = timedOut,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["retryDelayMs"] = DesktopRefreshRetryDelay.TotalMilliseconds,
                ["retryAt"] = _nextRefreshAt
            });
    }

    private static void ObserveDetachedTask(Task task, IDisposable resourceToDispose)
    {
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception;
            resourceToDispose.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void LogProviderResults(IReadOnlyList<UsageSnapshotData> snapshots, long elapsedMs, int taskbarMetrics,
        long snapshotFetchMs, long taskbarUpdateMs, string refreshId)
    {
        FileDiagnosticsLogger.Default.Info("Provider refresh completed",
            new Dictionary<string, object?>
            {
                ["elapsedMs"] = elapsedMs,
                ["snapshotFetchMs"] = snapshotFetchMs,
                ["taskbarUpdateMs"] = taskbarUpdateMs,
                ["taskbarMetrics"] = taskbarMetrics,
                ["refreshId"] = refreshId,
                ["providers"] = snapshots.Select(snapshot => new Dictionary<string, object?>
                {
                    ["providerId"] = snapshot.ProviderId,
                    ["errorCategory"] = snapshot.ErrorCategory,
                    ["hasError"] = !string.IsNullOrWhiteSpace(snapshot.Error),
                    ["hasWarning"] = !string.IsNullOrWhiteSpace(snapshot.Warning),
                    ["metricCount"] = snapshot.Lines.Count,
                    ["errorBadgeLines"] = snapshot.Lines.OfType<BadgeMetricData>()
                        .Count(badge => badge.Label.Equals(MetricLine.ErrorBadgeLabel, StringComparison.OrdinalIgnoreCase)),
                    ["taskbarConfigured"] = TaskbarMetricFilter.IsConfigured(snapshot),
                    ["historyPoints"] = snapshot.UsageHistory?.Points.Count ?? 0
                }).ToArray()
            });
    }

    private void NotifyAuthFailures(IReadOnlyList<UsageSnapshotData> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var isEnabled = IsProviderEnabled(snapshot.ProviderId);
            var isAuthFailure = snapshot.ErrorCategory is not null &&
                (snapshot.ErrorCategory.Equals(nameof(ProviderErrorCategory.Authentication), StringComparison.OrdinalIgnoreCase) ||
                 snapshot.ErrorCategory.Equals(nameof(ProviderErrorCategory.Authorization), StringComparison.OrdinalIgnoreCase));
            if (isEnabled && isAuthFailure)
            {
                // Surface a re-authentication prompt once per failure episode; the taskbar keeps
                // showing the last-known-good quota so the provider never disappears.
                if (_authFailureNotified.Add(snapshot.ProviderId))
                    _tray?.ShowAuthNotification($"{snapshot.DisplayName} needs re-authentication. {snapshot.Error ?? "Run the provider's login command and refresh."}");
            }
            else if (isEnabled)
            {
                _authFailureNotified.Remove(snapshot.ProviderId);
            }
        }
    }

    private static async Task WarmModelCatalogAsync(CachedModelCatalog catalog)
    {
        try
        {
            await catalog.GetAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException)
        {
            FileDiagnosticsLogger.Default.Warning("Model catalog refresh failed", exception: ex);
        }
    }

    private void PositionPopoverNearTaskbar()
    {
        try
        {
            if (_settings.StatusSurface == StatusSurfaceMode.TaskbarWidget &&
                _taskbar?.TryGetWidgetBounds(out var widgetBounds) == true &&
                !widgetBounds.IsEmpty)
            {
                PositionPopoverNearWidget(widgetBounds);
                return;
            }
            // A taskbar button does not expose its shell rectangle through WPF. At activation time
            // the cursor is still over the button (or tray icon), which is a better anchor than
            // guessing the far-right edge of a multi-monitor taskbar. Fall back to taskbar bounds
            // for launches from Start, jump lists, or keyboard shortcuts.
            if (TryPositionPopoverNearCursor()) return;
            var placement = new MonitorPlacementService();
            var screen = placement.ResolveScreen(_settings.SelectedMonitor);
            var taskbarHandle = placement.GetTaskbarHandle(screen);
            var taskbar = placement.GetTaskbarBounds(taskbarHandle);
            var working = screen.WorkingArea;
            var hwnd = new WindowInteropHelper(this).Handle;
            var scale = DpiScale(hwnd);
            var width = (ActualWidth > 0 ? ActualWidth : Width) * scale;
            var height = (ActualHeight > 0 ? ActualHeight : Height) * scale;
            var popup = PopupPlacement.NearTaskbar(
                new System.Drawing.Point(taskbar.IsEmpty ? working.Right : taskbar.Left, working.Bottom),
                taskbar,
                working,
                new System.Drawing.Size((int)Math.Ceiling(width), (int)Math.Ceiling(height)),
                (int)Math.Ceiling(8 * scale));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = popup.Left / scale;
            Top = popup.Top / scale;
        }
        catch (Exception ex)
        {
            FileDiagnosticsLogger.Default.Debug("Taskbar popover positioning was unavailable; retaining the previous window position.",
                new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().Name });
        }
    }

    /// <summary>Physical-to-logical scale factor for this window's monitor, clamped to a sane
    /// range so a broken DPI report cannot explode a popover size.</summary>
    private static double DpiScale(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(hwnd);
        return dpi is < 96 or > 480 ? 1d : dpi / 96d;
    }

    private bool TryPositionPopoverNearCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return false;
        var point = new System.Drawing.Point(cursor.X, cursor.Y);
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate => candidate.Bounds.Contains(point));
        if (screen is null) return false;

        var placement = new MonitorPlacementService();
        var taskbar = placement.GetTaskbarBounds(placement.GetTaskbarHandle(screen));
        if (taskbar.IsEmpty || !taskbar.Contains(point)) return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = DpiScale(hwnd);
        var width = (ActualWidth > 0 ? ActualWidth : Width) * scale;
        var height = (ActualHeight > 0 ? ActualHeight : Height) * scale;
        var working = screen.WorkingArea;
        var popup = PopupPlacement.NearTaskbar(
            point,
            taskbar,
            working,
            new System.Drawing.Size((int)Math.Ceiling(width), (int)Math.Ceiling(height)),
            (int)Math.Ceiling(8 * scale));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = popup.Left / scale;
        Top = popup.Top / scale;
        return true;
    }

    private void PositionPopoverNearWidget(System.Drawing.Rectangle widgetBounds)
    {
        var placement = new MonitorPlacementService();
        var screen = placement.ResolveScreen(_settings.SelectedMonitor);
        var taskbar = placement.GetTaskbarBounds(placement.GetTaskbarHandle(screen));
        var working = screen.WorkingArea;
        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = DpiScale(hwnd);
        var width = (ActualWidth > 0 ? ActualWidth : Width) * scale;
        var height = (ActualHeight > 0 ? ActualHeight : Height) * scale;
        var popup = PopupPlacement.NearWidget(
            widgetBounds,
            taskbar,
            working,
            new System.Drawing.Size((int)Math.Ceiling(width), (int)Math.Ceiling(height)),
            (int)Math.Ceiling(8 * scale));
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = popup.Left / scale;
        Top = popup.Top / scale;
    }

    private ProviderCardDisplay ToCard(DashboardProvider provider, UsageSnapshotData? snapshot)
    {
        if (snapshot is null)
        {
            var noData = new MetricDisplay("Usage", "No data", "No local credential was found", 0, "neutral", provider.DisplayName);
            var noDataCard = new ProviderCardDisplay(provider.DisplayName, "Waiting for local sign-in", "Not connected", "No data", "Connect the provider CLI or add an API key in Settings", "Refresh after sign-in", 0, provider.Accent, [noData]);
            return noDataCard with
            {
                ProviderId = provider.Id,
                Mark = provider.Mark,
                LogoPath = provider.LogoPath,
                LogoGeometry = WidgetWindow.GetProviderGeometry(provider.Id),
                AlwaysMetrics = [noData]
            };
        }
        var metrics = snapshot.Lines.Select(line => ToMetric(snapshot.DisplayName, line)).ToList();
        if (snapshot.UsageHistory is { TotalCostUsd: > 0 } history)
        {
            var estimated = history.Points.Any(point => point.Estimated);
            metrics.Add(new MetricDisplay(estimated ? "30-day API value" : "30-day spend",
                $"${history.TotalCostUsd:0.00}",
                estimated ? $"{history.TotalTokens:N0} local tokens · estimate, not a bill" : $"{history.TotalTokens:N0} local tokens",
                0, estimated ? "warn" : "normal", provider.DisplayName));
        }
        var progress = snapshot.Lines.OfType<ProgressMetricData>().FirstOrDefault();
        var hasError = !string.IsNullOrWhiteSpace(snapshot.Error) ||
                       snapshot.Lines.OfType<BadgeMetricData>().Any(line => line.Label.Equals(MetricLine.ErrorBadgeLabel, StringComparison.OrdinalIgnoreCase));
        var state = hasError ? "Unavailable" :
            !string.IsNullOrWhiteSpace(snapshot.Warning) ? "Stale" :
            snapshot.Lines.OfType<BadgeMetricData>().FirstOrDefault()?.Text ?? "Connected";
        var always = MetricVisibility.SelectPinned(metrics, IsStarred).ToList();
        var onDemand = metrics.Where(metric => !always.Contains(metric)).ToList();
        var shownProgress = progress is null || !_settings.UsageDisplay.Equals("Remaining", StringComparison.OrdinalIgnoreCase)
            ? progress?.Used ?? 0
            : Math.Max(0, progress.Limit - progress.Used);
        var progressLabel = _settings.UsageDisplay.Equals("Remaining", StringComparison.OrdinalIgnoreCase) ? "remaining" : "used";
        var card = new ProviderCardDisplay(provider.DisplayName, snapshot.Plan ?? "Connected", state,
            progress is null ? "No limit" : $"{FormatProgressValue(shownProgress, progress.Unit)} {progressLabel}",
            progress is null ? "" : $"of {FormatProgressValue(progress.Limit, progress.Unit)}",
            progress?.ResetsAt is { } reset ? FormatReset(reset) : "No reset time",
            progress is null ? 0 : Math.Clamp(progress.Limit <= 0 ? 0 : progress.Used / progress.Limit, 0, 1), provider.Accent, metrics);
        var canRepairClaude = provider.DisplayName.Equals("Claude Code", StringComparison.OrdinalIgnoreCase) &&
            hasError &&
            ReauthActionResolver.ShouldOfferReauth(snapshot.ErrorCategory, snapshot.Error);
        var canRepairAntigravity = provider.DisplayName.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) &&
            hasError &&
            ReauthActionResolver.ShouldOfferReauth(snapshot.ErrorCategory, snapshot.Error);
        var hasAction = canRepairClaude || canRepairAntigravity;
        return card with
        {
            ProviderId = provider.Id,
            Mark = provider.Mark,
            LogoPath = provider.LogoPath,
            LogoGeometry = WidgetWindow.GetProviderGeometry(provider.Id),
            AlwaysMetrics = always,
            OnDemandMetrics = onDemand,
            HasAction = hasAction,
            ActionLabel = canRepairClaude
                ? "Open Claude sign-in"
                : canRepairAntigravity
                    ? "Run agy to sign in"
                    : string.Empty
        };
    }

    private void ProviderAction_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProviderCardDisplay card }) return;

        try
        {
            if (card.Provider.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
            {
                // Let Claude Code own its browser/device flow. TokenBurn never handles or copies
                // the OAuth response, and the separate console makes the action explicit on Windows.
                var loginProcess = Process.Start(ClaudeLoginCommand.CreateStartInfo());
                LastUpdatedText.Text = "Claude sign-in opened. Finish it, then TokenBurn will refresh.";
                AttachRefreshOnExit(loginProcess);
            }
            else if (card.Provider.Equals("Antigravity", StringComparison.OrdinalIgnoreCase))
            {
                // Launch the Antigravity CLI so the user can complete the browser sign-in flow in
                // its own console. TokenBurn never touches the OAuth response.
                var loginProcess = Process.Start(AntigravityLoginCommand.CreateStartInfo());
                LastUpdatedText.Text = "Antigravity CLI opened. Finish sign-in, then TokenBurn will refresh.";
                AttachRefreshOnExit(loginProcess);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show("The provider CLI could not be launched. Run it in a terminal, then refresh TokenBurn.",
                "Provider sign-in", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AttachRefreshOnExit(Process? process)
    {
        if (process is null) return;
        process.EnableRaisingEvents = true;
        // The Exited handler runs on a thread-pool thread; marshal back to the UI dispatcher.
        // The Process is disposed once its exit has been observed so the native handle does not
        // linger for the rest of the session.
        process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            // Always request the post-login refresh. If a scheduled refresh is already running,
            // RefreshDataAsync joins it and promotes a force (via _forceRefreshQueued) so the fresh
            // credential is actually read instead of silently dropping the request.
            RefreshData(force: true, reason: "post-login");
            try { process.Dispose(); } catch { }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool IsStarred(MetricDisplay metric)
    {
        var providerId = ProviderCatalog.NormalizeId(metric.Provider);
        var label = metric.Label.Trim();
        return (_settings.StarredMetrics ?? []).Any(star =>
        {
            var key = star.Trim();
            var separator = key.IndexOf(':');
            if (separator > 0)
            {
                var starredProvider = ProviderCatalog.NormalizeId(key[..separator]);
                var starredLabel = key[(separator + 1)..].Trim();
                return starredProvider.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                       (label.Equals(starredLabel, StringComparison.OrdinalIgnoreCase) ||
                        label.Contains(starredLabel, StringComparison.OrdinalIgnoreCase));
            }

            return label.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                   label.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                   $"{metric.Provider} {label}".Contains(key, StringComparison.OrdinalIgnoreCase);
        });
    }

    private bool IsProviderEnabled(string provider)
        => !(_settings.DisabledProviders ?? [])
            .Contains(ProviderCatalog.NormalizeId(provider), StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<string> GetEnabledProviderIds() => DashboardData.ActiveProviders
        .Where(provider => IsProviderEnabled(provider.Id))
        .Select(provider => provider.Id)
        .ToArray();

    private IReadOnlyList<MetricDisplay> BuildStatusMetrics(IReadOnlyList<UsageSnapshotData> snapshots)
    {
        var selected = new List<MetricDisplay>();
        foreach (var provider in DashboardData.ActiveProviders.Where(item => IsProviderEnabled(item.Id)))
        {
            var snapshot = snapshots.FirstOrDefault(item =>
                item.ProviderId.Equals(provider.Id, StringComparison.OrdinalIgnoreCase));
            // The main page intentionally renders every enabled provider, including honest
            // unavailable cards. The taskbar is different: placeholder status is not useful
            // there and made the strip expand across the entire taskbar.
            if (snapshot is null || !TaskbarMetricFilter.IsConfigured(snapshot)) continue;
            var candidates = snapshot.Lines
                .Select(line => ToMetric(snapshot.DisplayName, line, taskbarStrip: true)).ToList();
            if (candidates.Count == 0) continue;

            var starred = MetricVisibility.SelectPinned(candidates, IsStarred, 2).ToList();

            foreach (var metric in starred)
                if (!selected.Contains(metric)) selected.Add(metric);
        }

        return selected;
    }

    private MetricDisplay ToMetric(string provider, UsageMetricData line, bool taskbarStrip = false) => line switch
    {
        ProgressMetricData p => BuildProgressMetric(provider, p, taskbarStrip),
        TextMetricData t => new(t.Label, t.Value, t.Subtitle ?? "", 0, "normal", provider),
        BadgeMetricData b => new(b.Label, b.Text, b.Subtitle ?? "", 0, b.Color is null ? "neutral" : "warn", provider),
        ValuesMetricData values => new(values.Label, FormatValuesValue(values), FormatValuesDetail(values), 0, "normal", provider),
        BarChartMetricData chart => new(
            chart.Label,
            FormatChartValue(chart),
            chart.Note ?? $"{chart.Points.Count} day trend",
            0,
            "normal",
            provider,
            ChartPoints: chart.Points.Select(point => new MetricChartDisplay(point.Label, point.Value, point.ValueLabel)).ToArray()),
        _ => new(line.Label, "No value", "Open the provider for details", 0, "neutral", provider)
    };

    private MetricDisplay BuildProgressMetric(string provider, ProgressMetricData p, bool taskbarStrip = false)
    {
        var remaining = Math.Max(0, p.Limit - p.Used);
        var useRemaining = _settings.UsageDisplay.Equals("Remaining", StringComparison.OrdinalIgnoreCase);
        var shown = useRemaining ? remaining : p.Used;
        var detail = p.ResetsAt is { } reset
            ? FormatReset(reset)
            : useRemaining
                ? $"remaining of {FormatProgressValue(p.Limit, p.Unit)}"
                : $"of {FormatProgressValue(p.Limit, p.Unit)}";
        // The compact taskbar strip has no room for units, and mixing dollar/count strings in with
        // every other provider's plain percent reads as broken. Collapse to percent-of-quota there;
        // the dashboard card below keeps the precise dollar/count figure.
        var isNativePercent = (p.Unit?.Trim().ToLowerInvariant()) is null or "" or "percent" or "%";
        var value = taskbarStrip && !isNativePercent && p.Limit > 0
            ? $"{Math.Round((useRemaining ? 1 - p.Used / p.Limit : p.Used / p.Limit) * 100)}%"
            : FormatProgressValue(shown, p.Unit);
        return new MetricDisplay(
            p.Label,
            value,
            detail,
            p.Limit <= 0 ? 0 : Math.Clamp(p.Used / p.Limit, 0, 1),
            p.Used >= p.Limit ? "danger" : p.Used >= p.Limit * .75 ? "warn" : "normal",
            provider,
            p.ResetsAt,
            true);
    }

    private string FormatReset(DateTimeOffset reset)
        => ResetTimeFormatter.FormatSurface(reset, _settings.ResetTimeDisplay);

    private void ApplyScreenSharePrivacy()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        try
        {
            NativeMethods.SetWindowDisplayAffinity(hwnd,
                _settings.HideFromScreenShare ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static string FormatValuesValue(ValuesMetricData line)
    {
        if (line.Values.Count == 0) return "No value";
        if (line.Values.Count == 1) return FormatMetricValue(line.Values[0]);
        return $"{line.Values.Count} values";
    }

    private static string FormatProgressValue(double value, string? unit)
    {
        return unit?.Trim().ToLowerInvariant() switch
        {
            "usd" or "dollars" or "currency" => $"${value:0.00}",
            "count" or "requests" or "tokens" => $"{value:N0}",
            "duration" or "hours" => $"{value:0.#}h",
            _ => $"{value:0.#}%"
        };
    }

    private static string FormatValuesDetail(ValuesMetricData line)
        => string.Join(" / ", line.Values.Take(3).Select(value =>
            string.IsNullOrWhiteSpace(value.ValueLabel) ? FormatMetricValue(value) : $"{value.ValueLabel}: {FormatMetricValue(value)}"));

    private static string FormatChartValue(BarChartMetricData line)
    {
        var point = line.Points.LastOrDefault();
        return point is null ? "No value" : point.ValueLabel ?? point.Value.ToString("0.#");
    }

    private static string FormatMetricValue(ScalarValueData value)
    {
        if (!string.IsNullOrWhiteSpace(value.ValueLabel)) return value.ValueLabel!;
        return value.Unit.ToLowerInvariant() switch
        {
            "dollars" or "usd" or "currency" => $"${value.Number:0.00}",
            "percent" or "%" => $"{value.Number:0.#}%",
            "duration" or "hours" => $"{value.Number:0.#}h",
            _ => value.Number.ToString("0.#")
        };
    }

    /// <summary>
    /// Entry point for the tray's right-click menu. Routes through the popup's own in-page
    /// Settings/Customize view (same one the Options button opens) when the popup is available,
    /// so the tray menu no longer opens a second native window with its own focus/position bugs.
    /// Falls back to the native dialog only when the popup itself is turned off.
    /// </summary>
    public void ShowSettingsPage(DrawingPoint? anchor = null, bool useWidgetAvoidRect = true)
        => _ = ShowSettingsPageAsync(anchor, useWidgetAvoidRect);

    private async Task ShowSettingsPageAsync(DrawingPoint? anchor, bool useWidgetAvoidRect)
    {
        try
        {
            if (_tauriPopup is not null &&
                await _tauriPopup.TryShowAsync(anchor ?? ResolvePopupAnchor(),
                    useWidgetAvoidRect ? GetWidgetAvoidRect() : null, "settings").ConfigureAwait(true))
                return;
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("The settings page could not be shown", exception: exception);
        }
        _tray?.ShowFallbackNotification("The compact dashboard could not be opened. TokenBurn is still running in the tray; try again in a moment.");
    }

    public void ShowCustomizePage(DrawingPoint? anchor = null, bool useWidgetAvoidRect = true)
        => _ = ShowCustomizePageAsync(anchor, useWidgetAvoidRect);

    private async Task ShowCustomizePageAsync(DrawingPoint? anchor, bool useWidgetAvoidRect)
    {
        try
        {
            if (_tauriPopup is not null &&
                await _tauriPopup.TryShowAsync(anchor ?? ResolvePopupAnchor(),
                    useWidgetAvoidRect ? GetWidgetAvoidRect() : null, "customize").ConfigureAwait(true))
                return;
        }
        catch (Exception exception)
        {
            FileDiagnosticsLogger.Default.Warning("The customize page could not be shown", exception: exception);
        }
        _tray?.ShowFallbackNotification("The compact dashboard could not be opened. TokenBurn is still running in the tray; try again in a moment.");
    }

    public void ShowSettings()
    {
        // When the Tauri popup owns presentation, this MainWindow is a hidden, minimized
        // coordination window (see App.OnStartup). Setting it as the dialog's Owner made Windows
        // restore it to its stored CenterScreen position behind the modal, leaving the oversized
        // dashboard visible and requiring a manual dismiss after the dialog closed. Only own the
        // dialog to this window when it is genuinely the visible surface (the non-popup fallback).
        var ownerIsPresentation = IsVisible && WindowState == WindowState.Normal;
        var dialog = new SettingsDialog(_settings, new MonitorPlacementService());
        if (ownerIsPresentation) dialog.Owner = this;
        else dialog.Topmost = true;
        dialog.SettingsChanged += updated =>
        {
            _settings = updated;
            App.CurrentApp.SaveSettings(_settings);
            ApplySettings(_settings);
            RefreshData();
        };
        dialog.ShowDialog();
        if (dialog.BackRequested)
        {
            // Settings is a page in the compact popup, not a dead-end modal dialog. Reopen the
            // popup at the same shell anchor when the user taps the back chevron.
            Dispatcher.BeginInvoke(new Action(() => ShowFromTray(ResolvePopupAnchor())),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        if (!ownerIsPresentation && WindowState != WindowState.Minimized)
            WindowState = WindowState.Minimized;
    }

    public void ShowUpdateStatus() => System.Windows.MessageBox.Show("The update channel is not configured for this unsigned development build. No network request was made. Install a signed release when a feed is available.", "TokenBurn updates", MessageBoxButton.OK, MessageBoxImage.Information);
    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => RefreshData();
    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => ShowSettings();

    private void OptionsButton_OnClick(object sender, RoutedEventArgs e)
        => ShowSettings();

    private void CustomizeButton_OnClick(object sender, RoutedEventArgs e) => ShowCustomize();

    public void ShowCustomize()
    {
        // Same restore-behind-modal problem as ShowSettings when the Tauri popup, not this
        // window, owns presentation. See ShowSettings for the full explanation.
        var ownerIsPresentation = IsVisible && WindowState == WindowState.Normal;
        var dialog = new CustomizeDialog(_settings);
        if (ownerIsPresentation) dialog.Owner = this;
        else dialog.Topmost = true;
        dialog.SettingsChanged += updated =>
        {
            _settings = updated;
            App.CurrentApp.SaveSettings(_settings);
            ApplySettings(_settings);
            RefreshData();
        };
        dialog.ShowDialog();
        if (!ownerIsPresentation && WindowState != WindowState.Minimized)
            WindowState = WindowState.Minimized;
    }

    private static readonly JsonSerializerOptions SettingsPageJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Serves the Tauri popup's in-page Settings/Customize views (see index.html) their editable
    /// state plus the reference lists (monitors, providers, pinned-metric names) needed to render
    /// the same choices the native SettingsDialog/CustomizeDialog offered. Two native windows
    /// coordinating with the popup window was the root cause of the settings-opening bugs (owner
    /// restoring the hidden dashboard, focus races, off-screen positioning): a page inside the
    /// popup's own window has none of that, matching the compact settings panel behavior.
    /// </summary>
    internal string GetSettingsPageDataJson()
    {
        var monitors = new MonitorPlacementService().GetMonitors()
            .Select(m => new { id = m.Id, displayName = m.DisplayName }).ToArray();
        var snapshotsByProvider = _latestSnapshots.ToDictionary(snapshot => snapshot.ProviderId,
            StringComparer.OrdinalIgnoreCase);
        var providers = DashboardData.Providers.Select(p =>
        {
            snapshotsByProvider.TryGetValue(p.Id, out var snapshot);
            var available = snapshot is not null &&
                snapshot.ErrorCategory is null &&
                snapshot.Lines.OfType<ProgressMetricData>().Any(metric => metric.ResetsAt is not null);
            return new
            {
                id = p.Id,
                displayName = p.DisplayName,
                logo = p.LogoPath,
                available
            };
        }).ToArray();
        // The Customize page needs only an actionable, already-redacted reason. Provider output
        // can contain enough implementation detail to help a user reconnect, but it must never
        // carry credentials, logs, or raw exception data into the webview. The category lets the
        // page separate blocking failures (not installed, unsupported) from recoverable ones
        // (sign-in, rate limit, network); ErrorCategory is only set on error snapshots, so a
        // warning-only status arrives with a null category.
        var providerStatuses = DashboardData.Providers.Select(provider =>
        {
            snapshotsByProvider.TryGetValue(provider.Id, out var snapshot);
            var reason = snapshot?.Error ?? snapshot?.Warning;
            return new
            {
                id = provider.Id,
                reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
                category = snapshot?.ErrorCategory?.ToString()
            };
        }).ToArray();
        var payload = new
        {
            settings = _settings,
            monitors,
            providers,
            providerStatuses,
            metricNames = CustomizableMetricNames
        };
        return JsonSerializer.Serialize(payload, SettingsPageJsonOptions);
    }

    /// <summary>
    /// Builds a redacted, self-contained diagnostics snapshot for the Settings "Copy logs" button.
    /// Everything is already scrubbed by the file logger or the settings surface; provider data is
    /// limited to status metadata and counts, never prompts, bodies, or credentials.
    /// </summary>
    internal string BuildDiagnosticsBundleJson()
    {
        var snapshotsByProvider = _latestSnapshots.ToDictionary(snapshot => snapshot.ProviderId,
            StringComparer.OrdinalIgnoreCase);
        var providers = DashboardData.Providers.Select(provider =>
        {
            snapshotsByProvider.TryGetValue(provider.Id, out var snapshot);
            return new
            {
                id = provider.Id,
                displayName = provider.DisplayName,
                enabled = IsProviderEnabled(provider.Id),
                error = snapshot?.Error,
                errorCategory = snapshot?.ErrorCategory,
                warning = snapshot?.Warning,
                fetchedAt = snapshot?.FetchedAt,
                metricCount = snapshot?.Lines.Count ?? 0,
                historyPoints = snapshot?.UsageHistory?.Points.Count ?? 0,
                historyCostUsd = snapshot?.UsageHistory?.TotalCostUsd ?? 0,
                unknownModels = snapshot?.UsageHistory?.UnknownModels
            };
        }).ToArray();
        var claude = SnapshotCatalog?.Find(ProviderIds.ClaudeCode) as ClaudeProvider;
        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            sessionStartedAt = _sessionStartedAt,
            appVersion = ProductInfo.Version,
            osDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            runtimeVersion = Environment.Version.ToString(),
            logTail = ReadDiagnosticsLogTail(),
            logFile = DescribeFile(UsageMonitorPaths.Current.DiagnosticsLogFile),
            taskbarStripLog = DescribeFile(UsageMonitorPaths.Current.TaskbarStripLogFile),
            refresh = new
            {
                nextRefreshAt = _nextRefreshAt,
                inFlight = _refreshInFlight
            },
            settings = _settings,
            providers,
            claudeRateLimitedUntil = claude?.RateLimitedUntil,
            pricingCatalogFile = DescribeFile(Path.Combine(UsageMonitorPaths.Current.PricingDirectory, "model-catalog.json")),
            cache = DescribeCacheDirectory()
        };
        return JsonSerializer.Serialize(payload, SettingsPageJsonOptions);
    }

    private static IReadOnlyList<object?> ReadDiagnosticsLogTail(int maxLines = 600)
    {
        try
        {
            var path = UsageMonitorPaths.Current.DiagnosticsLogFile;
            if (!File.Exists(path)) return [];
            var lines = File.ReadAllLines(path);
            var tail = lines.Length > maxLines ? lines[^maxLines..] : lines;
            var entries = new List<object?>(tail.Length);
            foreach (var line in tail)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                        entries.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                    entries.Add(new { raw = SensitiveDataRedactor.Redact(line) });
                }
            }
            return entries;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static object? DescribeCacheDirectory()
    {
        try
        {
            var directory = UsageMonitorPaths.Current.CacheDirectory;
            if (!Directory.Exists(directory)) return null;
            var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new { name = info.Name, bytes = info.Length, lastWriteUtc = info.LastWriteTimeUtc };
                })
                .OrderByDescending(file => file.lastWriteUtc)
                .Take(20)
                .ToArray();
            return new { fileCount = files.Length, files };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static object? DescribeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            return new { name = info.Name, bytes = info.Length, lastWriteUtc = info.LastWriteTimeUtc };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Must be called on the UI thread; the desktop control server marshals via Dispatcher.</summary>
    internal bool ApplySettingsPageDataJson(string json)
    {
        try
        {
            var updated = JsonSerializer.Deserialize<UserSettings>(json, SettingsPageJsonOptions);
            if (updated is null) return false;
            _settings = updated;
            App.CurrentApp.SaveSettings(_settings);
            RefreshData();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void UpdateButton_OnClick(object sender, RoutedEventArgs e) => ShowUpdateStatus();
    private void QuitButton_OnClick(object sender, RoutedEventArgs e) => ShutdownFromApp();

    private void ShareScreenshotButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateLayout();
            var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(this);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export TokenBurn share card",
                Filter = "PNG image|*.png",
                FileName = "UsageMonitor-share.png",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            FileDiagnosticsLogger.Default.Warning("Share screenshot export failed", exception: ex);
            System.Windows.MessageBox.Show("The share card could not be exported.", "TokenBurn", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowShutdown) return;
        e.Cancel = true;
        // Keep the normal WPF taskbar button alive. Clicking it restores the
        // dashboard, while the tray remains the low-profile way to reopen it.
        if (!IsVisible) Show();
        WindowState = WindowState.Minimized;
    }

    public void ShutdownFromApp() { _allowShutdown = true; Close(); }
}

internal static class DashboardData
{
    public static IReadOnlyList<DashboardProvider> Providers { get; } =
    [
        new(ProviderIds.ClaudeCode, "Claude Code", "#D9D9DD", "✳", "./assets/providers/claude.svg"),
        new(ProviderIds.Codex, "Codex", "#3D82F6", "◎", "./assets/providers/codex.svg"),
        new(ProviderIds.Antigravity, "Antigravity", "#34A853", "A", "./assets/providers/antigravity.svg"),
        new(ProviderIds.Cursor, "Cursor", "#B8B2A6", "C", "./assets/providers/cursor.svg"),
        new(ProviderIds.Copilot, "Copilot", "#A56BFF", "◉", "./assets/providers/copilot.svg"),
        new(ProviderIds.Devin, "Devin", "#35C8C0", "D", "./assets/providers/devin.svg"),
        new(ProviderIds.Grok, "Grok", "#F2F2F0", "G", "./assets/providers/grok.svg"),
        new(ProviderIds.OpenCode, "OpenCode", "#FFFFFF", "O", "./assets/providers/opencode.svg")
    ];

    public static IReadOnlyList<MetricDisplay> SampleMetrics { get; } =
    [new("Session", "No data", "Sign in to a supported provider", 0, "neutral", "Usage"), new("Weekly", "No data", "Sign in to a supported provider", 0, "neutral", "Usage")];

    // Hosted API billing providers are intentionally kept out of the default Windows monitor.
    // Their adapters remain available for a future opt-in custom-model pack.
    public static IReadOnlyList<DashboardProvider> ActiveProviders { get; } =
        Providers;
}

internal sealed record DashboardProvider(string Id, string DisplayName, string Accent, string Mark, string LogoPath);

internal sealed class SettingsDialog : Window
{
    private readonly WpfComboBox _monitor;
    private readonly WpfCheckBox _startup;
    private readonly WpfCheckBox _alerts;
    private readonly WpfCheckBox _tauriPopup;
    private readonly WpfCheckBox _taskbarPositionLocked;
    private readonly WpfComboBox _usageDisplay;
    private readonly WpfComboBox _resetTimes;
    private readonly WpfButton _notificationProviders;
    private readonly IReadOnlyList<WpfMenuItem> _notificationProviderItems;
    private readonly WpfCheckBox _hideFromScreenShare;
    private readonly IReadOnlyList<MonitorOption> _monitors;

    public SettingsDialog(UserSettings settings, MonitorPlacementService placement)
    {
        Title = "TokenBurn Settings";
        Width = 660;
        Height = 860;
        MinHeight = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextPrimaryBrush"];
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(18),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });
        _monitors = placement.GetMonitors();
        // The stock Windows ComboBox template uses a light selection field. Keep that
        // field readable even when the dashboard theme is dark.
        _monitor = new WpfComboBox
        {
            ItemsSource = _monitors,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextPrimaryBrush"],
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelRaisedBrush"],
            BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelStrokeBrush"],
            BorderThickness = new Thickness(1),
            MinHeight = 34,
            Margin = new Thickness(0, 6, 0, 14)
        };
        _monitor.Style = (Style)System.Windows.Application.Current.Resources["DarkComboBox"];
        _monitor.ItemTemplate = CreateComboItemTemplate(nameof(MonitorOption.DisplayName));
        _monitor.SelectedItem = _monitors.FirstOrDefault(x => x.Id.Equals(settings.SelectedMonitor, StringComparison.OrdinalIgnoreCase)) ?? _monitors.FirstOrDefault();
        _monitor.IsEnabled = true;
        _startup = new WpfCheckBox { Content = "Launch TokenBurn when I sign in", IsChecked = settings.StartAtLogin, Margin = new Thickness(0, 5, 0, 8) };
        _alerts = new WpfCheckBox { Content = "Notify when quotas reset", IsChecked = settings.NotificationsEnabled, Margin = new Thickness(0, 5, 0, 8) };
        _tauriPopup = new WpfCheckBox { Content = "Use compact Tauri popup", IsChecked = settings.UseTauriPopup, Margin = new Thickness(0, 5, 0, 8) };
        _taskbarPositionLocked = new WpfCheckBox { Content = "Lock taskbar position", IsChecked = settings.TaskbarPositionLocked, Margin = new Thickness(0, 5, 0, 8) };
        _usageDisplay = CreateSettingsCombo(["Used", "Remaining"], settings.UsageDisplay, 0);
        _resetTimes = CreateSettingsCombo(["Countdown", "Exact time"], settings.ResetTimeDisplay, 0);
        _notificationProviderItems = DashboardData.Providers.Select(provider =>
        {
            var item = new WpfMenuItem
            {
                Header = provider.DisplayName,
                Tag = provider.Id,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = (settings.NotificationProviderIds ?? []).Contains(provider.Id, StringComparer.OrdinalIgnoreCase)
            };
            item.Click += (_, _) =>
            {
                UpdateNotificationProviderSummary();
                SettingsChanged?.Invoke(BuildResult(settings));
            };
            return item;
        }).ToArray();
        var notificationMenu = new WpfContextMenu
        {
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelRaisedBrush"],
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextPrimaryBrush"],
            BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelStrokeBrush"]
        };
        foreach (var item in _notificationProviderItems) notificationMenu.Items.Add(item);
        _notificationProviders = new WpfButton
        {
            MinWidth = 150,
            MinHeight = 32,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelStrokeBrush"],
            ContextMenu = notificationMenu,
            ToolTip = "Choose which quota resets you want to be notified about"
        };
        _notificationProviders.Click += (_, _) =>
        {
            notificationMenu.PlacementTarget = _notificationProviders;
            notificationMenu.IsOpen = true;
        };
        UpdateNotificationProviderSummary();
        _hideFromScreenShare = new WpfCheckBox { Content = "Hide usage values from screen sharing", IsChecked = settings.HideFromScreenShare, Margin = new Thickness(0, 5, 0, 8) };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "SETTINGS", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"] });
        panel.Children.Add(new TextBlock { Text = "Taskbar status strip + tray", FontSize = 12, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"], Margin = new Thickness(0, 18, 0, 2) });
        panel.Children.Add(new TextBlock { Text = "The compact status strip is always available. Use the tray menu to hide it temporarily; Windows keeps the tray fallback running.", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Taskbar display", FontSize = 12 });
        panel.Children.Add(_monitor);
        panel.Children.Add(new TextBlock { Text = "Display selection applies to the experimental status strip. Windows owns the native taskbar button's monitor placement.", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, -6, 0, 10) });
        panel.Children.Add(_startup);
        panel.Children.Add(_tauriPopup);
        panel.Children.Add(_taskbarPositionLocked);
        panel.Children.Add(new TextBlock { Text = "Unlock this only when you need to drag the taskbar strip.", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, -4, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "USAGE DISPLAY", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(LabeledControl("Show usage as", _usageDisplay));
        panel.Children.Add(LabeledControl("Reset times", _resetTimes));
        panel.Children.Add(new TextBlock { Text = "NOTIFICATIONS", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(_alerts);
        panel.Children.Add(LabeledControl("Reset Notifications", _notificationProviders));
        panel.Children.Add(new TextBlock { Text = "The app watches each selected reset timer locally, so alerts do not wait for the next five-minute refresh.", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, -2, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "PRIVACY", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(_hideFromScreenShare);
        panel.Children.Add(new TextBlock { Text = "Usage values stay local. No anonymous usage or crash uploads are enabled in this build.", TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, -3, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "ADVANCED", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"], Margin = new Thickness(0, 14, 0, 2) });
        panel.Children.Add(new TextBlock { Text = "Diagnostics stay local and redacted. Provider updates are checked only when a refresh is requested.", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"], FontSize = 11, Margin = new Thickness(0, 0, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = "Codex, Claude Code, and Antigravity use their existing Windows-native sign-in data. Hosted API billing providers are intentionally outside this monitor, so no API key is required.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"],
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0)
        });
        panel.Children.Add(new TextBlock { Text = "The compact status strip is the Windows surface. Explorer does not provide a public embedded-widget API, so it can fall back to the supported native taskbar button and tray without losing monitoring.", TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"], FontSize = 11, Margin = new Thickness(0, 12, 0, 0) });
        var header = new Grid { Height = 58, Margin = new Thickness(18, 8, 18, 0) };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        var back = new WpfButton { Content = "‹", Width = 34, Height = 34, Padding = new Thickness(0), FontSize = 25, Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.Transparent, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"] };
        back.Click += (_, _) => { BackRequested = true; Close(); };
        var title = new TextBlock { Text = "Settings", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        Grid.SetColumn(title, 1);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(back);
        header.Children.Add(title);
        var body = new ScrollViewer
        {
            Padding = new Thickness(26, 4, 26, 18),
            // Keep the long settings page wheel/keyboard-scrollable without adding a permanent
            // stock rail that clashes with the compact surface.
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        shell.Children.Add(header);
        shell.Children.Add(body);
        Content = new Border
        {
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["CanvasBrush"],
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 81, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.5, Color = Colors.Black },
            Child = shell
        };
        WireInstantApply(settings);
    }

    public UserSettings Result { get; private set; } = UserSettings.Default;
    public bool BackRequested { get; private set; }
    public event Action<UserSettings>? SettingsChanged;

    private void WireInstantApply(UserSettings original)
    {
        var queued = false;
        void QueueApply()
        {
            if (queued) return;
            queued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                queued = false;
                SettingsChanged?.Invoke(BuildResult(original));
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        foreach (var check in new[] { _startup, _alerts, _tauriPopup, _taskbarPositionLocked, _hideFromScreenShare })
        {
            check.Checked += (_, _) => QueueApply();
            check.Unchecked += (_, _) => QueueApply();
        }
        _monitor.SelectionChanged += (_, _) => QueueApply();
        _usageDisplay.SelectionChanged += (_, _) => QueueApply();
        _resetTimes.SelectionChanged += (_, _) => QueueApply();
    }

    private static DataTemplate CreateComboItemTemplate(string? property = null)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.ForegroundProperty, (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextPrimaryBrush"]);
        text.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI"));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property ?? "."));
        return new DataTemplate { VisualTree = text };
    }

    private static WpfComboBox CreateSettingsCombo(IEnumerable<string> values, string? selected, int fallback)
    {
        var combo = new WpfComboBox
        {
            ItemsSource = values.ToArray(),
            MinWidth = 150,
            MinHeight = 32,
            Margin = new Thickness(0, 4, 0, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextPrimaryBrush"],
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelRaisedBrush"],
            BorderBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["PanelStrokeBrush"],
            BorderThickness = new Thickness(1)
        };
        combo.Style = (Style)System.Windows.Application.Current.Resources["DarkComboBox"];
        combo.ItemTemplate = CreateComboItemTemplate();
        combo.SelectedItem = values.FirstOrDefault(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? values.ElementAtOrDefault(fallback);
        return combo;
    }

    private static Grid LabeledControl(string label, FrameworkElement control)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private UserSettings BuildResult(UserSettings original)
    {
        var monitor = _monitor.SelectedItem as MonitorOption;
        var result = original.Clone();
        result.StatusSurface = StatusSurfaceMode.TaskbarWidget;
        result.SelectedMonitor = monitor?.Id ?? MonitorPlacementService.PrimaryMonitorId;
        result.StartAtLogin = _startup.IsChecked == true;
        result.NotificationsEnabled = _alerts.IsChecked == true;
        result.CompactDensity = true;
        result.ShowTotalSpend = true;
        result.UseTauriPopup = _tauriPopup.IsChecked == true;
        result.TaskbarPositionLocked = _taskbarPositionLocked.IsChecked == true;
        result.UsageDisplay = (_usageDisplay.SelectedItem as string) ?? "Used";
        result.ResetTimeDisplay = (_resetTimes.SelectedItem as string) ?? "Countdown";
        result.NotificationProviderIds = _notificationProviderItems
            .Where(item => item.IsChecked)
            .Select(item => item.Tag?.ToString() ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToList();
        result.HideFromScreenShare = _hideFromScreenShare.IsChecked == true;
        return result;
    }

    private void UpdateNotificationProviderSummary()
    {
        var selected = _notificationProviderItems.Count(item => item.IsChecked);
        _notificationProviders.Content = selected == _notificationProviderItems.Count
            ? "All providers"
            : selected == 0
                ? "No providers"
                : $"{selected} provider{(selected == 1 ? string.Empty : "s")}";
    }
}

/// <summary>
/// Small, deliberately local customization surface. It keeps the taskbar selection
/// understandable without making the dashboard depend on a second navigation model.
/// </summary>
internal sealed class CustomizeDialog : Window
{
    private readonly List<WpfCheckBox> _checks = [];
    private readonly List<WpfCheckBox> _providerChecks = [];
    private readonly UserSettings _original;

    public CustomizeDialog(UserSettings settings)
    {
        _original = settings.Clone();
        Title = "Customize metrics";
        Width = 420;
        Height = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextPrimaryBrush"];
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(18),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false
        });

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Choose which providers and metrics stay visible in the dashboard and status surfaces.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"],
            Margin = new Thickness(0, 8, 0, 14)
        });

        panel.Children.Add(new TextBlock
        {
            Text = "PROVIDERS",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"],
            Margin = new Thickness(0, 2, 0, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "METRICS",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"],
            Margin = new Thickness(0, 18, 0, 0)
        });
        foreach (var provider in DashboardData.ActiveProviders)
        {
            var providerMetrics = MainWindow.CustomizableMetricNames
                .Where(metric => metric.StartsWith(provider.Id + ":", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var providerCheck = new WpfCheckBox
            {
                Content = provider.DisplayName,
                Tag = provider.Id,
                IsChecked = !_original.DisabledProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 2, 14, 2),
                Padding = new Thickness(2)
            };
            _providerChecks.Add(providerCheck);

            var metricPanel = new StackPanel { Margin = new Thickness(14, 0, 0, 6) };
            var visibleCount = new TextBlock
            {
                Text = $"Exposes {providerMetrics.Length} metrics · {providerMetrics.Count(metric => _original.StarredMetrics?.Contains(metric, StringComparer.OrdinalIgnoreCase) == true)} visible",
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"],
                Margin = new Thickness(4, 0, 0, 4)
            };
            metricPanel.Children.Add(visibleCount);
            foreach (var metric in providerMetrics)
            {
                var check = new WpfCheckBox
                {
                    Content = MetricLabel(metric),
                    Tag = metric,
                    IsChecked = _original.StarredMetrics?.Contains(metric, StringComparer.OrdinalIgnoreCase) == true,
                    Margin = new Thickness(0, 4, 0, 4),
                    Padding = new Thickness(2)
                };
                _checks.Add(check);
                check.Checked += (_, _) => UpdateMetricCount(visibleCount, providerMetrics);
                check.Unchecked += (_, _) => UpdateMetricCount(visibleCount, providerMetrics);
                metricPanel.Children.Add(check);
            }

            var providerHeader = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            providerHeader.Children.Add(providerCheck);
            var expander = new Expander
            {
                Header = providerHeader,
                Content = metricPanel,
                IsExpanded = false,
                Margin = new Thickness(0, 4, 0, 2),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            panel.Children.Add(expander);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Metric toggles control the compact summary, taskbar strip, and tray details. Full provider cards keep every available metric.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextMutedBrush"],
            Margin = new Thickness(0, 16, 0, 0)
        });
        var header = new Grid { Height = 58, Margin = new Thickness(18, 8, 18, 0) };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        var back = new WpfButton { Content = "‹", Width = 34, Height = 34, Padding = new Thickness(0), FontSize = 25, Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.Transparent, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"] };
        back.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(back, 0);
        var title = new TextBlock { Text = "Customize", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        Grid.SetColumn(title, 1);
        var reset = new WpfButton { Content = "↶", Width = 34, Height = 34, Padding = new Thickness(0), FontSize = 21, Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.Transparent, Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["TextSecondaryBrush"], ToolTip = "Reset customization" };
        reset.Click += (_, _) =>
        {
            foreach (var check in _providerChecks) check.IsChecked = true;
            foreach (var check in _checks) check.IsChecked = _original.StarredMetrics?.Contains(check.Tag?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase) == true;
        };
        Grid.SetColumn(reset, 2);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.Children.Add(back);
        header.Children.Add(title);
        header.Children.Add(reset);

        var body = new ScrollViewer
        {
            Padding = new Thickness(24, 4, 24, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel
        };
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        shell.Children.Add(header);
        shell.Children.Add(body);
        var outer = new Border
        {
            Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["CanvasBrush"],
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 81, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.5, Color = Colors.Black },
            Child = shell
        };
        Content = outer;
        WireInstantApply();
    }

    public UserSettings Result { get; private set; } = UserSettings.Default;
    public event Action<UserSettings>? SettingsChanged;

    private void WireInstantApply()
    {
        var queued = false;
        void QueueApply()
        {
            if (queued) return;
            queued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                queued = false;
                SettingsChanged?.Invoke(BuildResult());
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        foreach (var check in _providerChecks.Concat(_checks))
        {
            check.Checked += (_, _) => QueueApply();
            check.Unchecked += (_, _) => QueueApply();
        }
    }

    private UserSettings BuildResult()
    {
        var result = _original.Clone();
        result.StarredMetrics = _checks.Where(check => check.IsChecked == true)
            .Select(check => check.Tag?.ToString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
        result.DisabledProviders = _providerChecks
            .Where(check => check.IsChecked != true)
            .Select(check => check.Tag?.ToString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
        return result;
    }

    private static void UpdateMetricCount(TextBlock target, IReadOnlyList<string> metrics)
    {
        var visible = metrics.Count(metric =>
            target.Parent is System.Windows.Controls.Panel panel && panel.Children.OfType<WpfCheckBox>().Any(check =>
                string.Equals(check.Tag?.ToString(), metric, StringComparison.OrdinalIgnoreCase) && check.IsChecked == true));
        target.Text = $"Exposes {metrics.Count} metrics · {visible} visible";
    }

    private static string MetricLabel(string metric)
    {
        var parts = metric.Split(':', 2);
        var provider = DashboardData.Providers.FirstOrDefault(item => item.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        var label = parts.Length > 1 ? parts[1].Replace('-', ' ') : metric;
        label = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(label);
        return $"{provider?.DisplayName ?? parts[0]} {label}";
    }
}
