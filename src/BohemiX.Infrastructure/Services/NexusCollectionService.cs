using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace BohemiX.Infrastructure.Services;

public sealed class NexusCollectionService : INexusCollectionService, IDisposable
{
    internal const int MaximumGraphQlResponseBytes = 2 * 1024 * 1024;
    internal const int MaximumManifestBytes = 4 * 1024 * 1024;
    internal const long MaximumCollectionArchiveBytes = 512L * 1024 * 1024;
    internal const long MaximumBundledExtractedBytes = 1024L * 1024 * 1024;
    internal const int MaximumManifestItems = 5000;
    private const string GameDomainName = "kingdomcomedeliverance2";
    private static readonly Uri GraphQlEndpoint = new("https://api.nexusmods.com/v2/graphql");
    private static readonly Uri NexusApiRoot = new("https://api.nexusmods.com/");

    private readonly IApplicationPathService applicationPathService;
    private readonly INexusApiKeyProvider apiKeyProvider;
    private readonly INexusCookieAuthService cookieAuthService;
    private readonly HttpClient httpClient;
    private readonly ILogger logger;

    public NexusCollectionService(
        IApplicationPathService applicationPathService,
        INexusApiKeyProvider apiKeyProvider,
        INexusCookieAuthService cookieAuthService,
        ILogger logger)
        : this(
            applicationPathService,
            apiKeyProvider,
            cookieAuthService,
            logger,
            new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal NexusCollectionService(
        IApplicationPathService applicationPathService,
        INexusApiKeyProvider apiKeyProvider,
        INexusCookieAuthService cookieAuthService,
        ILogger logger,
        HttpMessageHandler handler)
    {
        this.applicationPathService = applicationPathService;
        this.apiKeyProvider = apiKeyProvider;
        this.cookieAuthService = cookieAuthService;
        this.logger = logger.ForContext<NexusCollectionService>();
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
    }

    public async Task<NexusCollectionRevisionInfo> GetLatestPublishedRevisionAsync(
        string slug,
        bool viewAdultContent,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidSlug(slug))
        {
            throw new NexusModsException("The Nexus collection slug is invalid.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query = LatestPublishedRevisionQuery,
            variables = new
            {
                slug = slug.Trim().ToLowerInvariant(),
                viewAdultContent
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await SendAuthenticatedAsync(request, cancellationToken);
        var body = await ReadLimitedBodyAsync(response, MaximumGraphQlResponseBytes, cancellationToken);
        EnsureSuccess(response, body, "Nexus collection metadata request failed");

        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        ThrowGraphQlErrors(document.RootElement);
        if (!TryGetProperty(document.RootElement, out var data, "data")
            || !TryGetProperty(data, out var collection, "collection")
            || collection.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new NexusModsException("The Nexus collection is unavailable or no longer public.", HttpStatusCode.NotFound);
        }

        var collectionStatus = ReadString(collection, "collectionStatus", "collection_status") ?? string.Empty;
        if (!IsPublicCollectionStatus(collectionStatus))
        {
            throw new NexusModsException("The Nexus collection is not public.", HttpStatusCode.Forbidden);
        }

        var gameDomain = TryGetProperty(collection, out var game, "game")
            ? ReadString(game, "domainName", "domain_name")
            : null;
        if (!string.Equals(gameDomain, GameDomainName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NexusModsException("The Nexus collection belongs to a different game.");
        }

        if (!TryGetProperty(collection, out var revision, "latestPublishedRevision", "latest_published_revision")
            || revision.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new NexusModsException("The Nexus collection has no published revision.", HttpStatusCode.NotFound);
        }

        var revisionStatus = ReadString(revision, "revisionStatus", "revision_status", "status") ?? string.Empty;
        if (!IsPublishedRevisionStatus(revisionStatus))
        {
            throw new NexusModsException("The latest Nexus collection revision is not public.", HttpStatusCode.Forbidden);
        }

        var downloadLink = ReadString(revision, "downloadLink", "download_link");
        if (string.IsNullOrWhiteSpace(downloadLink))
        {
            throw new NexusModsException("The Nexus collection revision did not provide an official download link.");
        }

        return new NexusCollectionRevisionInfo(
            ReadLong(collection, "id") ?? throw new NexusModsException("The Nexus collection id is missing."),
            ReadLong(revision, "id") ?? throw new NexusModsException("The Nexus collection revision id is missing."),
            ReadString(collection, "slug") ?? slug.Trim().ToLowerInvariant(),
            ReadString(collection, "name") ?? slug.Trim(),
            ReadString(collection, "summary") ?? string.Empty,
            gameDomain!,
            ReadInt(revision, "revisionNumber", "revision_number") ?? 0,
            revisionStatus,
            ReadBool(revision, "adultContent", "adult_content"),
            downloadLink,
            NormalizeApiSize(ReadLong(revision, "assetsSizeBytes", "assets_size_bytes", "fileSize", "file_size")),
            NormalizeApiSize(ReadLong(revision, "totalSize", "total_size")),
            ReadInt(revision, "modCount", "mod_count"));
    }

    public async Task<NexusCollectionPackage> DownloadAndReadPackageAsync(
        NexusCollectionRevisionInfo revision,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!string.Equals(revision.GameDomainName, GameDomainName, StringComparison.OrdinalIgnoreCase)
            || !IsValidSlug(revision.Slug))
        {
            throw new NexusModsException("The Nexus collection revision identity is invalid.");
        }

        var cacheDirectory = applicationPathService.GetPaths().CacheDirectory;
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new NexusModsException("The application cache directory is unavailable.");
        }

        var sessionDirectory = Path.Combine(
            cacheDirectory,
            "ModPacks",
            "Sessions",
            sessionId.ToString("N"));
        var bundledDirectory = Path.Combine(sessionDirectory, "bundled");
        Directory.CreateDirectory(sessionDirectory);
        Directory.CreateDirectory(bundledDirectory);

        var archivePath = Path.Combine(sessionDirectory, "collection.package");
        var archiveUri = await ResolveCollectionArchiveUriAsync(revision.DownloadLink, cancellationToken);
        await DownloadArchiveAsync(archiveUri, archivePath, cancellationToken);
        var manifest = await ReadPackageAsync(archivePath, bundledDirectory, cancellationToken);
        if (!string.Equals(manifest.Info.DomainName, GameDomainName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NexusModsException("The downloaded collection manifest belongs to a different game.");
        }

        logger.Information(
            "Prepared Nexus collection {CollectionSlug} revision {RevisionNumber} with {ItemCount} item(s).",
            revision.Slug,
            revision.RevisionNumber,
            manifest.Items.Count);
        return new NexusCollectionPackage(revision, manifest, sessionDirectory, archivePath, bundledDirectory);
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<Uri> ResolveCollectionArchiveUriAsync(
        string downloadLink,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveNexusApiUri(downloadLink);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await SendAuthenticatedAsync(request, cancellationToken);
        var body = await ReadLimitedBodyAsync(response, MaximumGraphQlResponseBytes, cancellationToken);
        EnsureSuccess(response, body, "Nexus collection download-link request failed");

        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return ReadFirstDownloadUri(root);
        }

        if (TryGetProperty(root, out var links, "download_links", "downloadLinks")
            && links.ValueKind == JsonValueKind.Array)
        {
            return ReadFirstDownloadUri(links);
        }

        if (TryGetProperty(root, out var link, "download_link", "downloadLink"))
        {
            return ReadDownloadUri(link);
        }

        return ReadDownloadUri(root);
    }

    private async Task DownloadArchiveAsync(Uri uri, string destinationPath, CancellationToken cancellationToken)
    {
        AssertOfficialDownloadUri(uri);
        var temporaryPath = destinationPath + ".tmp";
        TryDeleteFile(temporaryPath);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            EnsureNoRedirect(response);
            if (!response.IsSuccessStatusCode)
            {
                throw new NexusModsException(
                    $"Nexus collection archive download failed with HTTP {(int)response.StatusCode}.",
                    response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is > MaximumCollectionArchiveBytes)
            {
                throw new NexusModsException("The Nexus collection package is too large.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaximumCollectionArchiveBytes)
                    {
                        throw new NexusModsException("The Nexus collection package is too large.");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    internal static async Task<NexusCollectionManifest> ReadPackageAsync(
        string archivePath,
        string bundledDirectory,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var state = new CollectionPackageReadState();
            using var archive = ArchiveFactory.OpenArchive(packageStream);
            foreach (var entry in archive.Entries)
            {
                await ReadPackageEntryAsync(
                    state,
                    entry.Key ?? string.Empty,
                    entry.IsDirectory,
                    entry.Size,
                    entry.OpenEntryStream,
                    bundledDirectory,
                    cancellationToken);
            }

            if (state.ManifestBytes is null)
            {
                throw new NexusModsException("The Nexus collection package does not contain collection.json.");
            }

            return ParseManifest(state.ManifestBytes);
        }
        catch (Exception ex) when (ex is InvalidFormatException or ArchiveOperationException or NotSupportedException)
        {
            throw new NexusModsException("The Nexus collection package is not a supported compressed archive.", innerException: ex);
        }
    }

    private static async Task ReadPackageEntryAsync(
        CollectionPackageReadState state,
        string entryKey,
        bool isDirectory,
        long entrySize,
        Func<Stream> openEntryStream,
        string bundledDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isDirectory)
        {
            return;
        }

        state.EntryCount++;
        if (state.EntryCount > 10000)
        {
            throw new NexusModsException("The Nexus collection package contains too many files.");
        }

        var key = NormalizeArchiveKey(entryKey);
        if (string.Equals(Path.GetFileName(key), "collection.json", StringComparison.OrdinalIgnoreCase))
        {
            if (state.ManifestBytes is not null)
            {
                throw new NexusModsException("The Nexus collection package contains more than one collection.json manifest.");
            }

            if (entrySize > MaximumManifestBytes)
            {
                throw new NexusModsException("The Nexus collection manifest is too large.");
            }

            using var stream = openEntryStream();
            state.ManifestBytes = await ReadLimitedStreamAsync(stream, MaximumManifestBytes, cancellationToken);
            return;
        }

        if (!key.StartsWith("bundled/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        state.BundledBytes += Math.Max(0, entrySize);
        if (state.BundledBytes > MaximumBundledExtractedBytes)
        {
            throw new NexusModsException("The bundled collection resources are too large.");
        }

        var relativePath = key["bundled/".Length..];
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var destinationPath = Path.GetFullPath(Path.Combine(bundledDirectory, relativePath));
        if (!IsPathInsideDirectory(bundledDirectory, destinationPath))
        {
            throw new NexusModsException("The Nexus collection package contains an unsafe bundled path.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = openEntryStream();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyLimitedAsync(source, destination, MaximumBundledExtractedBytes - state.BundledBytes + entrySize, cancellationToken);
    }

    internal static NexusCollectionManifest ParseManifest(byte[] json)
    {
        ReadOnlyMemory<byte> content = json;
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF)
        {
            content = json.AsMemory(3);
        }

        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 96
        });
        var root = document.RootElement;
        if (!TryGetProperty(root, out var info, "info") || info.ValueKind != JsonValueKind.Object)
        {
            throw new NexusModsException("The Nexus collection manifest is missing its info block.");
        }

        var domainName = ReadString(info, "domainName", "domain_name") ?? string.Empty;
        var manifestInfo = new NexusCollectionManifestInfo(
            ReadString(info, "author") ?? string.Empty,
            ReadString(info, "name") ?? string.Empty,
            ReadString(info, "description") ?? string.Empty,
            ReadString(info, "summary") ?? string.Empty,
            domainName,
            ReadStringArray(info, "gameVersions", "game_versions"),
            ReadString(info, "installInstructions", "install_instructions") ?? string.Empty);

        if (!TryGetProperty(root, out var mods, "mods") || mods.ValueKind != JsonValueKind.Array)
        {
            throw new NexusModsException("The Nexus collection manifest is missing its mod list.");
        }

        var items = new List<NexusCollectionManifestItem>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.EnumerateArray())
        {
            if (items.Count >= MaximumManifestItems)
            {
                throw new NexusModsException("The Nexus collection manifest contains too many items.");
            }

            if (!TryGetProperty(mod, out var source, "source") || source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var sourceType = ParseSource(ReadString(source, "type"));
            var modId = ReadInt(source, "modId", "mod_id");
            var fileId = ReadInt(source, "fileId", "file_id");
            var logicalFileName = ReadString(source, "logicalFilename", "logical_filename");
            var identity = BuildItemIdentity(sourceType, modId, fileId, logicalFileName, items.Count);
            if (!identities.Add(identity))
            {
                continue;
            }

            items.Add(new NexusCollectionManifestItem(
                identity,
                ReadString(mod, "name") ?? $"Collection item {items.Count + 1}",
                ReadString(mod, "version") ?? string.Empty,
                ReadBool(mod, "optional"),
                ReadString(mod, "domainName", "domain_name") ?? domainName,
                sourceType,
                modId,
                fileId,
                ReadString(source, "md5"),
                NormalizeManifestSize(ReadLong(source, "fileSize", "file_size")),
                logicalFileName,
                ReadString(source, "fileExpression", "file_expression"),
                ReadString(source, "url"),
                ReadString(mod, "instructions") ?? ReadString(source, "instructions"),
                ReadString(mod, "author"),
                Math.Max(0, ReadInt(mod, "phase") ?? 0),
                HasNonEmptyValue(mod, "choices"),
                HasNonEmptyValue(mod, "patches"),
                HasNonEmptyValue(mod, "fileOverrides", "file_overrides"),
                ReadBool(source, "adultContent", "adult_content")));
        }

        return new NexusCollectionManifest(
            manifestInfo,
            items,
            ReadStringArray(root, "loadOrder", "load_order"));
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.UserAgent.ParseAdd("BohemiX/0.9.1");
        request.Headers.TryAddWithoutValidation("Application-Name", "BohemiX");
        request.Headers.TryAddWithoutValidation("Application-Version", "0.9.1");
        request.Headers.TryAddWithoutValidation("Protocol-Version", "1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var apiKey = await apiKeyProvider.GetApiKeyAsync(cancellationToken);
        NexusCookieAuthLease? cookieLease = null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
        }
        else
        {
            cookieLease = await cookieAuthService.TryCreateCookieHeaderLeaseAsync(request.RequestUri!, cancellationToken);
            if (cookieLease is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieLease.RevealHeaderValue());
            }
        }

        try
        {
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureNoRedirect(response);
            return response;
        }
        finally
        {
            cookieLease?.Dispose();
        }
    }

    private static Uri ResolveNexusApiUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !absolute.Host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new NexusModsException("The Nexus collection API link is not official.");
            }

            return absolute;
        }

        return new Uri(NexusApiRoot, value.TrimStart('/'));
    }

    private static Uri ReadFirstDownloadUri(JsonElement links)
    {
        foreach (var link in links.EnumerateArray())
        {
            try
            {
                return ReadDownloadUri(link);
            }
            catch (NexusModsException)
            {
            }
        }

        throw new NexusModsException("Nexus returned no usable collection archive URL.");
    }

    private static Uri ReadDownloadUri(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : ReadString(element, "URI", "uri", "url");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new NexusModsException("Nexus returned an invalid collection archive URL.");
        }

        AssertOfficialDownloadUri(uri);
        return uri;
    }

    private static void AssertOfficialDownloadUri(Uri uri)
    {
        var host = uri.Host;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !(host.Equals("api.nexusmods.com", StringComparison.OrdinalIgnoreCase)
                 || host.Equals("nexus-cdn.com", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase)
                 || host.Equals("nxmcdn.com", StringComparison.OrdinalIgnoreCase)
                 || host.EndsWith(".nxmcdn.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new NexusModsException("The collection archive URL is not an official Nexus host.");
        }
    }

    private static void EnsureNoRedirect(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new NexusModsException("Nexus returned an unexpected redirect.", response.StatusCode);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string prefix)
    {
        EnsureNoRedirect(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new NexusModsException($"{prefix}: HTTP {(int)response.StatusCode}.", response.StatusCode, responseBody: body);
        }
    }

    private static async Task<string> ReadLimitedBodyAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new NexusModsException("Nexus returned an unexpectedly large response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await ReadLimitedStreamAsync(stream, maximumBytes, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadLimitedStreamAsync(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, maximumBytes, cancellationToken);
        return buffer.ToArray();
    }

    private static async Task CopyLimitedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new NexusModsException("The Nexus collection package exceeds the extraction limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ThrowGraphQlErrors(JsonElement root)
    {
        if (!TryGetProperty(root, out var errors, "errors") || errors.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var messages = errors.EnumerateArray()
            .Select(error => ReadString(error, "message"))
            .Where(message => !string.IsNullOrWhiteSpace(message));
        throw new NexusModsException($"Nexus collection query failed: {string.Join("; ", messages)}");
    }

    private static bool IsValidSlug(string? slug)
    {
        return !string.IsNullOrWhiteSpace(slug)
            && slug.Length is >= 2 and <= 64
            && slug.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsPublishedRevisionStatus(string value)
    {
        return value.Equals("is_public", StringComparison.OrdinalIgnoreCase)
            || value.Equals("public", StringComparison.OrdinalIgnoreCase)
            || value.Equals("published", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublicCollectionStatus(string value)
    {
        return value.Equals("listed", StringComparison.OrdinalIgnoreCase)
            || value.Equals("unlisted", StringComparison.OrdinalIgnoreCase);
    }

    internal const string LatestPublishedRevisionQuery = """
        query GetCollection($slug: String!, $viewAdultContent: Boolean) {
          collection(slug: $slug, viewAdultContent: $viewAdultContent) {
            id
            slug
            name
            summary
            collectionStatus
            game { domainName }
            latestPublishedRevision {
              id
              revisionNumber
              revisionStatus
              status
              adultContent
              downloadLink
              assetsSizeBytes
              totalSize
              modCount
            }
          }
        }
        """;

    private static long? NormalizeApiSize(long? value)
    {
        return value is > 0 ? value : null;
    }

    private static long? NormalizeManifestSize(long? value)
    {
        return value is > 0 ? checked(value * 1024) : null;
    }

    private static ModPackInstallItemSource ParseSource(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "nexus" => ModPackInstallItemSource.Nexus,
            "bundle" => ModPackInstallItemSource.Bundle,
            "direct" => ModPackInstallItemSource.Direct,
            "browse" => ModPackInstallItemSource.Browse,
            "manual" => ModPackInstallItemSource.Manual,
            _ => ModPackInstallItemSource.Unknown
        };
    }

    private static string BuildItemIdentity(
        ModPackInstallItemSource source,
        int? modId,
        int? fileId,
        string? logicalFileName,
        int index)
    {
        return source switch
        {
            ModPackInstallItemSource.Nexus when modId is > 0 && fileId is > 0 => $"nexus-{modId}-{fileId}",
            ModPackInstallItemSource.Bundle when !string.IsNullOrWhiteSpace(logicalFileName) => $"bundle-{NormalizeIdentity(logicalFileName)}",
            _ => $"{source.ToString().ToLowerInvariant()}-{index + 1}"
        };
    }

    private static string NormalizeIdentity(string value)
    {
        return new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
    }

    private static bool HasNonEmptyValue(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            _ => true
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        return TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static long? ReadLong(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static bool ReadBool(JsonElement element, params string[] names)
    {
        if (!TryGetProperty(element, out var value, names))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolean) => boolean,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            _ => false
        };
    }

    private static string NormalizeArchiveKey(string? key)
    {
        return (key ?? string.Empty).Replace('\\', '/').TrimStart('/');
    }

    private static bool IsPathInsideDirectory(string rootDirectory, string candidatePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return Path.GetFullPath(candidatePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CollectionPackageReadState
    {
        public byte[]? ManifestBytes { get; set; }

        public long BundledBytes { get; set; }

        public int EntryCount { get; set; }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
