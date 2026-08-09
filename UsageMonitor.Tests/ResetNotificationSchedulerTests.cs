using System.Text.Json;
using UsageMonitor.Core;
using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class ResetNotificationSchedulerTests
{
    [Fact]
    public void FreshSettingsEnableNotificationsForEveryKnownProvider()
    {
        var settings = UserSettings.Default;

        Assert.True(settings.NotificationsEnabled);
        Assert.Equal(
            ProviderCatalog.DefaultDescriptors.Select(provider => provider.Id),
            settings.NotificationProviderIds);
    }

    [Fact]
    public void ExplicitEmptyProviderSelectionSurvivesNormalizationAndRoundTrip()
    {
        var settings = new UserSettings { NotificationProviderIds = [] };

        SettingsMigration.Normalize(settings);
        var restored = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        Assert.Empty(restored!.NotificationProviderIds);
    }

    [Fact]
    public void SchedulesFutureLinesAndFiresEachLineOnce()
    {
        var now = At(10);
        var scheduler = new ResetNotificationScheduler();
        var notifications = new List<ResetNotification>();

        scheduler.Observe([Snapshot("codex", now.AddSeconds(5), now.AddSeconds(8))], true, ["codex"], now);
        Assert.Equal(2, scheduler.ScheduledCount);

        Assert.Equal(0, scheduler.Tick(now.AddSeconds(4), notifications.Add));
        Assert.Equal(1, scheduler.Tick(now.AddSeconds(5), notifications.Add));
        Assert.Single(notifications);
        Assert.Equal("Session", notifications[0].MetricLabel);

        Assert.Equal(0, scheduler.Tick(now.AddSeconds(6), notifications.Add));
        Assert.Single(notifications);
        Assert.Equal(1, scheduler.Tick(now.AddSeconds(8), notifications.Add));
        Assert.Equal(2, notifications.Count);
        Assert.Equal(0, scheduler.Tick(now.AddSeconds(9), notifications.Add));
    }

    [Fact]
    public void DoesNotNotifyForAnAlreadyExpiredInitialSnapshot()
    {
        var now = At(20);
        var scheduler = new ResetNotificationScheduler();
        var notifications = new List<ResetNotification>();

        scheduler.Observe([Snapshot("codex", now.AddSeconds(-1))], true, ["codex"], now);

        Assert.Equal(0, scheduler.ScheduledCount);
        Assert.Equal(0, scheduler.Tick(now, notifications.Add));
        Assert.Empty(notifications);
    }

    [Fact]
    public void ProviderFilterAndDisabledStateClearTimers()
    {
        var now = At(30);
        var scheduler = new ResetNotificationScheduler();
        var notifications = new List<ResetNotification>();
        var snapshots = new[] { Snapshot("codex", now.AddSeconds(5)), Snapshot("claude-code", now.AddSeconds(5)) };

        scheduler.Observe(snapshots, true, ["codex"], now);
        Assert.Equal(1, scheduler.ScheduledCount);
        Assert.Equal(1, scheduler.Tick(now.AddSeconds(5), notifications.Add));
        Assert.Equal("codex", notifications[0].ProviderId);

        scheduler.Observe(snapshots, true, ["codex", "claude-code"], now);
        scheduler.Observe(snapshots, false, ["codex", "claude-code"], now);
        Assert.Equal(0, scheduler.ScheduledCount);
        Assert.Equal(0, scheduler.Tick(now.AddSeconds(6), notifications.Add));
    }

    [Fact]
    public void NewResetTimestampCreatesANewGeneration()
    {
        var now = At(40);
        var scheduler = new ResetNotificationScheduler();
        var notifications = new List<ResetNotification>();

        scheduler.Observe([Snapshot("codex", now.AddSeconds(5))], true, ["codex"], now);
        scheduler.Observe([Snapshot("codex", now.AddSeconds(8))], true, ["codex"], now.AddSeconds(1));

        Assert.Equal(0, scheduler.Tick(now.AddSeconds(5), notifications.Add));
        Assert.Empty(notifications);
        Assert.Equal(0, scheduler.Tick(now.AddSeconds(7), notifications.Add));
        Assert.Equal(1, scheduler.Tick(now.AddSeconds(8), notifications.Add));
        Assert.Single(notifications);
        Assert.Equal(now.AddSeconds(8), notifications[0].ResetAt);
    }

    [Fact]
    public void WarningAndErrorSnapshotsDoNotReplaceExistingTimers()
    {
        var now = At(50);
        var scheduler = new ResetNotificationScheduler();
        var notifications = new List<ResetNotification>();

        scheduler.Observe([Snapshot("codex", now.AddSeconds(5))], true, ["codex"], now);
        scheduler.Observe([Snapshot("codex", now.AddSeconds(1), warning: "Rate limited")], true, ["codex"], now);

        Assert.Equal(1, scheduler.ScheduledCount);
        Assert.Equal(1, scheduler.Tick(now.AddSeconds(5), notifications.Add));
    }

    private static UsageSnapshotData Snapshot(
        string providerId,
        DateTimeOffset sessionReset,
        DateTimeOffset? weeklyReset = null,
        string? warning = null)
    {
        var lines = new List<UsageMetricData>
        {
            new ProgressMetricData("Session", 50, 100, ResetsAt: sessionReset)
        };
        if (weeklyReset is { } reset)
            lines.Add(new ProgressMetricData("Weekly", 50, 100, ResetsAt: reset));

        return new UsageSnapshotData(providerId, providerId, null, lines, sessionReset.AddMinutes(-1))
        {
            Warning = warning,
            Error = warning is null ? null : "refresh failed"
        };
    }

    private static DateTimeOffset At(int second)
        => new(2030, 1, 1, 0, 0, second, TimeSpan.Zero);
}
