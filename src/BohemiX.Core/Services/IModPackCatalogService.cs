using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IModPackCatalogService
{
    Task<ModPackCatalogResult> GetCatalogAsync(
        bool refreshRemote = true,
        CancellationToken cancellationToken = default);
}
