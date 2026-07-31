using BohemiX.Core.PlayerProfiles;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.PlayerProfiles;
using BohemiX.Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace BohemiX.Core.Tests;

public sealed class SteamAccountBindingServiceTests
{
    [Fact]
    public async Task BindingPersistsAndSameSteamAccountRefreshesPersonaName()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = await CreateRuntimeAsync(root);
            var player = await runtime.CreatePlayerAsync("Henry");
            var first = await runtime.BindingService.BindAsync(
                player.Id,
                new SteamAccountIdentity(76561198000000001, "Old name", OwnsKcd2: true));

            var restartedService = new SqliteSteamAccountBindingService(runtime.ConnectionFactory);
            var persisted = await restartedService.GetBindingByPlayerIdAsync(player.Id);
            var refreshed = await restartedService.BindAsync(
                player.Id,
                new SteamAccountIdentity(first.SteamId, "New name", OwnsKcd2: true));

            Assert.NotNull(persisted);
            Assert.Equal(first.SteamId, persisted.SteamId);
            Assert.Equal("Old name", persisted.PersonaName);
            Assert.Equal(first.BoundAt, persisted.BoundAt);
            Assert.Equal("New name", refreshed.PersonaName);
            Assert.Equal(first.BoundAt, refreshed.BoundAt);
            Assert.True(refreshed.LastValidatedAt >= first.LastValidatedAt);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task SteamAccountCannotBeBoundToTwoOfflinePlayers()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = await CreateRuntimeAsync(root);
            var firstPlayer = await runtime.CreatePlayerAsync("Henry");
            var secondPlayer = await runtime.CreatePlayerAsync("Theresa");
            var identity = new SteamAccountIdentity(76561198000000002, "Steam Henry", OwnsKcd2: true);
            await runtime.BindingService.BindAsync(firstPlayer.Id, identity);

            var exception = await Assert.ThrowsAsync<SteamAccountBindingException>(
                () => runtime.BindingService.BindAsync(secondPlayer.Id, identity));

            Assert.Equal(
                SteamAccountBindingFailureKind.SteamAccountAlreadyBoundToDifferentPlayer,
                exception.FailureKind);
            Assert.Null(await runtime.BindingService.GetBindingByPlayerIdAsync(secondPlayer.Id));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task OfflinePlayerCannotBeSilentlyReboundToDifferentSteamAccount()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = await CreateRuntimeAsync(root);
            var player = await runtime.CreatePlayerAsync("Henry");
            await runtime.BindingService.BindAsync(
                player.Id,
                new SteamAccountIdentity(76561198000000003, "First Steam", OwnsKcd2: true));

            var exception = await Assert.ThrowsAsync<SteamAccountBindingException>(
                () => runtime.BindingService.BindAsync(
                    player.Id,
                    new SteamAccountIdentity(76561198000000004, "Second Steam", OwnsKcd2: true)));

            Assert.Equal(
                SteamAccountBindingFailureKind.PlayerAlreadyBoundToDifferentSteamAccount,
                exception.FailureKind);
            Assert.Equal(
                76561198000000003UL,
                (await runtime.BindingService.GetBindingByPlayerIdAsync(player.Id))?.SteamId);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task DeletingOfflinePlayerRemovesSteamBinding()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = await CreateRuntimeAsync(root);
            var player = await runtime.CreatePlayerAsync("Henry");
            var identity = new SteamAccountIdentity(76561198000000005, "Steam Henry", OwnsKcd2: true);
            await runtime.BindingService.BindAsync(player.Id, identity);

            await runtime.Repository.DeleteAsync(player.Id);

            Assert.Null(await runtime.BindingService.GetBindingByPlayerIdAsync(player.Id));
            Assert.Null(await runtime.BindingService.GetBindingBySteamIdAsync(identity.SteamId));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static async Task<TestRuntime> CreateRuntimeAsync(string root)
    {
        var context = new PlayerContext();
        var paths = new ApplicationPathService(context, root);
        var connectionFactory = new ProfileConnectionFactory(paths);
        var repository = new SqliteProfileRepository(connectionFactory);
        await repository.InitializeAsync();
        return new TestRuntime(
            repository,
            connectionFactory,
            new SqliteSteamAccountBindingService(connectionFactory));
    }

    private static string CreateTestRoot() =>
        Path.Combine(Path.GetTempPath(), "BohemiX.SteamBindings.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record TestRuntime(
        SqliteProfileRepository Repository,
        ProfileConnectionFactory ConnectionFactory,
        SqliteSteamAccountBindingService BindingService)
    {
        public async Task<PlayerProfile> CreatePlayerAsync(string displayName)
        {
            var now = DateTimeOffset.UtcNow;
            var profile = new PlayerProfile(
                Guid.NewGuid(),
                displayName,
                Avatar: null,
                PlayerProviderIds.Local,
                Bio: null,
                now,
                now,
                CloudId: null,
                Email: null,
                AccessToken: null,
                RefreshToken: null,
                IsCloudUser: false,
                SyncTime: null,
                Version: 1);
            await Repository.UpsertAsync(profile);
            return profile;
        }
    }
}
