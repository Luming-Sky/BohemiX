using System.Security.Cryptography;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ApplicationUpdateValidationService : IApplicationUpdateValidationService
{
    private static readonly string[] RequiredExtensions =
    [
        ".exe",
        ".dll",
        ".deps.json",
        ".runtimeconfig.json"
    ];

    public async Task<ApplicationUpdateValidationResult> ValidateAsync(
        string applicationDirectory,
        string applicationName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        if (!string.Equals(applicationName, Path.GetFileName(applicationName), StringComparison.Ordinal))
        {
            throw new ArgumentException("The application name must not contain a path.", nameof(applicationName));
        }

        var root = Path.GetFullPath(applicationDirectory);
        var missingFiles = RequiredExtensions
            .Select(extension => $"{applicationName}{extension}")
            .Where(fileName => !File.Exists(Path.Combine(root, fileName)))
            .ToArray();

        if (missingFiles.Length > 0)
        {
            return new ApplicationUpdateValidationResult(false, null, missingFiles);
        }

        var assemblyPath = Path.Combine(root, $"{applicationName}.dll");
        await using var assemblyStream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(assemblyStream, cancellationToken);

        return new ApplicationUpdateValidationResult(
            true,
            Convert.ToHexString(hash),
            Array.Empty<string>());
    }
}
