#pragma warning disable CS0618 // Compatibility cases intentionally exercise the legacy profile constructor.

using BohemiX.App.PlayerProfiles;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using Serilog.Core;

namespace BohemiX.App.Tests;

public sealed class PlayerProfileManagerViewModelTests
{
    [Fact]
    public async Task AccountEntryCommandsRequestThePlayerProfilesSettingsPage()
    {
        var player = CreatePlayer("Henry");
        using var viewModel = new PlayerProfileManagerViewModel(
            new RecordingPlayerService(player),
            new EmptyGameInstallationDetector(),
            new EmptyPathPickerService(),
            Logger.None);
        var requestCount = 0;
        viewModel.SettingsRequested += (_, _) => requestCount++;

        await viewModel.InitializeAsync();
        viewModel.OpenSettingsCommand.Execute(null);
        viewModel.OpenInformationCommand.Execute(null);

        Assert.Equal(2, requestCount);
        Assert.False(viewModel.IsInformationOpen);
    }

    [Fact]
    public async Task EditingExistingProfileAutoSavesAfterDebounce()
    {
        var player = CreatePlayer("Henry");
        var playerService = new RecordingPlayerService(player);
        using var viewModel = new PlayerProfileManagerViewModel(
            playerService,
            new EmptyGameInstallationDetector(),
            new EmptyPathPickerService(),
            Logger.None);

        await viewModel.InitializeAsync();
        viewModel.OpenEditProfileCommand.Execute(null);
        viewModel.EditorDisplayName = "Henry of Skalitz";

        var update = await playerService.UpdateReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitForAsync(
            () => viewModel.EditorSaveStatus.Contains("已保存", StringComparison.Ordinal),
            TimeSpan.FromSeconds(1));

        Assert.Equal("Henry of Skalitz", update.DisplayName);
        Assert.Equal(player.Id, playerService.CurrentPlayer?.Id);
        Assert.Contains("已保存", viewModel.EditorSaveStatus, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    private static PlayerProfile CreatePlayer(string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlayerProfile(
            Guid.NewGuid(),
            displayName,
            null,
            PlayerPlatformIds.Steam,
            PlayerProviderIds.Local,
            null,
            now,
            now,
            null,
            null,
            null,
            null,
            false,
            null,
            1);
    }

    private sealed class RecordingPlayerService : IPlayerService
    {
        private readonly IPlayerProvider provider = new StubPlayerProvider();

        public RecordingPlayerService(PlayerProfile player)
        {
            CurrentPlayer = player;
            Profiles = [player];
        }

        public TaskCompletionSource<PlayerProfileUpdate> UpdateReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PlayerProfile? CurrentPlayer { get; private set; }

        public IReadOnlyList<PlayerProfile> Profiles { get; private set; }

        public IReadOnlyList<IPlayerProvider> Providers => [provider];

        public event EventHandler<PlayerChangedEventArgs>? CurrentPlayerChanged;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PlayerProfile> CreateAsync(
            PlayerProfileDraft draft,
            bool makeCurrent = true,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlayerProfile> UpdateAsync(
            Guid playerId,
            PlayerProfileUpdate update,
            CancellationToken cancellationToken = default)
        {
            var previous = CurrentPlayer ?? throw new InvalidOperationException();
            CurrentPlayer = previous with
            {
                DisplayName = update.DisplayName,
                Avatar = update.Avatar,
                Platform = update.Platform,
                GameInstallPath = update.GameInstallPath
            };
            Profiles = [CurrentPlayer];
            UpdateReceived.TrySetResult(update);
            CurrentPlayerChanged?.Invoke(
                this,
                new PlayerChangedEventArgs(previous, CurrentPlayer, PlayerChangeReason.Updated));
            return Task.FromResult(CurrentPlayer);
        }

        public Task<PlayerProfile> SwitchAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlayerProfile?> DeleteAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubPlayerProvider : IPlayerProvider
    {
        public string Id => PlayerProviderIds.Local;

        public string DisplayName => "Local Account";

        public bool RequiresNetwork => false;

        public Task<PlayerProfile> CreateAsync(
            PlayerProfileDraft draft,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlayerProfile> UpdateAsync(
            PlayerProfile current,
            PlayerProfileUpdate update,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyGameInstallationDetector : ILocalGameInstallationDetector
    {
        public Task<IReadOnlyList<DiscoveredGame>> DetectLocalInstallationsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredGame>>([]);
    }

    private sealed class EmptyPathPickerService : IGamePathPickerService
    {
        public Task<string?> PickGameExecutableAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickGameDirectoryAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSaveDirectoryAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickModPackageAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickModPackAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickAvatarAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickAppearanceMediaAsync(bool animated) => Task.FromResult<string?>(null);
    }
}

#pragma warning restore CS0618
