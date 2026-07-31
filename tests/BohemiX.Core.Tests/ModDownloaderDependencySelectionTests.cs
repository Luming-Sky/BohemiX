using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class ModDownloaderDependencySelectionTests
{
    [Fact]
    public async Task EnqueueFilesWithoutDependenciesQueuesOnlySelectedFiles()
    {
        using var downloader = CreateDownloader();

        var queued = await downloader.EnqueueFilesAsync(
            "kingdomcomedeliverance2",
            10,
            CreateSelectedFile(),
            Path.GetTempPath());

        var item = Assert.Single(queued);
        Assert.Equal(10, item.Request.ModId);
    }

    [Fact]
    public async Task EnqueueFilesWithDependenciesQueuesNexusRequirementsButNotExternalRequirements()
    {
        using var downloader = CreateDownloader();

        var queued = await downloader.EnqueueFilesWithDependenciesAsync(
            "kingdomcomedeliverance2",
            10,
            CreateSelectedFile(),
            Path.GetTempPath());

        Assert.Equal([11, 10], queued.Select(item => item.Request.ModId));
    }

    private static ModDownloader CreateDownloader() => new(
        new DependencyNexusService(),
        new NullCookieAuthService(),
        new EmptyModCatalogService(),
        new ModDownloaderOptions(),
        Logger.None);

    private static IReadOnlyCollection<NexusModFile> CreateSelectedFile() =>
    [new NexusModFile(100, 10, "Main", "main.zip", "MAIN", "1.0", null, null, 0, true, true)];

    private sealed class DependencyNexusService : INexusModService
    {
        public Task<NexusModDetails> GetModDetailsAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<NexusModRequirement> requirements = modId == 10
                ? [
                    new NexusModRequirement(11, "Automatic dependency", 1, false, null, null),
                    new NexusModRequirement(12, "External dependency", 1, true, null, null)
                ]
                : [];

            return Task.FromResult(new NexusModDetails(
                modId, 1, gameDomainName, $"Mod {modId}", string.Empty, string.Empty, "1.0", "Tester",
                0, 0, true, "published", requirements, []));
        }

        public Task<NexusModFile> GetPreferredFileAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusModFile(modId * 10, modId, "Main", $"mod-{modId}.zip", "MAIN", "1.0", null, null, 0, true, true));

        public Task<NexusDownloadLink> GetDownloadLinkAsync(string gameDomainName, int modId, int fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusDownloadLink(new Uri($"https://cdn.nexusmods.com/files/{modId}/{fileId}"), "Nexus", "Nexus"));

        public Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModSearchResult> SearchModsAsync(NexusModSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NullCookieAuthService : INexusCookieAuthService
    {
        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusCookieAuthProbeResult(false, null, "Unavailable", "Unavailable.", []));

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(Uri requestUri, CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusCookieAuthLease?>(null);

        public void ClearSession()
        {
        }
    }

    private sealed class EmptyModCatalogService : IModCatalogService
    {
        public Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModManifest>>([]);

        public Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveModEnabledStatesAsync(IReadOnlyDictionary<string, bool> enabledStates, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateModMetadataAsync(ModManifest mod, string displayName, string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
