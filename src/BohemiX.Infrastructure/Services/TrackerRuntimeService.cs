using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerRuntimeService : ITrackerRuntimeService, IDisposable
{
    private readonly ITrackerBridgeMonitorService bridgeMonitorService;
    private readonly ITrackerProjectionService projectionService;
    private readonly ITrackerAchievementService achievementService;
    private readonly ILogger logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object lifecycleGate = new();
    private CancellationTokenSource? monitorCancellation;
    private Task? monitorTask;
    private TrackerRuntimeSnapshot current = TrackerRuntimeSnapshot.Empty;
    private Exception? lastError;
    private int disposed;

    public TrackerRuntimeService(
        ITrackerBridgeMonitorService bridgeMonitorService,
        ITrackerProjectionService projectionService,
        ITrackerAchievementService achievementService,
        ILogger logger)
    {
        this.bridgeMonitorService = bridgeMonitorService;
        this.projectionService = projectionService;
        this.achievementService = achievementService;
        this.logger = logger.ForContext<TrackerRuntimeService>();
    }

    public TrackerRuntimeSnapshot Current => Volatile.Read(ref current);

    public Exception? LastError => Volatile.Read(ref lastError);

    public event Action<TrackerRuntimeUpdate>? Updated;

    public async Task<TrackerRuntimeUpdate> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var ingest = await projectionService.IngestBridgeFileAsync(cancellationToken);
            return await BuildUpdateAsync(
                ingest.Summary,
                ingest.ProcessedEvents,
                ingest.SkippedLines,
                isAutomatic: false,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref lastError, ex);
            logger.Error(ex, "Tracker runtime refresh failed");
            throw;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    public void Start()
    {
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (monitorTask is { IsCompleted: false })
            {
                return;
            }

            monitorCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            monitorCancellation = cancellation;
            monitorTask = Task.Run(() => MonitorAsync(cancellation.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (lifecycleGate)
        {
            cancellation = monitorCancellation;
            task = monitorTask;
            cancellation?.Cancel();
        }

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (lifecycleGate)
        {
            if (ReferenceEquals(task, monitorTask))
            {
                monitorTask = null;
            }

            if (ReferenceEquals(cancellation, monitorCancellation))
            {
                monitorCancellation = null;
            }
        }

        cancellation?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in bridgeMonitorService.WatchAsync(cancellationToken))
            {
                await refreshGate.WaitAsync(cancellationToken);
                try
                {
                    var ingest = await projectionService.IngestBridgeFileAsync(cancellationToken);
                    var update = await BuildUpdateAsync(
                        ingest.Summary,
                        ingest.ProcessedEvents,
                        ingest.SkippedLines,
                        isAutomatic: true,
                        cancellationToken);
                    Publish(update);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(ref lastError, ex);
                    logger.Error(ex, "Tracker runtime automatic projection failed");
                }
                finally
                {
                    refreshGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref lastError, ex);
            logger.Error(ex, "Tracker runtime monitor stopped unexpectedly");
        }
    }

    private async Task<TrackerRuntimeUpdate> BuildUpdateAsync(
        TrackerSummary summary,
        int processedEvents,
        int skippedLines,
        bool isAutomatic,
        CancellationToken cancellationToken)
    {
        var entitiesTask = projectionService.LoadVisibleEntitiesAsync(cancellationToken);
        var recentEventsTask = projectionService.LoadRecentEventLabelsAsync(6, cancellationToken);
        var achievementsTask = achievementService.EvaluateAsync(cancellationToken);
        await Task.WhenAll(entitiesTask, recentEventsTask, achievementsTask);

        var snapshot = new TrackerRuntimeSnapshot(
            summary,
            entitiesTask.Result.ToArray(),
            recentEventsTask.Result.ToArray(),
            achievementsTask.Result.ToArray(),
            DateTimeOffset.UtcNow);
        Volatile.Write(ref current, snapshot);
        Volatile.Write(ref lastError, null);

        return new TrackerRuntimeUpdate(snapshot, processedEvents, skippedLines, isAutomatic);
    }

    private void Publish(TrackerRuntimeUpdate update)
    {
        var handlers = Updated;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<TrackerRuntimeUpdate> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(update);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Tracker runtime update subscriber failed");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        refreshGate.Dispose();
    }
}
