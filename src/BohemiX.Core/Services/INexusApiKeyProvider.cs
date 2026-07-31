namespace BohemiX.Core.Services;

public interface INexusApiKeyProvider
{
    ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default);
}
