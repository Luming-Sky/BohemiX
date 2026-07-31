using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class TrackerRuntimeServiceTests
{
    [Fact]
    public async Task RefreshAsync_BuildsCompleteSnapshot()
    {
        var summary = CreateSummary(totalEvents: 12);
        var entity = CreateEntity("quest.main");
        var achievement = new AchievementProgress("first_step", 1, 1, true, DateTimeOffset.UtcNow);
        var projection = new FakeProjectionService
        {
            IngestResult = new TrackerIngestResult(3, 1, 42, summary),
            Entities = [entity],
            RecentEvents = ["Quest started"],
            Achievements = [achievement]
        };
        using var service = CreateService(new FakeBridgeMonitorService(), projection);

        var update = await service.RefreshAsync();

        Assert.False(update.IsAutomatic);
        Assert.Equal(3, update.ProcessedEvents);
        Assert.Equal(1, update.SkippedLines);
        Assert.Equal(summary, update.Snapshot.Summary);
        Assert.Equal(entity, Assert.Single(update.Snapshot.VisibleEntities));
        Assert.Equal("Quest started", Assert.Single(update.Snapshot.RecentEventLabels));
        Assert.Equal(achievement, Assert.Single(update.Snapshot.Achievements));
        Assert.Same(update.Snapshot, service.Current);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var monitor = new FakeBridgeMonitorService();
        using var service = CreateService(monitor, new FakeProjectionService());

        service.Start();
        service.Start();
        service.Start();

        await WaitUntilAsync(() => monitor.WatchCount == 1);
        Assert.Equal(1, monitor.WatchCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task AutomaticChange_IngestsAndPublishesCompleteSnapshot()
    {
        var monitor = new FakeBridgeMonitorService();
        var projection = new FakeProjectionService
        {
            IngestResult = new TrackerIngestResult(2, 0, 21, CreateSummary(totalEvents: 8)),
            Entities = [CreateEntity("quest.side")],
            RecentEvents = ["Quest completed"],
            Achievements = [new AchievementProgress("explorer", 2, 10, false, null)]
        };
        using var service = CreateService(monitor, projection);
        var received = new TaskCompletionSource<TrackerRuntimeUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Updated += update => received.TrySetResult(update);

        service.Start();
        await WaitUntilAsync(() => monitor.WatchCount == 1);
        monitor.Signal();
        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(update.IsAutomatic);
        Assert.Equal(2, update.ProcessedEvents);
        Assert.Equal(8, update.Snapshot.Summary.TotalEvents);
        Assert.Single(update.Snapshot.VisibleEntities);
        Assert.Single(update.Snapshot.RecentEventLabels);
        Assert.Single(update.Snapshot.Achievements);
        Assert.Equal(1, projection.IngestCount);
    }

    [Fact]
    public async Task ManualAndAutomaticRefreshes_AreSerialized()
    {
        var monitor = new FakeBridgeMonitorService();
        var projection = new FakeProjectionService
        {
            IngestResult = new TrackerIngestResult(1, 0, 10, CreateSummary(totalEvents: 1)),
            BlockFirstIngest = true
        };
        using var service = CreateService(monitor, projection);
        service.Start();
        await WaitUntilAsync(() => monitor.WatchCount == 1);

        var manualRefresh = service.RefreshAsync();
        await projection.FirstIngestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Signal();
        await Task.Delay(100);

        Assert.Equal(1, projection.IngestCount);

        projection.ReleaseFirstIngest.TrySetResult();
        await manualRefresh;
        await WaitUntilAsync(() => projection.IngestCount == 2);
        Assert.Equal(1, projection.MaxConcurrentIngests);
    }

    [Fact]
    public async Task FailedRefresh_PreservesLastSuccessfulSnapshot()
    {
        var projection = new FakeProjectionService
        {
            IngestResult = new TrackerIngestResult(1, 0, 10, CreateSummary(totalEvents: 5))
        };
        using var service = CreateService(new FakeBridgeMonitorService(), projection);
        var successful = await service.RefreshAsync();
        var failure = new InvalidOperationException("bridge unavailable");
        projection.IngestException = failure;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync());

        Assert.Same(failure, thrown);
        Assert.Same(successful.Snapshot, service.Current);
        Assert.Same(failure, service.LastError);
    }

    [Fact]
    public async Task StopAsync_AllowsMonitorToRestart()
    {
        var monitor = new FakeBridgeMonitorService();
        using var service = CreateService(monitor, new FakeProjectionService());

        service.Start();
        await WaitUntilAsync(() => monitor.WatchCount == 1);
        await service.StopAsync();
        service.Start();
        await WaitUntilAsync(() => monitor.WatchCount == 2);

        Assert.Equal(2, monitor.WatchCount);
        await service.StopAsync();
    }

    [Fact]
    public async Task SubscriberFailure_DoesNotPreventOtherSubscribers()
    {
        var monitor = new FakeBridgeMonitorService();
        using var service = CreateService(monitor, new FakeProjectionService());
        var received = new TaskCompletionSource<TrackerRuntimeUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Updated += _ => throw new InvalidOperationException("subscriber failed");
        service.Updated += update => received.TrySetResult(update);

        service.Start();
        await WaitUntilAsync(() => monitor.WatchCount == 1);
        monitor.Signal();

        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(update.IsAutomatic);
    }

    private static TrackerRuntimeService CreateService(
        ITrackerBridgeMonitorService monitor,
        FakeProjectionService projection) =>
        new(monitor, projection, projection, Logger.None);

    private static TrackerSummary CreateSummary(int totalEvents) => new(
        totalEvents,
        1,
        1,
        0,
        0,
        "Quest started",
        DateTimeOffset.UtcNow,
        10,
        20,
        30);

    private static TrackerEntityState CreateEntity(string id) => new(
        id,
        TrackerEntityKind.Quest,
        id,
        TrackerEntityStatus.Active,
        true,
        DateTimeOffset.UtcNow,
        null,
        null,
        null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeBridgeMonitorService : ITrackerBridgeMonitorService
    {
        private readonly Channel<DateTimeOffset> changes = Channel.CreateUnbounded<DateTimeOffset>();
        private int watchCount;

        public int WatchCount => Volatile.Read(ref watchCount);

        public void Signal() => changes.Writer.TryWrite(DateTimeOffset.UtcNow);

        public async IAsyncEnumerable<DateTimeOffset> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref watchCount);
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
    }

    private sealed class FakeProjectionService : ITrackerProjectionService, ITrackerAchievementService
    {
        private int ingestCount;
        private int activeIngests;
        private int maxConcurrentIngests;

        public TrackerIngestResult IngestResult { get; set; } =
            new(0, 0, 0, TrackerSummary.Empty);

        public IReadOnlyList<TrackerEntityState> Entities { get; set; } = [];

        public IReadOnlyList<string> RecentEvents { get; set; } = [];

        public IReadOnlyList<AchievementProgress> Achievements { get; set; } = [];

        public Exception? IngestException { get; set; }

        public bool BlockFirstIngest { get; set; }

        public TaskCompletionSource FirstIngestEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstIngest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int IngestCount => Volatile.Read(ref ingestCount);

        public int MaxConcurrentIngests => Volatile.Read(ref maxConcurrentIngests);

        public async Task<TrackerIngestResult> IngestBridgeFileAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref ingestCount);
            var active = Interlocked.Increment(ref activeIngests);
            UpdateMaximum(ref maxConcurrentIngests, active);
            try
            {
                if (BlockFirstIngest && count == 1)
                {
                    FirstIngestEntered.TrySetResult();
                    await ReleaseFirstIngest.Task.WaitAsync(cancellationToken);
                }

                if (IngestException is not null)
                {
                    throw IngestException;
                }

                return IngestResult;
            }
            finally
            {
                Interlocked.Decrement(ref activeIngests);
            }
        }

        public Task<TrackerSummary> LoadSummaryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IngestResult.Summary);

        public Task<IReadOnlyList<TrackerEntityState>> LoadVisibleEntitiesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Entities);

        public Task<IReadOnlyList<string>> LoadRecentEventLabelsAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RecentEvents);

        public Task<IReadOnlyList<AchievementProgress>> EvaluateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Achievements);

        public Task<IReadOnlyList<AchievementProgress>> LoadProgressAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Achievements);

        private static void UpdateMaximum(ref int target, int candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
