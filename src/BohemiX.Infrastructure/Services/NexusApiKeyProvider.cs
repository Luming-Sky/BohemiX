using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class NexusApiKeyProvider(
    NexusAccountCredentialStore accountCredentialStore,
    EnvironmentNexusApiKeyProvider environmentProvider) : INexusApiKeyProvider
{
    public async ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var boundAccountKey = await accountCredentialStore.GetApiKeyAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(boundAccountKey)
            ? boundAccountKey
            : await environmentProvider.GetApiKeyAsync(cancellationToken);
    }
}
