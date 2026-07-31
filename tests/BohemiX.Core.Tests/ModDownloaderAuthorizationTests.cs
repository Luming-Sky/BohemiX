using System.Net;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class ModDownloaderAuthorizationTests
{
    [Fact]
    public async Task EnqueueSelectedFileRequestsBrowserAuthorizationAfterDirectLinkIsRejected()
    {
        var nexus = new AuthorizationRequiredNexusService();
        var authorization = new CapturingAuthorizationService();
        using var downloader = new ModDownloader(
            nexus,
            new NullCookieAuthService(),
            new EmptyModCatalogService(),
            new ModDownloaderOptions(),
            Logger.None,
            null,
            new HttpClientHandler { AllowAutoRedirect = false },
            authorization);

        var selectedFile = new NexusModFile(
            34,
            12,
            "Main file",
            "main-file.zip",
            "MAIN FILES",
            "1.0",
            null,
            null,
            0,
            true,
            true);

        var queued = await downloader.EnqueueFileWithDependenciesAsync(
            "kingdomcomedeliverance2",
            12,
            selectedFile,
            Path.GetTempPath());

        var item = Assert.Single(queued);
        Assert.Equal(1, nexus.DirectLinkCalls);
        Assert.Equal(1, nexus.AuthorizedLinkCalls);
        Assert.Equal(1, authorization.CallCount);
        Assert.Equal(12, authorization.Request?.ModId);
        Assert.Equal(34, authorization.Request?.FileId);
        Assert.Equal(
            new Uri("https://www.nexusmods.com/kingdomcomedeliverance2/mods/12?tab=files&file_id=34"),
            authorization.Request?.OfficialFilePageUri);
        Assert.Equal("https://cdn.nexusmods.com/files/12/34", item.Request.DownloadUri.AbsoluteUri);
    }

    private sealed class AuthorizationRequiredNexusService : INexusModService
    {
        public int DirectLinkCalls { get; private set; }
        public int AuthorizedLinkCalls { get; private set; }

        public Task<NexusModDetails> GetModDetailsAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusModDetails(
                modId,
                5851,
                gameDomainName,
                "Authorization Test Mod",
                string.Empty,
                string.Empty,
                "1.0",
                "Tester",
                0,
                0,
                true,
                "published",
                [],
                []));

        public Task<NexusDownloadLink> GetDownloadLinkAsync(string gameDomainName, int modId, int fileId, CancellationToken cancellationToken = default)
        {
            DirectLinkCalls++;
            throw new NexusModsException("Nexus requires file authorization.", HttpStatusCode.Forbidden);
        }

        public Task<NexusDownloadLink> GetDownloadLinkAsync(string gameDomainName, int modId, int fileId, NexusDownloadAuthorization authorization, CancellationToken cancellationToken = default)
        {
            AuthorizedLinkCalls++;
            return Task.FromResult(new NexusDownloadLink(new Uri($"https://cdn.nexusmods.com/files/{modId}/{fileId}"), "Nexus", "Nexus"));
        }

        public Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModSearchResult> SearchModsAsync(NexusModSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModFile> GetPreferredFileAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CapturingAuthorizationService : INexusDownloadAuthorizationService
    {
        public int CallCount { get; private set; }
        public NexusDownloadAuthorizationRequest? Request { get; private set; }

        public Task<NexusDownloadAuthorizationResult> AuthorizeAsync(NexusDownloadAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(new NexusDownloadAuthorizationResult(
                true,
                new NexusDownloadAuthorization("test", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())));
        }
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
