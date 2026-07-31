using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IPlayerStatisticsService
{
    Task<IReadOnlyList<PlayerStatistic>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PlayerStatistic?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task UpsertAsync(PlayerStatistic statistic, CancellationToken cancellationToken = default);
}
