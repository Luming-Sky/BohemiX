using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerBridgeMonitorService : ITrackerBridgeMonitorService
{
    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;

    public TrackerBridgeMonitorService(
        IApplicationPathService applicationPathService,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<TrackerBridgeMonitorService>();
    }

    public async IAsyncEnumerable<DateTimeOffset> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.TrackerDirectory);

        if (!File.Exists(paths.TrackerBridgeEventsPath))
        {
            await File.WriteAllTextAsync(paths.TrackerBridgeEventsPath, string.Empty, cancellationToken);
        }

        var channel = Channel.CreateBounded<DateTimeOffset>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
        using var watcher = new FileSystemWatcher(paths.TrackerDirectory, Path.GetFileName(paths.TrackerBridgeEventsPath))
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Created += (_, _) => TrySignal(channel);
        watcher.Changed += (_, _) => TrySignal(channel);
        watcher.Renamed += (_, _) => TrySignal(channel);
        watcher.Error += (_, args) => logger.Error(args.GetException(), "Tracker bridge watcher failed for {BridgePath}", paths.TrackerBridgeEventsPath);

        logger.Information("Watching tracker bridge file {BridgePath}", paths.TrackerBridgeEventsPath);

        await foreach (var signal in channel.Reader.ReadAllAsync(cancellationToken))
        {
            _ = signal;
            while (channel.Reader.TryRead(out var _))
            {
            }

            await Task.Delay(250, cancellationToken);

            yield return signal;
        }
    }

    private static void TrySignal(Channel<DateTimeOffset> channel)
    {
        channel.Writer.TryWrite(DateTimeOffset.UtcNow);
    }
}
