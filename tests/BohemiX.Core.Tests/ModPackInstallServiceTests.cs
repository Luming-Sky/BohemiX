using System.Net;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class ModPackInstallServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallerTriesDirectLinksForEveryAccountAndHonorsPhases(bool premium)
    {
        Directory.CreateDirectory(tempRoot);
        var paths = new TestPathService(tempRoot);
        var collection = new FakeCollectionService(paths);
        var nexus = new FakeNexusService();
        var account = new FakeAccountService(premium);
        var authorization = new FakeAuthorizationService();
        var downloader = new FakeDownloader();
        var packageInstaller = new FakePackageInstaller();
        var catalog = new FakeCatalogService();
        var service = new ModPackInstallService(
            collection,
            nexus,
            account,
            authorization,
            downloader,
            packageInstaller,
            catalog,
            paths,
            new LoggerConfiguration().CreateLogger());
        var reportedProgress = new List<ModPackInstallProgress>();

        var plan = await service.PrepareAsync(CreateEntry(), allowAdultContent: false);
        var result = await service.InstallAsync(
            plan.SessionId,
            [],
            new InlineProgress<ModPackInstallProgress>(reportedProgress.Add));

        Assert.Equal(ModPackInstallSessionStatus.Completed, result.Status);
        Assert.Equal(2, result.InstalledCount);
        Assert.Equal(0, authorization.CallCount);
        Assert.Equal(2, nexus.DirectLinkCalls);
        Assert.Equal(0, nexus.AuthorizedLinkCalls);
        Assert.Equal(["Phase zero", "Phase one"], packageInstaller.InstalledNames);
        Assert.Equal(["nexus-2-file-20", "nexus-1-file-10"], catalog.LastSavedLoadOrder);
        Assert.Contains(reportedProgress, value => value.DownloadProgress is { BytesDownloaded: 50, TotalBytes: 100 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerRequestsPerFileAuthorizationOnlyAfterDirectLinkIsRejected(bool premium)
    {
        Directory.CreateDirectory(tempRoot);
        var paths = new TestPathService(tempRoot);
        var nexus = new FakeNexusService(rejectDirectLinks: true);
        var authorization = new FakeAuthorizationService();
        var service = new ModPackInstallService(
            new FakeCollectionService(paths),
            nexus,
            new FakeAccountService(premium),
            authorization,
            new FakeDownloader(),
            new FakePackageInstaller(),
            new FakeCatalogService(),
            paths,
            new LoggerConfiguration().CreateLogger());

        var plan = await service.PrepareAsync(CreateEntry(), allowAdultContent: false);
        var result = await service.InstallAsync(plan.SessionId, []);

        Assert.Equal(ModPackInstallSessionStatus.Completed, result.Status);
        Assert.Equal(2, nexus.DirectLinkCalls);
        Assert.Equal(2, authorization.CallCount);
        Assert.Equal(2, nexus.AuthorizedLinkCalls);
    }

    [Fact]
    public async Task InstallerDownloadsItemsInTheSamePhaseConcurrentlyButInstallsSerially()
    {
        Directory.CreateDirectory(tempRoot);
        var paths = new TestPathService(tempRoot);
        NexusCollectionManifestItem[] items =
        [
            CreateManifestItem(1, phase: 0),
            CreateManifestItem(2, phase: 0),
            CreateManifestItem(3, phase: 0),
            CreateManifestItem(4, phase: 1)
        ];
        var downloader = new FakeDownloader(TimeSpan.FromMilliseconds(40));
        var packageInstaller = new FakePackageInstaller(TimeSpan.FromMilliseconds(10));
        var service = new ModPackInstallService(
            new FakeCollectionService(paths, items),
            new FakeNexusService(),
            new FakeAccountService(premium: true),
            new FakeAuthorizationService(),
            downloader,
            packageInstaller,
            new FakeCatalogService(),
            paths,
            new LoggerConfiguration().CreateLogger());

        var plan = await service.PrepareAsync(CreateEntry(), allowAdultContent: false);
        var result = await service.InstallAsync(plan.SessionId, []);

        Assert.Equal(ModPackInstallSessionStatus.Completed, result.Status);
        Assert.True(downloader.MaxObservedConcurrentDownloads >= 2);
        Assert.True(downloader.MaxObservedConcurrentDownloads <= 3);
        Assert.Equal(1, packageInstaller.MaxObservedConcurrentInstalls);
        Assert.Equal("Mod 4", packageInstaller.InstalledNames[^1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static ModPackCatalogEntry CreateEntry() => new(
        "test-pack",
        "Test Pack",
        "Test collection",
        ModPackPlatform.NexusCollection,
        "test-pack",
        "https://www.nexusmods.com/kingdomcomedeliverance2/collections/test-pack",
        "Curator",
        ModPackCategory.EssentialsTools,
        ["test"],
        2,
        "KCD2",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static NexusCollectionManifestItem CreateManifestItem(int modId, int phase) => new(
        $"nexus-{modId}-{modId * 10}",
        $"Mod {modId}",
        "1",
        false,
        "kingdomcomedeliverance2",
        ModPackInstallItemSource.Nexus,
        modId,
        modId * 10,
        null,
        100,
        $"mod-{modId}.zip",
        null,
        null,
        null,
        null,
        phase,
        false,
        false,
        false,
        false);

    private sealed class FakeCollectionService(
        TestPathService paths,
        IReadOnlyList<NexusCollectionManifestItem>? manifestItems = null) : INexusCollectionService
    {
        public Task<NexusCollectionRevisionInfo> GetLatestPublishedRevisionAsync(
            string slug,
            bool viewAdultContent,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusCollectionRevisionInfo(
                1,
                2,
                slug,
                "Test Pack",
                "Summary",
                "kingdomcomedeliverance2",
                3,
                "published",
                false,
                "https://api.nexusmods.com/download",
                null,
                300,
                2));

        public Task<NexusCollectionPackage> DownloadAndReadPackageAsync(
            NexusCollectionRevisionInfo revision,
            Guid sessionId,
            CancellationToken cancellationToken = default)
        {
            var sessionDirectory = Path.Combine(paths.GetPaths().CacheDirectory!, "ModPacks", "Sessions", sessionId.ToString("N"));
            Directory.CreateDirectory(sessionDirectory);
            var manifest = new NexusCollectionManifest(
                new NexusCollectionManifestInfo("Curator", "Test Pack", "", "", "kingdomcomedeliverance2", [], ""),
                manifestItems ??
                [
                    new NexusCollectionManifestItem("nexus-1-10", "Phase one", "1", false, "kingdomcomedeliverance2", ModPackInstallItemSource.Nexus, 1, 10, null, 100, "one.zip", null, null, null, null, 1, false, false, false, false),
                    new NexusCollectionManifestItem("nexus-2-20", "Phase zero", "1", false, "kingdomcomedeliverance2", ModPackInstallItemSource.Nexus, 2, 20, null, 200, "two.zip", null, null, null, null, 0, false, false, false, false)
                ],
                []);
            return Task.FromResult(new NexusCollectionPackage(
                revision,
                manifest,
                sessionDirectory,
                Path.Combine(sessionDirectory, "collection.package"),
                Path.Combine(sessionDirectory, "bundled")));
        }
    }

    private sealed class FakeNexusService(bool rejectDirectLinks = false) : INexusModService
    {
        public int DirectLinkCalls { get; private set; }
        public int AuthorizedLinkCalls { get; private set; }

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NexusModFile>>([
                new NexusModFile(modId * 10, modId, $"File {modId}", $"file-{modId}.zip", "MAIN", "1", 100, null, 0, true, true)
            ]);

        public Task<NexusDownloadLink> GetDownloadLinkAsync(string gameDomainName, int modId, int fileId, CancellationToken cancellationToken = default)
        {
            DirectLinkCalls++;
            if (rejectDirectLinks)
            {
                throw new NexusModsException("Nexus requires file authorization.", HttpStatusCode.Forbidden);
            }

            return Task.FromResult(new NexusDownloadLink(new Uri($"https://cdn.nexusmods.com/{modId}/{fileId}"), "Nexus", "Nexus"));
        }

        public Task<NexusDownloadLink> GetDownloadLinkAsync(string gameDomainName, int modId, int fileId, NexusDownloadAuthorization authorization, CancellationToken cancellationToken = default)
        {
            AuthorizedLinkCalls++;
            return Task.FromResult(new NexusDownloadLink(new Uri($"https://cdn.nexusmods.com/{modId}/{fileId}"), "Nexus", "Nexus"));
        }

        public Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModSearchResult> SearchModsAsync(NexusModSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModDetails> GetModDetailsAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NexusModFile> GetPreferredFileAsync(string gameDomainName, int modId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAccountService(bool premium) : INexusAccountService
    {
        private readonly NexusAccountBinding account = new(1, "Tester", premium, false);
        public Task<NexusAccountBinding?> GetBoundAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult<NexusAccountBinding?>(account);
        public Task<NexusAccountBinding> BindAsync(CancellationToken cancellationToken = default) => Task.FromResult(account);
        public Task<NexusAccountBinding> BindWithApiKeyAsync(string apiKey, CancellationToken cancellationToken = default) => Task.FromResult(account);
        public Task UnbindAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAuthorizationService : INexusDownloadAuthorizationService
    {
        public int CallCount { get; private set; }
        public Task<NexusDownloadAuthorizationResult> AuthorizeAsync(NexusDownloadAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new NexusDownloadAuthorizationResult(
                true,
                new NexusDownloadAuthorization("test", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())));
        }
    }

    private sealed class FakeDownloader(TimeSpan? delay = null) : IModDownloader
    {
        private readonly List<ModDownloadQueueItem> queue = [];
        private readonly object syncRoot = new();
        private int activeDownloads;
        private int maxObservedConcurrentDownloads;
        public IReadOnlyCollection<ModDownloadQueueItem> Queue
        {
            get
            {
                lock (syncRoot)
                {
                    return queue.ToArray();
                }
            }
        }

        public int MaxObservedConcurrentDownloads => Volatile.Read(ref maxObservedConcurrentDownloads);

        public async Task<ModDownloadResult> DownloadAsync(NexusModDownloadRequest request, IProgress<ModDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeDownloads);
            SetMax(ref maxObservedConcurrentDownloads, active);
            try
            {
                if (delay is { } value && value > TimeSpan.Zero)
                {
                    await Task.Delay(value, cancellationToken);
                }

                Directory.CreateDirectory(request.DestinationDirectory);
                var path = Path.Combine(request.DestinationDirectory, request.FileName);
                await File.WriteAllTextAsync(path, "package", cancellationToken);
                var queueKey = $"{request.ModId}:{request.FileId}";
                lock (syncRoot)
                {
                    queue.Add(new ModDownloadQueueItem(queueKey, request, ModDownloadStatus.Completed, null));
                }

                progress?.Report(new ModDownloadProgress(
                    queueKey,
                    request.ModId,
                    request.FileId,
                    request.FileName,
                    50,
                    100,
                    50,
                    25,
                    ModDownloadStatus.Downloading));
                return new ModDownloadResult(true, ModDownloadStatus.Completed, request, path, null);
            }
            finally
            {
                Interlocked.Decrement(ref activeDownloads);
            }
        }

        public bool PauseQueueItem(string queueKey) => false;
        public bool ResumeQueueItem(string queueKey) => false;
        public bool RetryQueueItem(string queueKey) => false;
        public bool CancelQueueItem(string queueKey) => false;
        public int PauseQueue() => 0;
        public int ResumeQueue() => 0;
        public int CancelQueue() => 0;
        public int ClearInactiveQueueItems() => 0;
        public int ClearCompletedQueueItems() => 0;
        public int ClearCanceledQueueItems() => 0;
        public Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueWithDependenciesAsync(string gameDomainName, int modId, string destinationDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFileWithDependenciesAsync(string gameDomainName, int modId, NexusModFile selectedFile, string destinationDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFilesWithDependenciesAsync(string gameDomainName, int modId, IReadOnlyCollection<NexusModFile> selectedFiles, string destinationDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModDownloadResult>> StartQueuedDownloadsAsync(IProgress<ModDownloadProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePackageInstaller(TimeSpan? delay = null) : IModPackageInstaller
    {
        public List<string> InstalledNames { get; } = [];
        private int activeInstalls;
        private int maxObservedConcurrentInstalls;
        public int MaxObservedConcurrentInstalls => Volatile.Read(ref maxObservedConcurrentInstalls);

        public async Task<ModPackageInstallResult> InstallAsync(ModPackageInstallRequest request, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeInstalls);
            SetMax(ref maxObservedConcurrentInstalls, active);
            try
            {
                if (delay is { } value && value > TimeSpan.Zero)
                {
                    await Task.Delay(value, cancellationToken);
                }

                InstalledNames.Add(request.DisplayName);
                return new ModPackageInstallResult(true, request.ModsDirectory, "Installed");
            }
            finally
            {
                Interlocked.Decrement(ref activeInstalls);
            }
        }

        public Task<ModPackageInstallResult> InstallPreparedDirectoryAsync(PreparedModPackageInstallRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModPackageInstallResult(true, request.PayloadDirectory, "Installed"));
    }

    private static void SetMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private sealed class FakeCatalogService : IModCatalogService
    {
        public IReadOnlyList<string> LastSavedLoadOrder { get; private set; } = [];
        public Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ModManifest>>([]);
        public Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default)
        {
            LastSavedLoadOrder = orderedModIds.ToArray();
            return Task.CompletedTask;
        }
        public Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveModEnabledStatesAsync(IReadOnlyDictionary<string, bool> enabledStates, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateModMetadataAsync(ModManifest mod, string displayName, string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestPathService(string root) : IApplicationPathService
    {
        public ApplicationPaths GetPaths() => new(
            root,
            Path.Combine(root, "bohemix.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "Mods"),
            Path.Combine(root, "native"),
            Path.Combine(root, "tracker"),
            Path.Combine(root, "tracker", "events.jsonl"),
            Path.Combine(root, "tracker", "rules.json"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            CacheDirectory: Path.Combine(root, "cache"));

        public GlobalApplicationPaths GetGlobalPaths() => throw new NotSupportedException();
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
