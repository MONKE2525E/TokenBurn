using System.Text.Json;
using System.Drawing;
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
    public void TaskbarLayoutReservesEdgeSpaceForTheRenderer()
    {
        var decision = TaskbarLayoutDecision.Calculate(1920);

        Assert.Equal(1732, decision.AvailableWidthDip);
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
    public void TrayTooltipJoinsProviderValuesAndTruncatesToTheNotificationLimit()
    {
        Assert.Equal("TokenBurn | no provider data yet", TrayIconService.BuildTooltip([], "Countdown"));

        var reset = DateTimeOffset.UtcNow.AddHours(2);
        var values = new[]
        {
            new MetricDisplay("Weekly", "$12.34", "", 0.5, "normal", "Codex", reset),
            new MetricDisplay("Session", "41%", "", 0.41, "normal", "Claude Code")
        };
        var tooltip = TrayIconService.BuildTooltip(values, "Countdown");
        Assert.StartsWith("TokenBurn | Codex $12.34", tooltip, StringComparison.Ordinal);
        Assert.Contains("resets in", tooltip, StringComparison.Ordinal);
        Assert.Contains("Claude Code 41%", tooltip, StringComparison.Ordinal);
        Assert.True(tooltip.Length <= 63, $"the tray tooltip must fit the 63-char limit, got {tooltip.Length}");
    }

    [Fact]
    public void PopupHostResolutionSkipsRefusedBuildsUntilTheFileChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", "popup-host-pick", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var devBuild = Path.Combine(root, "dev-host.exe");
            var productionBuild = Path.Combine(root, "production-host.exe");
            File.WriteAllBytes(devBuild, [0x4D, 0x5A]);
            File.WriteAllBytes(productionBuild, [0x4D, 0x5A]);
            // Newest-wins ordering: the dev build is newer and would normally win.
            File.SetLastWriteTimeUtc(devBuild, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(productionBuild, DateTime.UtcNow.AddMinutes(-1));
            var unusable = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
            {
                [devBuild] = File.GetLastWriteTimeUtc(devBuild)
            };

            Assert.Equal(productionBuild, TauriPopupBridge.PickPopupHostCandidate([devBuild, productionBuild], unusable));

            // A rebuild of the same path clears the refusal via the timestamp comparison.
            File.SetLastWriteTimeUtc(devBuild, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(devBuild, TauriPopupBridge.PickPopupHostCandidate([devBuild, productionBuild], unusable));

            // If every candidate was refused, the newest still wins: a refused build beats no popup.
            var stale = File.GetLastWriteTimeUtc(devBuild);
            unusable[devBuild] = stale;
            Assert.Equal(devBuild, TauriPopupBridge.PickPopupHostCandidate([devBuild], unusable));

            Assert.Null(TauriPopupBridge.PickPopupHostCandidate([], unusable));
            Assert.Null(TauriPopupBridge.PickPopupHostCandidate([Path.Combine(root, "missing-host.exe")], unusable));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClaudeLoginResolverRunsDeterministicallyFromAnInjectedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "UsageMonitorTests", "claude-login", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "claude.ps1"), "");
            File.WriteAllText(Path.Combine(root, "claude.exe"), "");

            // Resolution prefers shims in PATH order (claude.exe first).
            Assert.Equal(Path.Combine(root, "claude.exe"), ClaudeLoginCommand.ResolveCommandPath(root));
            File.Delete(Path.Combine(root, "claude.exe"));
            Assert.Equal(Path.Combine(root, "claude.ps1"), ClaudeLoginCommand.ResolveCommandPath(root));

            // A PowerShell shim must be launched through PowerShell with -File so argument
            // boundaries survive, and the "auth login" arguments must be attached.
            var shimInfo = ClaudeLoginCommand.CreateStartInfo(Path.Combine(root, "claude.ps1"));
            Assert.Contains("powershell", shimInfo.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("-File", shimInfo.ArgumentList);
            Assert.Contains("auth", shimInfo.ArgumentList);
            Assert.Contains("login", shimInfo.ArgumentList);

            // A real executable is started directly with the same arguments.
            var exePath = Path.Combine(root, "claude.cmd");
            File.WriteAllText(exePath, "");
            var directInfo = ClaudeLoginCommand.CreateStartInfo(exePath);
            Assert.Equal(exePath, directInfo.FileName);
            Assert.Contains("auth", directInfo.ArgumentList);
            Assert.Contains("login", directInfo.ArgumentList);

            // No launcher anywhere falls back to the bare command via the shell.
            Assert.Null(ClaudeLoginCommand.ResolveCommandPath(string.Empty));
            var fallback = ClaudeLoginCommand.CreateStartInfo(null);
            Assert.Equal("claude", fallback.FileName);
            Assert.True(fallback.UseShellExecute);
            Assert.Contains("auth", fallback.Arguments, StringComparison.Ordinal);
            Assert.Contains("login", fallback.Arguments, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
