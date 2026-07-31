namespace BohemiX.Core.PlayerProfiles;

public interface IPlayerProvider
{
    string Id { get; }

    string DisplayName { get; }

    bool RequiresNetwork { get; }

    Task<PlayerProfile> CreateAsync(
        PlayerProfileDraft draft,
        CancellationToken cancellationToken = default);

    Task<PlayerProfile> UpdateAsync(
        PlayerProfile current,
        PlayerProfileUpdate update,
        CancellationToken cancellationToken = default);
}
