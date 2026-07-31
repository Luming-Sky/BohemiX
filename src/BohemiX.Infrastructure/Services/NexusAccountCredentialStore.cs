using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class NexusAccountCredentialStore : INexusApiKeyProvider
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BohemiX.NexusAccount.v1");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string credentialPath;
    private readonly ILogger logger;

    public NexusAccountCredentialStore(IApplicationPathService applicationPathService, ILogger logger)
        : this(
            Path.Combine(applicationPathService.GetGlobalPaths().RootDirectory, "nexus-account.dat"),
            logger)
    {
    }

    internal NexusAccountCredentialStore(string credentialPath, ILogger logger)
    {
        this.credentialPath = credentialPath;
        this.logger = logger.ForContext<NexusAccountCredentialStore>();
    }

    public async ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var credential = await LoadAsync(cancellationToken);
        return credential?.ApiKey;
    }

    internal async Task<NexusStoredAccount?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(credentialPath))
            {
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Nexus account credentials require Windows user data protection.");
            }

            var protectedBytes = await File.ReadAllBytesAsync(credentialPath, cancellationToken);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<NexusStoredAccount>(plainBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to load the locally protected Nexus account binding.");
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task SaveAsync(
        string apiKey,
        NexusAccountBinding account,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(account);

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Nexus account credentials require Windows user data protection.");
            }

            var parent = Path.GetDirectoryName(credentialPath)
                ?? throw new InvalidOperationException("The Nexus credential path has no parent directory.");
            Directory.CreateDirectory(parent);

            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(new NexusStoredAccount(
                apiKey.Trim(),
                account.UserId,
                account.Name,
                account.IsPremium,
                account.IsSupporter));
            byte[] protectedBytes;
            try
            {
                protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }

            var temporaryPath = credentialPath + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
                File.Move(temporaryPath, credentialPath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(credentialPath))
            {
                File.Delete(credentialPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

internal sealed record NexusStoredAccount(
    string ApiKey,
    long UserId,
    string Name,
    bool IsPremium,
    bool IsSupporter)
{
    public NexusAccountBinding ToBinding() => new(UserId, Name, IsPremium, IsSupporter);
}
