using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class ModPackCatalogService : IModPackCatalogService
{
    private const string EmbeddedResourceName = "BohemiX.Infrastructure.Data.mod-pack-catalog.json";
    private const string CacheDirectoryName = "mod-pack-catalog";
    private const string CatalogFileName = "catalog.json";
    private const string SignatureFileName = "catalog.json.sig";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IApplicationPathService applicationPathService;
    private readonly ModPackCatalogOptions options;
    private readonly HttpClient httpClient;
    private readonly ILogger logger;

    public ModPackCatalogService(
        IApplicationPathService applicationPathService,
        ModPackCatalogOptions options,
        HttpClient httpClient,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.options = options;
        this.httpClient = httpClient;
        this.logger = logger.ForContext<ModPackCatalogService>();
    }

    public async Task<ModPackCatalogResult> GetCatalogAsync(
        bool refreshRemote = true,
        CancellationToken cancellationToken = default)
    {
        var embedded = await LoadEmbeddedAsync(cancellationToken).ConfigureAwait(false);
        string? warning = null;

        var shouldRefreshRemote = refreshRemote && !string.IsNullOrWhiteSpace(options.RemoteCatalogUri);
        if (shouldRefreshRemote)
        {
            try
            {
                var remote = await LoadRemoteAsync(cancellationToken).ConfigureAwait(false);
                await SaveLastKnownGoodAsync(remote.CatalogBytes, remote.SignatureBytes, cancellationToken).ConfigureAwait(false);
                return new ModPackCatalogResult(FilterVisible(remote.Document), ModPackCatalogSource.Remote);
            }
            catch (Exception ex) when (ex is HttpRequestException
                                       or IOException
                                       or InvalidDataException
                                       or JsonException
                                       or CryptographicException
                                       or UnauthorizedAccessException)
            {
                warning = ex.Message;
                logger.Warning(ex, "Unable to refresh the signed mod-pack catalog");
            }

            try
            {
                var cached = await LoadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    return new ModPackCatalogResult(FilterVisible(cached), ModPackCatalogSource.LastKnownGood, warning);
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or InvalidDataException
                                       or JsonException
                                       or CryptographicException
                                       or UnauthorizedAccessException)
            {
                warning = warning is null ? ex.Message : $"{warning} {ex.Message}";
                logger.Warning(ex, "Unable to load the last-known-good mod-pack catalog");
            }
        }

        return new ModPackCatalogResult(FilterVisible(embedded), ModPackCatalogSource.Embedded, warning);
    }

    internal static ModPackCatalogDocument DeserializeAndValidate(ReadOnlySpan<byte> bytes)
    {
        var document = JsonSerializer.Deserialize<ModPackCatalogDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Mod-pack catalog JSON is empty.");
        var errors = ModPackCatalogValidator.Validate(document);
        if (errors.Count > 0)
        {
            throw new InvalidDataException(string.Join(" ", errors));
        }

        return document;
    }

    internal static bool VerifySignature(
        ReadOnlySpan<byte> catalogBytes,
        ReadOnlySpan<byte> signatureBytes,
        string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.VerifyData(catalogBytes, signatureBytes, HashAlgorithmName.SHA256);
    }

    private async Task<ModPackCatalogDocument> LoadEmbeddedAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(ModPackCatalogService).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidDataException($"Embedded mod-pack catalog '{EmbeddedResourceName}' was not found.");
        var bytes = await ReadLimitedAsync(stream, options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        return DeserializeAndValidate(bytes);
    }

    private async Task<(ModPackCatalogDocument Document, byte[] CatalogBytes, byte[] SignatureBytes)> LoadRemoteAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PublicKeyPem))
        {
            throw new CryptographicException("Remote catalog trust key is not configured.");
        }

        var catalogUri = RequireHttpsUri(options.RemoteCatalogUri, "catalog");
        var signatureUri = RequireHttpsUri(
            string.IsNullOrWhiteSpace(options.RemoteSignatureUri)
                ? options.RemoteCatalogUri + ".sig"
                : options.RemoteSignatureUri,
            "signature");

        var catalogBytes = await DownloadAsync(catalogUri, cancellationToken).ConfigureAwait(false);
        var signaturePayload = await DownloadAsync(signatureUri, cancellationToken).ConfigureAwait(false);
        var signatureBytes = DecodeSignature(signaturePayload);
        if (!VerifySignature(catalogBytes, signatureBytes, options.PublicKeyPem))
        {
            throw new CryptographicException("Remote mod-pack catalog signature is invalid.");
        }

        return (DeserializeAndValidate(catalogBytes), catalogBytes, signatureBytes);
    }

    private async Task<ModPackCatalogDocument?> LoadLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.PublicKeyPem))
        {
            return null;
        }

        var (catalogPath, signaturePath) = GetCachePaths();
        if (!File.Exists(catalogPath) || !File.Exists(signaturePath))
        {
            return null;
        }

        var catalogBytes = await ReadFileLimitedAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        var signaturePayload = await ReadFileLimitedAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        var signatureBytes = DecodeSignature(signaturePayload);
        if (!VerifySignature(catalogBytes, signatureBytes, options.PublicKeyPem))
        {
            throw new CryptographicException("Cached mod-pack catalog signature is invalid.");
        }

        return DeserializeAndValidate(catalogBytes);
    }

    private async Task SaveLastKnownGoodAsync(
        byte[] catalogBytes,
        byte[] signatureBytes,
        CancellationToken cancellationToken)
    {
        var (catalogPath, signaturePath) = GetCachePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        await WriteAtomicallyAsync(catalogPath, catalogBytes, cancellationToken).ConfigureAwait(false);
        await WriteAtomicallyAsync(
            signaturePath,
            Encoding.ASCII.GetBytes(Convert.ToBase64String(signatureBytes)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException($"Redirects are not allowed for the mod-pack catalog ({(int)response.StatusCode}).");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
        {
            throw new InvalidDataException("Mod-pack catalog response exceeds the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ReadLimitedAsync(stream, options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadFileLimitedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadLimitedAsync(stream, options.MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Mod-pack catalog response exceeds the size limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static byte[] DecodeSignature(byte[] payload)
    {
        var text = Encoding.ASCII.GetString(payload).Trim();
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return payload;
        }
    }

    private static Uri RequireHttpsUri(string? value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Port != 443)
        {
            throw new InvalidDataException($"Remote {label} URI must use HTTPS.");
        }

        return uri;
    }

    private static IReadOnlyList<ModPackCatalogEntry> FilterVisible(ModPackCatalogDocument document) =>
        document.Entries
            .Where(entry => entry.IsActive && entry.IsPublic && entry.RightsBasis == ModPackRightsBasis.OfficialPlatform)
            .ToArray();

    private (string CatalogPath, string SignaturePath) GetCachePaths()
    {
        var paths = applicationPathService.GetPaths();
        var root = paths.CacheDirectory ?? Path.Combine(paths.DataDirectory, "cache");
        var directory = Path.Combine(root, CacheDirectoryName);
        return (Path.Combine(directory, CatalogFileName), Path.Combine(directory, SignatureFileName));
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
