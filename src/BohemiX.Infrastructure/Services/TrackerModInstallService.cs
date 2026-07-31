using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerModInstallService : ITrackerModInstallService
{
    private const string ModId = "bohemix-tracker";

    private readonly ITrackerModPackageService trackerModPackageService;
    private readonly ILogger logger;

    public TrackerModInstallService(ITrackerModPackageService trackerModPackageService, ILogger logger)
    {
        this.trackerModPackageService = trackerModPackageService;
        this.logger = logger.ForContext<TrackerModInstallService>();
    }

    public async Task<TrackerModInstallResult> InstallAsync(DiscoveredGame game, CancellationToken cancellationToken = default)
    {
        if (!game.IsVerified || string.IsNullOrWhiteSpace(game.InstallPath) || !Directory.Exists(game.InstallPath))
        {
            return new TrackerModInstallResult(false, "KCD2 installation is not verified.", null);
        }

        var package = await trackerModPackageService.PrepareAsync(cancellationToken);
        var gameRoot = Path.GetFullPath(game.InstallPath);
        var modsRoot = Path.GetFullPath(Path.Combine(gameRoot, "Mods"));
        var destination = Path.GetFullPath(Path.Combine(modsRoot, ModId));

        if (!destination.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
        {
            logger.Error("Refused to install Tracker mod outside the game root: {Destination}", destination);
            return new TrackerModInstallResult(false, "Refused to install outside the verified game directory.", null);
        }

        Directory.CreateDirectory(destination);
        await CopyDirectoryAsync(package.PackageDirectory, destination, cancellationToken);
        await EnsureModOrderEntryAsync(modsRoot, cancellationToken);

        logger.Information("Installed Tracker mod package to {Destination}", destination);
        return new TrackerModInstallResult(true, $"Installed Tracker mod to {destination}", destination);
    }

    private static async Task CopyDirectoryAsync(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationFile = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            await using var sourceStream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var destinationStream = File.Open(destinationFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }
    }

    private static async Task EnsureModOrderEntryAsync(string modsRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(modsRoot);
        var modOrderPath = Path.Combine(modsRoot, "mod_order.txt");
        var lines = File.Exists(modOrderPath)
            ? await File.ReadAllLinesAsync(modOrderPath, cancellationToken)
            : [];

        if (lines.Any(line => string.Equals(line.Trim(), ModId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var output = lines
            .Concat([ModId])
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToArray();

        await File.WriteAllLinesAsync(modOrderPath, output, cancellationToken);
    }
}
