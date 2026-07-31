using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class GameDiscoveryServiceTests
{
    [Fact]
    public async Task FastDiscovery_FindsSteamManifestWithoutDeepDirectoryScan()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var steamRoot = Path.Combine(root, "Steam");
            var executablePath = CreateExecutable(
                steamRoot,
                "steamapps",
                "common",
                "Kingdom Come Deliverance II",
                "Bin",
                "Win64MasterMasterSteamPGO");
            Directory.CreateDirectory(Path.Combine(steamRoot, "steamapps"));
            await File.WriteAllTextAsync(
                Path.Combine(steamRoot, "steamapps", "appmanifest_1771300.acf"),
                "\"AppState\"\n{\n\t\"installdir\"\t\"Kingdom Come Deliverance II\"\n}");

            var customExecutable = CreateExecutable(
                root,
                "DeepOnly",
                "CustomFolder",
                "KCD2",
                "Bin",
                "Win64MasterMasterSteamPGO");
            var source = new TestPathSource([steamRoot], [], [], [root]);
            var service = new GameDiscoveryService(CreateLogger(), new InMemoryGameInstallationStore(), source);

            var games = await service.DiscoverInstalledGamesAsync(GameDiscoverySearchMode.Fast);

            var game = Assert.Single(games);
            Assert.Equal(executablePath, game.ExecutablePath);
            Assert.DoesNotContain(games, candidate => candidate.ExecutablePath == customExecutable);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task DeepDiscovery_FindsCustomInstallationOutsideKnownRoots()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var executablePath = CreateExecutable(
                root,
                "DeepOnly",
                "CustomFolder",
                "KCD2",
                "Bin",
                "Win64MasterMasterSteamPGO");
            var service = new GameDiscoveryService(
                CreateLogger(),
                new InMemoryGameInstallationStore(),
                new TestPathSource([], [], [], [root]));

            var games = await service.DiscoverInstalledGamesAsync(GameDiscoverySearchMode.Deep);

            var game = Assert.Single(games);
            Assert.True(game.IsVerified);
            Assert.Equal(executablePath, game.ExecutablePath);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Discovery_HonorsCancellationBeforeScanning()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var source = new TestPathSource([], [], [], [root]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new GameDiscoveryService(CreateLogger(), new InMemoryGameInstallationStore(), source);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.DiscoverInstalledGamesAsync(GameDiscoverySearchMode.Deep, cancellation.Token));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static string CreateExecutable(string root, params string[] pathSegments)
    {
        var directory = Path.Combine([root, .. pathSegments]);
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, "KingdomCome.exe");
        File.WriteAllText(executablePath, "test");
        return executablePath;
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ILogger CreateLogger() => new LoggerConfiguration().CreateLogger();

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestPathSource(
        IReadOnlyList<string> steamRoots,
        IReadOnlyList<string> epicInstallRoots,
        IReadOnlyList<string> commonGameInstallRoots,
        IReadOnlyList<string> driveRoots) : IGameDiscoveryPathSource
    {
        public string EpicManifestRoot => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        public IEnumerable<string> GetSteamRoots() => steamRoots;

        public IEnumerable<string> GetEpicInstallRoots() => epicInstallRoots;

        public IEnumerable<string> GetCommonGameInstallRoots() => commonGameInstallRoots;

        public IEnumerable<string> GetDriveRoots() => driveRoots;
    }

    private sealed class InMemoryGameInstallationStore : IGameInstallationStore
    {
        private IReadOnlyList<DiscoveredGame> games = [];

        public Task<IReadOnlyList<DiscoveredGame>> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(games);
        }

        public Task SaveAsync(IReadOnlyList<DiscoveredGame> games, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.games = games;
            return Task.CompletedTask;
        }
    }
}
