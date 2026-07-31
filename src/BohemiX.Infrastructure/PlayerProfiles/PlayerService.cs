using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.PlayerProfiles;

public sealed class PlayerService : IPlayerService
{
    private readonly IProfileRepository repository;
    private readonly IReadOnlyDictionary<string, IPlayerProvider> providers;
    private readonly IPlayerContextAccessor playerContext;
    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<PlayerProfile> profiles = [];
    private bool initialized;

    public PlayerService(
        IProfileRepository repository,
        IEnumerable<IPlayerProvider> providers,
        IPlayerContextAccessor playerContext,
        IApplicationPathService applicationPathService,
        ILogger logger)
    {
        this.repository = repository;
        this.providers = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        this.playerContext = playerContext;
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<PlayerService>();
        playerContext.CurrentPlayerChanged += (_, args) => CurrentPlayerChanged?.Invoke(this, args);
    }

    public PlayerProfile? CurrentPlayer => playerContext.CurrentPlayer;

    public IReadOnlyList<PlayerProfile> Profiles => profiles;

    public IReadOnlyList<IPlayerProvider> Providers => providers.Values
        .OrderBy(provider => provider.RequiresNetwork)
        .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public event EventHandler<PlayerChangedEventArgs>? CurrentPlayerChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            var globalPaths = applicationPathService.GetGlobalPaths();
            Directory.CreateDirectory(globalPaths.RootDirectory);
            Directory.CreateDirectory(globalPaths.AccountsDirectory);
            Directory.CreateDirectory(globalPaths.DeletedAccountsDirectory);
            Directory.CreateDirectory(globalPaths.LogsDirectory);
            await repository.InitializeAsync(cancellationToken);
            await ReloadProfilesAsync(cancellationToken);

            var currentId = await repository.GetCurrentPlayerIdAsync(cancellationToken);
            var current = currentId is null
                ? null
                : profiles.FirstOrDefault(profile => profile.Id == currentId.Value);
            current ??= profiles.FirstOrDefault();

            if (current is not null)
            {
                current = current with { LastLoginTime = DateTimeOffset.UtcNow };
                await repository.UpsertAsync(current, cancellationToken);
                await repository.SetCurrentPlayerIdAsync(current.Id, cancellationToken);
                EnsureAccountDirectories(current.Id);
                await ReloadProfilesAsync(cancellationToken);
                current = profiles.First(profile => profile.Id == current.Id);
            }

