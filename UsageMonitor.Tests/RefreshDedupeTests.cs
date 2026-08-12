using UsageMonitor.Core;
using UsageMonitor.LocalApi;

namespace UsageMonitor.Tests;

/// <summary>
/// Hostile-concurrency tests for <see cref="CoreUsageSnapshotSource"/>'s per-provider in-flight
/// refresh gate: duplicate force/stale refreshes must share one provider call instead of hitting
/// the provider once per caller, and one caller's cancellation must not abort the shared work.
/// </summary>
public sealed class RefreshDedupeTests
{
    private static readonly ProviderDescriptor Descriptor = new("codex", "Codex", "primary");    [Fact]
    public async Task ConcurrentForcedRefreshesRunTheProviderOnce()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CountingProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return start.Task.ContinueWith(
                _ => ProviderSnapshot.Success(Descriptor, [MetricLine.TextLine("Session", "1")]),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        var source = new CoreUsageSnapshotSource(new FakeCatalog(provider));

        var first = source.GetSnapshotsAsync(null, force: true);
        var second = source.GetSnapshotsAsync(null, force: true);
        var third = source.GetSnapshotsAsync(null, force: true);
        start.SetResult();

        var results = await Task.WhenAll(first, second, third);

        Assert.Equal(3, results.Length);
        Assert.All(results, result => Assert.Single(result));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AForceLandingDuringAScheduledRefreshSharesTheInFlightRun()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CountingProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return start.Task.ContinueWith(
                _ => ProviderSnapshot.Success(Descriptor, [MetricLine.TextLine("Session", "1")]),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        var source = new CoreUsageSnapshotSource(new FakeCatalog(provider));

        var scheduled = source.GetSnapshotsAsync(null, force: false);
        var forced = source.GetSnapshotsAsync(null, force: true);
        start.SetResult();

        await Task.WhenAll(scheduled, forced);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AWaitingCallersCancellationDoesNotAbortTheSharedRefresh()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CountingProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return start.Task.ContinueWith(
                _ => ProviderSnapshot.Success(Descriptor, [MetricLine.TextLine("Session", "1")]),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        var source = new CoreUsageSnapshotSource(new FakeCatalog(provider));

        using var cancel = new CancellationTokenSource();
        var originator = source.GetSnapshotsAsync(null, force: true);
        var waiter = source.GetSnapshotsAsync(null, force: true, cancellationToken: cancel.Token);

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        start.SetResult();
        var originatorResult = await originator;
        Assert.Single(originatorResult);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TheGateClearsAfterCompletionSoTheNextRefreshRunsFresh()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CountingProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return start.Task.ContinueWith(
                _ => ProviderSnapshot.Success(Descriptor, [MetricLine.TextLine("Session", "1")]),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        var source = new CoreUsageSnapshotSource(new FakeCatalog(provider));

        var first = source.GetSnapshotsAsync(null, force: true);
        start.SetResult();
        await first;
        Assert.Equal(1, calls);

        var second = source.GetSnapshotsAsync(null, force: true);
        await second;
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedSharedRefreshIsServedToEveryWaiterWithoutAnUnhandledException()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CountingProvider(() =>
        {
            Interlocked.Increment(ref calls);
            return start.Task.ContinueWith(
                _ => ProviderSnapshot.Error(Descriptor, new InvalidOperationException("provider exploded")),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        var source = new CoreUsageSnapshotSource(new FakeCatalog(provider));

        var first = source.GetSnapshotsAsync(null, force: true);
        var second = source.GetSnapshotsAsync(null, force: true);
        start.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.All(results, result =>
        {
            var snapshot = Assert.Single(result);
            Assert.NotNull(snapshot.ErrorCategory);
        });
    }

    private sealed class CountingProvider(Func<Task<ProviderSnapshot>> refresh) : IUsageProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("codex", "Codex", "primary");

        public Task<ProviderSnapshot> RefreshAsync(ProviderContext context, CancellationToken cancellationToken = default)
            => refresh();
    }

    private sealed class FakeCatalog(IUsageProvider provider) : IUsageProviderCatalog
    {
        public IReadOnlyList<IUsageProvider> Providers { get; } = [provider];
        public IUsageProvider? Find(string providerId) =>
            provider.Descriptor.Id.Equals(providerId, StringComparison.OrdinalIgnoreCase) ? provider : null;
    }
}
