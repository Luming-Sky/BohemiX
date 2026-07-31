using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface INexusAccountService
{
    Task<NexusAccountBinding?> GetBoundAccountAsync(CancellationToken cancellationToken = default);

    Task<NexusAccountBinding> BindAsync(CancellationToken cancellationToken = default);

    Task<NexusAccountBinding> BindWithApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    Task UnbindAsync(CancellationToken cancellationToken = default);
}