            playerContext.SetCurrentPlayer(current, PlayerChangeReason.Initialized);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlayerProfile> CreateAsync(
        PlayerProfileDraft draft,
        bool makeCurrent = true,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var provider = GetProvider(draft.Provider);
            var profile = await provider.CreateAsync(draft, cancellationToken);
            EnsureAccountDirectories(profile.Id);
            profile = await ImportAvatarAsync(profile, draft.Avatar, cancellationToken);

            await repository.UpsertAsync(profile, cancellationToken);
            await ReloadProfilesAsync(cancellationToken);

            if (makeCurrent || CurrentPlayer is null)
            {
                await repository.SetCurrentPlayerIdAsync(profile.Id, cancellationToken);
                playerContext.SetCurrentPlayer(profile, PlayerChangeReason.Created);
            }

            logger.Information("Created player profile {PlayerId} with provider {Provider}", profile.Id, profile.Provider);
            return profile;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlayerProfile> UpdateAsync(
        Guid playerId,
        PlayerProfileUpdate update,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = profiles.FirstOrDefault(profile => profile.Id == playerId)
                ?? throw new KeyNotFoundException($"Player profile '{playerId}' was not found.");
            var provider = GetProvider(current.Provider);
            var updated = await provider.UpdateAsync(current, update, cancellationToken);
            updated = await ImportAvatarAsync(updated, update.Avatar, cancellationToken);
            await repository.UpsertAsync(updated, cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            updated = profiles.First(profile => profile.Id == playerId);

            if (CurrentPlayer?.Id == playerId)
            {
                playerContext.SetCurrentPlayer(updated, PlayerChangeReason.Updated);
            }

            logger.Information("Updated player profile {PlayerId}", playerId);
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlayerProfile> SwitchAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var selected = profiles.FirstOrDefault(profile => profile.Id == playerId)
                ?? throw new KeyNotFoundException($"Player profile '{playerId}' was not found.");
            selected = selected with { LastLoginTime = DateTimeOffset.UtcNow };
            EnsureAccountDirectories(selected.Id);
            await repository.UpsertAsync(selected, cancellationToken);
            await repository.SetCurrentPlayerIdAsync(selected.Id, cancellationToken);
            await ReloadProfilesAsync(cancellationToken);
            selected = profiles.First(profile => profile.Id == playerId);
            playerContext.SetCurrentPlayer(selected, PlayerChangeReason.Switched);
            logger.Information("Switched current player to {PlayerId}", playerId);
            return selected;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PlayerProfile?> DeleteAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var deleting = profiles.FirstOrDefault(profile => profile.Id == playerId)
                ?? throw new KeyNotFoundException($"Player profile '{playerId}' was not found.");
            var wasCurrent = CurrentPlayer?.Id == playerId;
            await repository.DeleteAsync(playerId, cancellationToken);
            await ReloadProfilesAsync(cancellationToken);

            PlayerProfile? replacement = CurrentPlayer;
            if (wasCurrent)
            {
                replacement = profiles.FirstOrDefault();
                if (replacement is not null)
                {
                    replacement = replacement with { LastLoginTime = DateTimeOffset.UtcNow };
                    await repository.UpsertAsync(replacement, cancellationToken);
                    await repository.SetCurrentPlayerIdAsync(replacement.Id, cancellationToken);
                    await ReloadProfilesAsync(cancellationToken);
                    replacement = profiles.First(profile => profile.Id == replacement.Id);
                }
                else
                {
                    await repository.SetCurrentPlayerIdAsync(null, cancellationToken);
                }

                playerContext.SetCurrentPlayer(replacement, PlayerChangeReason.Deleted);
            }

            ArchiveAccountDirectory(deleting.Id);
            logger.Information("Deleted player profile {PlayerId}; account data was archived locally", playerId);
            return replacement;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private IPlayerProvider GetProvider(string providerId)
    {
        if (!providers.TryGetValue(providerId?.Trim() ?? string.Empty, out var provider))
        {
            throw new NotSupportedException($"Player provider '{providerId}' is not registered.");
        }

        return provider;
    }

    private async Task ReloadProfilesAsync(CancellationToken cancellationToken)
    {
        profiles = await repository.GetAllAsync(cancellationToken);
    }

    private void EnsureAccountDirectories(Guid accountId)
    {
        var paths = applicationPathService.GetAccountPaths(accountId);
        Directory.CreateDirectory(paths.RootDirectory);
        Directory.CreateDirectory(paths.SettingsDirectory);
        Directory.CreateDirectory(paths.CacheDirectory);
        Directory.CreateDirectory(paths.AvatarDirectory);
    }

    private async Task<PlayerProfile> ImportAvatarAsync(
        PlayerProfile profile,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return profile with { Avatar = null };
        }

        var accountPaths = applicationPathService.GetAccountPaths(profile.Id);
        Directory.CreateDirectory(accountPaths.AvatarDirectory);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp" and not ".bmp")
        {
            throw new InvalidDataException("Avatar must be a PNG, JPG, WEBP, or BMP image.");
        }

        var destination = Path.Combine(accountPaths.AvatarDirectory, $"avatar{extension}");
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            await using var input = File.OpenRead(sourcePath);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, cancellationToken);
        }

        return profile with { Avatar = destination };
    }

    private void ArchiveAccountDirectory(Guid accountId)
    {
        var global = applicationPathService.GetGlobalPaths();
        var source = applicationPathService.GetAccountPaths(accountId).RootDirectory;
        if (!Directory.Exists(source))
        {
            return;
        }

        try
        {
            var accountsRoot = Path.GetFullPath(global.AccountsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedSource = Path.GetFullPath(source);
            if (!resolvedSource.StartsWith(accountsRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Account directory is outside the configured Accounts root.");
            }

            Directory.CreateDirectory(global.DeletedAccountsDirectory);
            var destination = Path.Combine(
                global.DeletedAccountsDirectory,
                $"{accountId:N}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            Directory.Move(resolvedSource, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to archive account directory for {PlayerId}", accountId);
        }
    }
}
