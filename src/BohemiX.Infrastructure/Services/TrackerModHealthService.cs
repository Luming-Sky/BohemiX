using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerModHealthService : ITrackerModHealthService
{
    private const string ModId = "bohemix-tracker";
    private const string PakFileName = "bohemix-tracker.pak";
    private const string GameLogEventPrefix = "[BohemiXTrackerEvent] ";

    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;

    public TrackerModHealthService(IApplicationPathService applicationPathService, ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<TrackerModHealthService>();
    }

    public Task<TrackerModHealth> CheckAsync(DiscoveredGame? game, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paths = applicationPathService.GetPaths();
        var localPackage = Path.Combine(paths.ModsDirectory, ModId);
        var localScript = Path.Combine(localPackage, "Data", "Scripts", "BohemiX", "bohemix_tracker.lua");
        var localPak = Path.Combine(localPackage, "Data", PakFileName);
        var isLocalPrepared = File.Exists(localScript) &&
            File.Exists(localPak) &&
            File.Exists(Path.Combine(localPackage, "mod.manifest"));

        var bridgeExists = File.Exists(paths.TrackerBridgeEventsPath);
        var bridgeHasEvents = bridgeExists && HasContent(paths.TrackerBridgeEventsPath);
        DateTimeOffset? bridgeLastWriteUtc = bridgeHasEvents
            ? File.GetLastWriteTimeUtc(paths.TrackerBridgeEventsPath)
            : null;

        var isInstalled = false;
        var isListed = false;
        var gameLogHasEvents = false;
        DateTimeOffset? gameLogLastWriteUtc = null;

        if (game is { IsVerified: true } && Directory.Exists(game.InstallPath))
        {
            var gameModDirectory = Path.Combine(game.InstallPath, "Mods", ModId);
            var installedScript = Path.Combine(gameModDirectory, "Data", "Scripts", "BohemiX", "bohemix_tracker.lua");
            var installedPak = Path.Combine(gameModDirectory, "Data", PakFileName);
            isInstalled = File.Exists(installedScript) &&
                File.Exists(installedPak) &&
                File.Exists(Path.Combine(gameModDirectory, "mod.manifest")) &&
                FilesMatch(localScript, installedScript) &&
                FilesMatch(localPak, installedPak);

            var modOrderPath = Path.Combine(game.InstallPath, "Mods", "mod_order.txt");
            if (File.Exists(modOrderPath))
            {
                try
                {
                    isListed = File.ReadLines(modOrderPath)
                        .Any(line => string.Equals(line.Trim(), ModId, StringComparison.OrdinalIgnoreCase));
                }
                catch (IOException ex)
                {
                    logger.Warning(ex, "Unable to read mod_order.txt at {ModOrderPath}", modOrderPath);
                }
            }


            var gameLogPath = Path.Combine(game.InstallPath, "kcd.log");
            gameLogHasEvents = ContainsText(gameLogPath, GameLogEventPrefix);
            if (gameLogHasEvents)
            {
                gameLogLastWriteUtc = File.GetLastWriteTimeUtc(gameLogPath);
            }
        }

        var runtimeEventsReceived = bridgeHasEvents || gameLogHasEvents;
        bridgeLastWriteUtc ??= gameLogLastWriteUtc;

        var message = (isLocalPrepared, game?.IsVerified == true, isInstalled, isListed, bridgeExists) switch
        {
            (false, _, _, _, _) => "Prepare Tracker mod package first.",
            (true, false, _, _, true) => "Tracker package ready; verify KCD2 install before game install.",
            (true, true, false, _, true) => "Tracker package ready; install to KCD2 Mods directory.",
            (true, true, true, false, true) => "Tracker installed; mod_order.txt does not list bohemix-tracker.",
            (true, true, true, true, true) when runtimeEventsReceived => "Tracker installed and game runtime events received.",
            (true, true, true, true, true) => "Tracker installed; bridge is empty until KCD2 loads the mod.",
            _ => "Tracker bridge file is missing."
        };

        return Task.FromResult(new TrackerModHealth(
            isLocalPrepared,
            isInstalled,
            isListed,
            bridgeExists,
            bridgeLastWriteUtc,
            message));
    }

    private static bool FilesMatch(string expectedPath, string actualPath)
    {
        try
        {
            var expected = new FileInfo(expectedPath);
            var actual = new FileInfo(actualPath);
            if (!expected.Exists || !actual.Exists || expected.Length != actual.Length)
            {
                return false;
            }

            return File.ReadAllBytes(expectedPath).AsSpan().SequenceEqual(File.ReadAllBytes(actualPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasContent(string path)
    {
        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsText(string path, string value)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return File.ReadLines(path).Any(line => line.Contains(value, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
