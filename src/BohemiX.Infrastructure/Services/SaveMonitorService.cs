using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class SaveMonitorService : ISaveMonitorService
{
    private const int PendingChangeCapacity = 512;
    private readonly ILogger logger;

    public SaveMonitorService(ILogger logger)
    {
        this.logger = logger.ForContext<SaveMonitorService>();
    }

    public async IAsyncEnumerable<SaveFileChange> WatchAsync(
        string saveDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<SaveFileChange>(new BoundedChannelOptions(PendingChangeCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        if (!Directory.Exists(saveDirectory))
        {
            logger.Warning("Save directory does not exist: {SaveDirectory}", saveDirectory);
            yield break;
        }

        using var watcher = new FileSystemWatcher(saveDirectory, "*.whs")
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Created += (_, args) => TryPublish(channel, args.FullPath, SaveFileChangeKind.Created);
        watcher.Changed += (_, args) => TryPublish(channel, args.FullPath, SaveFileChangeKind.Changed);
        watcher.Deleted += (_, args) => TryPublish(channel, args.FullPath, SaveFileChangeKind.Deleted);
        watcher.Renamed += (_, args) => TryPublish(channel, args.FullPath, SaveFileChangeKind.Renamed);
        watcher.Error += (_, args) => logger.Error(args.GetException(), "Save watcher failed for {SaveDirectory}", saveDirectory);

        logger.Information("Watching save directory {SaveDirectory}", saveDirectory);

        await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return change;
        }
    }

    private void TryPublish(Channel<SaveFileChange> channel, string path, SaveFileChangeKind kind)
    {
        if (!channel.Writer.TryWrite(new SaveFileChange(path, kind, DateTimeOffset.UtcNow)))
        {
            logger.Warning("Dropped save change event for {SavePath}", path);
        }
    }
}
