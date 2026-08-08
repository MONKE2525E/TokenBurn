using UsageMonitor.Desktop;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

public sealed class QuotaAlertTests
{
    [Fact]
    public void AlertsOnlyOnThresholdCrossingAndResetWhenDisabled()
    {
        var service = new QuotaAlertService();
        var messages = new List<string>();

        service.Observe([Snapshot("codex", 80)], enabled: true, almostOut: true,
            cuttingItClose: false, willRunOut: false, messages.Add);
        service.Observe([Snapshot("codex", 80)], enabled: true, almostOut: true,
            cuttingItClose: false, willRunOut: false, messages.Add);
        Assert.Single(messages);

        service.Observe([Snapshot("codex", 40)], enabled: true, almostOut: true,
            cuttingItClose: false, willRunOut: false, messages.Add);
        service.Observe([Snapshot("codex", 80)], enabled: true, almostOut: true,
            cuttingItClose: false, willRunOut: false, messages.Add);
        Assert.Equal(2, messages.Count);

        service.Observe([Snapshot("codex", 80)], enabled: false, almostOut: true,
            cuttingItClose: false, willRunOut: false, messages.Add);
        service.Observe([Snapshot("codex", 80)], enabled: true, almostOut: true,
            cuttingItClose: false, willRunOut: false, messages.Add);
        Assert.Equal(3, messages.Count);
    }

    private static UsageSnapshotData Snapshot(string providerId, double used)
        => new(providerId, providerId, null,
            [new ProgressMetricData("Weekly", used, 100)],
            DateTimeOffset.UtcNow);
}
