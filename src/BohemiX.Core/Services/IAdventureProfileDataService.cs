using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Provides game-environment data for the dashboard without coupling it to accounts.
/// </summary>
public interface IAdventureProfileDataService
{
    Task<AdventureProfileData> LoadAsync(
        Guid? saveSlotId = null,
        CancellationToken cancellationToken = default);
}
