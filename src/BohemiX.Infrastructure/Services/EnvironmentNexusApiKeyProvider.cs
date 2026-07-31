using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class EnvironmentNexusApiKeyProvider : INexusApiKeyStore
{
    private string? sessionApiKey;

    private static readonly string[] EnvironmentVariableNames =
    [
        "NEXUS_MODS_API_KEY",
        "NEXUS_API_KEY",
        "BOHEMIX_NEXUS_API_KEY"
    ];

    public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(sessionApiKey))
        {
            return ValueTask.FromResult<string?>(sessionApiKey);
        }

        foreach (var variableName in EnvironmentVariableNames)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return ValueTask.FromResult<string?>(value.Trim());
            }
        }

        return ValueTask.FromResult<string?>(null);
    }

    public void SetApiKey(string? apiKey)
    {
        sessionApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }
}
