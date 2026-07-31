using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Loads the optional KCD2 official-news feed without affecting local launcher operations.
/// </summary>
public interface IGameNewsService
{
    Task<GameNewsFeed?> LoadCachedAsync(CancellationToken cancellationToken = default);

    Task<GameNewsFeed> RefreshAsync(CancellationToken cancellationToken = default);
}
