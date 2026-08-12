using UsageMonitor.Desktop;

namespace UsageMonitor.Tests;

/// <summary>
/// Regression coverage for the taskbar host's coalescing and persistence throttles:
/// SyncPromoteState must keep a promote intent that arrives behind an already-queued sync, and
/// DebouncedPlacementPersister must turn a mouse-move stream into a handful of settings writes.
/// </summary>
public sealed class TaskbarSyncAndDebounceTests
{
    [Fact]
    public void PromoteIntendedSyncIsNotDroppedByAnEarlierQueuedSync()
    {
        var state = new SyncPromoteState();
        Assert.False(state.Consume(), "no promote requested yet");

        // A non-promote request wins the queue slot first (safety timer / foreground event).
        // The SHOW/LOCATIONCHANGE promote arrives before the queued sync runs.
        state.MarkPending();

        Assert.True(state.Consume(), "the sticky promote survives the coalesced queue slot");
        Assert.False(state.Consume(), "consume is idempotent until the next promote request");
    }

    [Fact]
    public void RepeatedPromoteRequestsStayPendingUntilConsumed()
    {
        var state = new SyncPromoteState();
        state.MarkPending();
        state.MarkPending();

        Assert.True(state.Consume(), "multiple pending promotes still promote once");
        Assert.False(state.Consume(), "no promote left after the first consume");

        // A later promote request must promote again.
        state.MarkPending();
        Assert.True(state.Consume(), "a fresh promote promotes again");
    }

    [Fact]
    public void DragMouseMoveStreamCoalescesIntoSingleSettingsWrite()
    {
        var writes = new List<(string MonitorId, double OffsetDip)>();
        var persister = new DebouncedPlacementPersister(
            (monitorId, offsetDip) => writes.Add((monitorId, offsetDip)),
            TimeSpan.FromMilliseconds(250));

        // A real drag emits a Request per mouse move. Only the final flush may reach the store.
        for (var move = 0; move < 40; move++)
            persister.Request("primary", 8.0 + move * 0.5);

        Assert.Empty(writes);
        persister.Flush();
        Assert.Equal([("primary", 8.0 + 39 * 0.5)], writes);
    }

    [Fact]
    public void PersistRequestsBetweenFlushesWriteTheLatestPosition()
    {
        var writes = new List<(string MonitorId, double OffsetDip)>();
        var persister = new DebouncedPlacementPersister(
            (monitorId, offsetDip) => writes.Add((monitorId, offsetDip)),
            TimeSpan.FromMilliseconds(250));

        persister.Request("primary", 10);
        persister.Flush();
        persister.Request("primary", 12);
        persister.Request("primary", 14);
        persister.Flush();

        Assert.Equal(
            [("primary", 10.0), ("primary", 14.0)],
            writes);
    }

    [Fact]
    public void FlushWithoutAPendingRequestWritesNothing()
    {
        var writes = new List<(string MonitorId, double OffsetDip)>();
        var persister = new DebouncedPlacementPersister(
            (monitorId, offsetDip) => writes.Add((monitorId, offsetDip)),
            TimeSpan.FromMilliseconds(250));

        persister.Flush();
        Assert.Empty(writes);
    }
}
