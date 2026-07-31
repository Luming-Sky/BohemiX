namespace BohemiX.Core.PlayerProfiles;

public interface IPlayerService
{
    PlayerProfile? CurrentPlayer { get; }

    IReadOnlyList<PlayerProfile> Profiles { get; }

    IReadOnlyList<IPlayerProvider> Providers { get; }

    event EventHandler<PlayerChangedEventArgs>? CurrentPlayerChanged;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<PlayerProfile> CreateAsync(
        PlayerProfileDraft draft,
        bool makeCurrent = true,
        CancellationToken cancellationToken = default);

    Task<PlayerProfile> UpdateAsync(
        Guid playerId,
        PlayerProfileUpdate update,
        CancellationToken cancellationToken = default);

    Task<PlayerProfile> SwitchAsync(Guid playerId, CancellationToken cancellationToken = default);

    Task<PlayerProfile?> DeleteAsync(Guid playerId, CancellationToken cancellationToken = default);
}
