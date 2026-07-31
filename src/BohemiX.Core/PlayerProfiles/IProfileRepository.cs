namespace BohemiX.Core.PlayerProfiles;

public interface IProfileRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PlayerProfile?> GetByIdAsync(Guid playerId, CancellationToken cancellationToken = default);

    Task<Guid?> GetCurrentPlayerIdAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(PlayerProfile profile, CancellationToken cancellationToken = default);

    Task SetCurrentPlayerIdAsync(Guid? playerId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid playerId, CancellationToken cancellationToken = default);
}
