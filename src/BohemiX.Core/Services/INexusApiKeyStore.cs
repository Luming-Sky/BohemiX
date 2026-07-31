namespace BohemiX.Core.Services;

public interface INexusApiKeyStore : INexusApiKeyProvider
{
    void SetApiKey(string? apiKey);
}
