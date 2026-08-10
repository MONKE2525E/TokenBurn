using System.Text.Json;
using System.Drawing;
using System.Windows.Media.Imaging;
using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class TaskbarPlacementTests
{
    [Fact]
    public void PopupPlacementUsesTheCorrectSideAndAvoidsTheBottomTaskbar()
    {
        AssertPlacement(new Rectangle(1300, 1040, 240, 40), new Rectangle(0, 1040, 1920, 40),
            new Rectangle(0, 0, 1920, 1040), new Size(480, 640), 1060, 392);
    }

    [Fact]
    public void PopupPlacementUsesTheCorrectSideAndAvoidsTheTopTaskbar()
    {
        AssertPlacement(new Rectangle(1300, 0, 240, 40), new Rectangle(0, 0, 1920, 40),
            new Rectangle(0, 40, 1920, 1040), new Size(480, 640), 1060, 48);
    }

    [Fact]
    public void PopupPlacementUsesTheCorrectSideAndAvoidsTheLeftTaskbar()
    {
        AssertPlacement(new Rectangle(0, 400, 40, 240), new Rectangle(0, 0, 40, 1080),
            new Rectangle(40, 0, 1880, 1080), new Size(120, 480), 48, 160);
    }

    [Fact]
    public void PopupPlacementUsesTheCorrectSideAndAvoidsTheRightTaskbar()
    {
        AssertPlacement(new Rectangle(1880, 400, 40, 240), new Rectangle(1880, 0, 40, 1080),
            new Rectangle(0, 0, 1880, 1080), new Size(120, 480), 1752, 160);
    }

    private static void AssertPlacement(
        Rectangle widget,
        Rectangle taskbar,
        Rectangle working,
        Size popupSize,
        int expectedLeft,
        int expectedTop)
    {
        var popup = PopupPlacement.NearWidget(widget, taskbar, working, popupSize);
        Assert.Equal(expectedLeft, popup.Left);
        Assert.Equal(expectedTop, popup.Top);
        Assert.False(popup.IntersectsWith(taskbar));
    }

    [Fact]
    public void PopupPlacementClampsToWorkingAreaWhenAnchorIsAtTheEdge()
    {
        var working = new Rectangle(1920, 0, 2560, 1400);
        var taskbar = new Rectangle(1920, 1360, 2560, 1400);

        var popup = PopupPlacement.NearTaskbar(
            new Point(2550, 1380), taskbar, working, new Size(700, 1000));

        Assert.True(working.Contains(popup));
        Assert.False(popup.IntersectsWith(taskbar));
    }

    [Fact]
    public void TaskbarFilterExcludesUnavailableButKeepsLastGoodWarningData()
    {
        var good = new UsageSnapshotData(
            "codex", "Codex", null,
            [new ProgressMetricData("Weekly", 41, 100)],
            DateTimeOffset.UtcNow);
        var unavailable = good with { Error = "Provider is not configured" };
        var stale = good with { Warning = "Refresh failed. Showing the last good limits." };

        Assert.False(TaskbarMetricFilter.IsConfigured(unavailable));
        Assert.True(TaskbarMetricFilter.IsConfigured(stale));
        Assert.Single(TaskbarMetricFilter.SelectConfigured([unavailable, stale]));
    }

    [Fact]
    public void TaskbarFilterKeepsCachedBarsWhenTheLatestRefreshFailed()
    {
        // CoreUsageSnapshotSource carries the last good bars over with an error badge when a
        // refresh fails. Dropping that envelope made providers vanish from the taskbar until the
        // next clean refresh, so an error badge alone must not disqualify usable bars.
        var withBars = new UsageSnapshotData(
            "antigravity", "Antigravity", null,
            [
                new ProgressMetricData("Weekly", 12, 100),
                new BadgeMetricData("Error", "Antigravity connection failed.", "#EF4444")
            ],
            DateTimeOffset.UtcNow) { Error = "Antigravity connection failed.", ErrorCategory = "Network" };
        var placeholder = new UsageSnapshotData(
            "antigravity", "Antigravity", null,
            [new BadgeMetricData("Error", "Not configured. Sign in first.", "#EF4444")],
            DateTimeOffset.UtcNow) { Error = "Not configured. Sign in first.", ErrorCategory = "NotConfigured" };

        Assert.True(TaskbarMetricFilter.IsConfigured(withBars));
        Assert.False(TaskbarMetricFilter.IsConfigured(placeholder));
    }

    [Fact]
    public void MetricVisibilityDoesNotInventAFallbackWhenNothingIsPinned()
    {
        var metrics = new[]
        {
            new MetricDisplay("Session", "41%", "", 0.41, "normal", "Codex", IsMeter: true),
            new MetricDisplay("Weekly", "72%", "", 0.72, "normal", "Codex", IsMeter: true)
        };

        Assert.Empty(MetricVisibility.SelectPinned(metrics, _ => false));
        Assert.Single(MetricVisibility.SelectPinned(metrics, metric => metric.Label == "Weekly", 1));
    }

    [Fact]
    public void TaskbarLayoutReservesEdgeSpaceAndCompactsWithoutDroppingIdentity()
    {
        var decision = TaskbarLayoutDecision.Calculate(2400, 1920);

        Assert.Equal(1732, decision.AvailableWidthDip);
        Assert.True(decision.CompactValues);
        Assert.InRange(decision.Scale, 0.55, 1);
    }

    [Fact]
    public void WidgetPlacementIsKeyedByDisplayIdentityAndClamped()
    {
        var settings = new UserSettings();
        settings.SetWidgetPlacement("\\\\.\\DISPLAY2", 2500);

        Assert.Equal(2000, settings.GetWidgetPlacement("\\\\.\\display2").EdgeOffsetDip);
        Assert.Equal(TaskbarWidgetPlacement.Default.EdgeOffsetDip,
            settings.GetWidgetPlacement(MonitorPlacementService.PrimaryMonitorId).EdgeOffsetDip);
    }

    [Fact]
    public void OutOfRangeOffsetClampsToTheNearestTaskbarEdge()
    {
        var taskbar = new Rectangle(0, 1040, 1920, 40);
        var result = TaskbarStripPlacement.Calculate(taskbar, 163, 1732, 1881.6, 96);

        Assert.True(result.ResetPersistedOffset);
        Assert.Equal(1753, result.EdgeOffsetDip);
        Assert.True(result.IsSane(taskbar));
        Assert.Equal(taskbar.Left + 4, result.Bounds.Left);
    }

    [Fact]
    public void LeftEdgePlacementSurvivesWidgetGrowingAfterRefresh()
    {
        var taskbar = new Rectangle(0, 1040, 1920, 40);

        var result = TaskbarStripPlacement.Calculate(taskbar, 300, 1732, 1756, 96);

        Assert.True(result.ResetPersistedOffset);
        Assert.Equal(1616, result.EdgeOffsetDip);
        Assert.Equal(taskbar.Left + 4, result.Bounds.Left);
    }

    [Fact]
    public void OldSymmetricLeadingClampMigratesToTheActualLeadingEdge()
    {
        var taskbar = new Rectangle(0, 1040, 1920, 40);
        var result = TaskbarStripPlacement.Calculate(taskbar, 160, 160, 1580, 96, 180);

        Assert.True(result.ResetPersistedOffset);
        Assert.Equal(1756, result.EdgeOffsetDip);
        Assert.Equal(4, result.Bounds.Left);
    }

    [Fact]
    public void ValidRightEdgeOffsetRemainsInPhysicalPixelsAfterDpiConversion()
    {
        var taskbar = new Rectangle(0, 1040, 1920, 40);
        var result = TaskbarStripPlacement.Calculate(taskbar, 163, 1732, 180, 144);

        Assert.False(result.ResetPersistedOffset);
        Assert.Equal(270, result.PhysicalEdgeOffset);
        Assert.Equal(180, result.EdgeOffsetDip);
        Assert.Equal(1650, result.Bounds.Right);
    }

    [Fact]
    public void DraggingLeftClampsAtTheLeadingTaskbarMarginWithoutWrapping()
    {
        var taskbar = new Rectangle(0, 1040, 1920, 40);
        var widget = new Rectangle(1600, 1040, 160, 40);

        var edgeOffset = TaskbarStripPlacement.CalculateDraggedEdgeOffset(
            taskbar, widget, -2000, 0, 96, 180);
        var result = TaskbarStripPlacement.Calculate(taskbar, 160, 160, edgeOffset, 96, 180);

        Assert.Equal(1756, edgeOffset);
        Assert.Equal(4, result.Bounds.Left);
        Assert.Equal(1756, result.PhysicalEdgeOffset);
    }

    [Fact]
    public void DraggingRightClampsAtTheTrailingTaskbarMarginWithoutWrapping()
    {
        var taskbar = new Rectangle(0, 1040, 1920, 40);
        var widget = new Rectangle(1600, 1040, 160, 40);

        var edgeOffset = TaskbarStripPlacement.CalculateDraggedEdgeOffset(
            taskbar, widget, 2000, 0, 96, 180);
        var result = TaskbarStripPlacement.Calculate(taskbar, 160, 160, edgeOffset, 96, 180);

        Assert.Equal(180, edgeOffset);
        Assert.Equal(1580, result.Bounds.Left);
        Assert.Equal(180, result.PhysicalEdgeOffset);
    }

    [Fact]
    public void TrayNotificationAreaGetsARealGap()
    {
        var taskbar = new Rectangle(0, 1104, 2048, 48);
        var trayNotify = new Rectangle(1827, 1104, 221, 48);
        var reserved = TaskbarStripPlacement.CalculateReservedEdgeDip(taskbar, trayNotify, 96);
        var result = TaskbarStripPlacement.Calculate(taskbar, 163, 163, 180, 96, reserved);

        Assert.True(reserved >= 229);
        Assert.True(result.Bounds.Right <= trayNotify.Left - 8);
    }

    [Fact]
    public void WidgetPlacementSurvivesSettingsSerialization()
    {
        var settings = new UserSettings();
        settings.SetWidgetPlacement(MonitorPlacementService.PrimaryMonitorId, 64.5);
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<UserSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(64.5, restored!.GetWidgetPlacement(MonitorPlacementService.PrimaryMonitorId).EdgeOffsetDip);
    }

    [Fact]
    public void TaskbarPositionLockDefaultsToLockedAndMigratesMissingValues()
    {
        Assert.True(UserSettings.Default.TaskbarPositionLocked);

        var legacy = JsonSerializer.Deserialize<UserSettings>("{\"SelectedMonitor\":\"PRIMARY\"}");
        Assert.NotNull(legacy);
        Assert.True(legacy!.TaskbarPositionLocked);

        var unlocked = new UserSettings { TaskbarPositionLocked = false };
        var roundTrip = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(unlocked));
        Assert.NotNull(roundTrip);
        Assert.False(roundTrip!.TaskbarPositionLocked);
    }

    [Fact]
    public void TaskbarWidgetDefaultKeepsLegacySurfaceValuesStable()
    {
        Assert.Equal(StatusSurfaceMode.TaskbarWidget, UserSettings.Default.StatusSurface);
        Assert.True(UserSettings.Default.StartAtLogin);

        var legacyWidget = JsonSerializer.Deserialize<UserSettings>("{\"StatusSurface\":0}");
        var legacyTray = JsonSerializer.Deserialize<UserSettings>("{\"StatusSurface\":1}");

        Assert.Equal(StatusSurfaceMode.TaskbarWidget, legacyWidget!.StatusSurface);
        Assert.Equal(StatusSurfaceMode.TrayOnly, legacyTray!.StatusSurface);
    }

    [Fact]
    public void SpendMetricPreferenceSurvivesSerializationAndNormalizesUnknownValues()
    {
        var settings = new UserSettings { SpendMetric = "tokens" };
        var restored = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Equal("tokens", restored!.SpendMetric);
        Assert.Equal("cost-mtok", SettingsMigration.NormalizeSpendMetric("Cost/MTok"));
        Assert.Equal("cost", SettingsMigration.NormalizeSpendMetric("invalid"));
    }

    [Theory]
    [InlineData("full", "full")]
    [InlineData("REDUCED", "reduced")]
    [InlineData("unexpected", "system")]
    [InlineData(null, "system")]
    public void MotionPreferenceIsStoredAsAnExplicitThreeWayChoice(string? value, string expected)
    {
        var settings = new UserSettings { MotionPreference = value! };

        SettingsMigration.Normalize(settings);

        Assert.Equal(expected, settings.MotionPreference);
        var restored = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings));
        Assert.Equal(expected, restored!.MotionPreference);
    }

    [Fact]
    public void TaskbarGlyphKeepsAVisibleTailForNearFullQuotas()
    {
        // This mirrors OpenUsage's MenuBarBarGeometry rule. A nearly exhausted quota must not
        // collapse into a visually solid 100% bar at taskbar scale.
        Assert.Equal(0.85, TaskbarGlyphRenderer.VisualFraction(0.96), precision: 6);
        Assert.Equal(0.7, TaskbarGlyphRenderer.VisualFraction(0.71), precision: 6);
        Assert.Equal(1, TaskbarGlyphRenderer.VisualFraction(1));
    }

    [Fact]
    public void TaskbarGlyphIgnoresPlaceholderRowsButKeepsRealZeroPercentData()
    {
        var noData = new MetricDisplay("Session", "No data", "", 0, "neutral", "Codex", IsMeter: true);
        var zero = new MetricDisplay("Session", "0%", "", 0, "normal", "Codex", IsMeter: true);

        Assert.False(TaskbarGlyphRenderer.HasRenderableData(noData));
        Assert.True(TaskbarGlyphRenderer.HasRenderableData(zero));
    }

    [Fact]
    public void TaskbarTooltipContainsExactProviderValueAndReset()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(2);
        var metric = new MetricDisplay("Weekly", "41%", "", 0.41, "normal", "Codex", reset, IsMeter: true);

        var tooltip = TaskbarGlyphRenderer.BuildTooltip([metric]);

        Assert.StartsWith("TokenBurn | Codex Weekly: 41% (resets in ", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("No data", tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskbarGlyphRendersVisiblePixelsForAZeroPercentMetric()
    {
        var metric = new MetricDisplay("Session", "0%", "", 0, "normal", "Codex", IsMeter: true);
        var image = Assert.IsAssignableFrom<BitmapSource>(TaskbarGlyphRenderer.Render([metric]));
        var pixels = new byte[image.PixelHeight * image.PixelWidth * 4];

        image.CopyPixels(pixels, image.PixelWidth * 4, 0);

        Assert.Equal(48, image.PixelWidth);
        Assert.Equal(48, image.PixelHeight);
        Assert.Contains(pixels, alpha => alpha > 0);
    }

    [Fact]
    public void EmptyTaskbarGlyphIsTransparentInsteadOfUsingTheAppIcon()
    {
        var image = Assert.IsAssignableFrom<BitmapSource>(TaskbarGlyphRenderer.Render([]));
        var pixels = new byte[image.PixelHeight * image.PixelWidth * 4];

        image.CopyPixels(pixels, image.PixelWidth * 4, 0);

        Assert.DoesNotContain(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
    }

    [Fact]
    public void ClaudeLoginResolverKeepsWindowsScriptShimsRunnable()
    {
        var command = ClaudeLoginCommand.ResolveCommandPath();
        if (command is null) return; // Claude Code is optional on build agents.

        var startInfo = ClaudeLoginCommand.CreateStartInfo();
        Assert.Contains("auth", startInfo.ArgumentList);
        Assert.Contains("login", startInfo.ArgumentList);
        if (Path.GetExtension(command).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            Assert.Contains("powershell", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
