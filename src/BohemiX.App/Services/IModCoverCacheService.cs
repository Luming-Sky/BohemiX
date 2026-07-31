using System.Threading;
using System.Threading.Tasks;

namespace BohemiX.App.Services;

public interface IModCoverCacheService
{
    string? GetCachedCoverPath(int modId);

    Task<string?> CacheCoverAsync(
        int modId,
        string? imageUrl,
        CancellationToken cancellationToken = default);
}
