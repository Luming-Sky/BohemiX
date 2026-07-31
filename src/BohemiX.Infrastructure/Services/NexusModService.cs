using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class NexusModService : INexusModService, IDisposable
{
    private const string UserAgent = "BohemiX/0.1.0 (NexusAccountBinding)";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0";

    private readonly HttpClient httpClient;
    private readonly INexusApiKeyProvider apiKeyProvider;
    private readonly INexusCookieAuthService cookieAuthService;
    private readonly IReadOnlyList<INexusWebDownloadLinkResolver> webDownloadLinkResolvers;
    private readonly IReadOnlyList<INexusWebPageResolver> webPageResolvers;
    private readonly NexusModsOptions options;
    private readonly ILogger logger;

    public NexusModService(
        INexusApiKeyProvider apiKeyProvider,
        INexusCookieAuthService cookieAuthService,
        IEnumerable<INexusWebDownloadLinkResolver> webDownloadLinkResolvers,
        IEnumerable<INexusWebPageResolver> webPageResolvers,
        NexusModsOptions options,
        ILogger logger)
        : this(
            apiKeyProvider,
            cookieAuthService,
            options,
            logger,
            new HttpClientHandler { AllowAutoRedirect = false },
            webDownloadLinkResolvers,
            webPageResolvers)
    {
    }

    internal NexusModService(
        INexusApiKeyProvider apiKeyProvider,
        INexusCookieAuthService cookieAuthService,
        NexusModsOptions options,
        ILogger logger,
        HttpMessageHandler httpMessageHandler,
        IEnumerable<INexusWebDownloadLinkResolver>? webDownloadLinkResolvers = null,
        IEnumerable<INexusWebPageResolver>? webPageResolvers = null)
    {
        this.apiKeyProvider = apiKeyProvider;
        this.cookieAuthService = cookieAuthService;
        this.webDownloadLinkResolvers = webDownloadLinkResolvers?.ToArray() ?? [];
        this.webPageResolvers = webPageResolvers?.ToArray() ?? [];
        this.options = options;
        this.logger = logger.ForContext<NexusModService>();
        httpClient = new HttpClient(httpMessageHandler);
    }

    public async Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default)
    {
        var domain = NormalizeGameDomain(gameDomainName);
        using var document = await SendRestJsonAsync($"games/{Uri.EscapeDataString(domain)}.json", cancellationToken);
        var root = document.RootElement;

        return new NexusGameInfo(
            ReadInt(root, "id", "game_id") ?? 0,
            ReadString(root, "domain_name", "domainName") ?? domain,
            ReadString(root, "name") ?? domain,
            ReadInt(root, "mods", "mod_count", "modCount") ?? 0);
    }

    public async Task<NexusModSearchResult> SearchModsAsync(
        NexusModSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        const string query = """
                             query SearchMods($filter: ModsFilter, $sort: [ModsSort!], $offset: Int, $count: Int) {
                               mods(
                                 filter: $filter
                                 sort: $sort
                                 offset: $offset
                                 count: $count
                               ) {
                                 nodes {
                                   modId
                                   name
                                   summary
                                   version
                                   author
                                   downloads
                                   endorsements
                                   fileSize
                                   thumbnailUrl
                                   updatedAt
                                   directDownloadEnabled
                                   status
                                 }
                                 totalCount
                               }
                             }
                             """;

        var searchText = request.Query?.Trim();
        object variables = new
        {
            filter = BuildModsSearchFilter(NormalizeGameDomain(request.GameDomainName), searchText),
            sort = string.IsNullOrWhiteSpace(searchText)
                ? new object[] { new { downloads = new { direction = "DESC" } } }
                : [new { relevance = new { direction = "DESC" } }, new { downloads = new { direction = "DESC" } }],
            offset = Math.Max(0, request.Offset),
            count = Math.Clamp(request.Count, 1, 100)
        };

        using var document = await SendGraphQlAsync(query, variables, cancellationToken);
        var modsNode = document.RootElement.GetProperty("data").GetProperty("mods");
        var mods = modsNode.GetProperty("nodes")
            .EnumerateArray()
            .Select(ParseModSummary)
            .ToArray();

        return new NexusModSearchResult(mods, ReadInt(modsNode, "totalCount") ?? mods.Length);
    }

    private static object BuildModsSearchFilter(string domain, string? searchText)
    {
        var domainFilter = new
        {
            gameDomainName = new[] { new { value = domain, op = "EQUALS" } }
        };

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return domainFilter;
        }

        var keywordFilter = new
        {
            op = "OR",
            filter = BuildModsKeywordFilters(searchText.Trim())
        };

        return new
        {
            op = "AND",
            filter = new object[] { domainFilter, keywordFilter }
        };
    }

    private static object[] BuildModsKeywordFilters(string searchText)
    {
        var filters = new List<object>
        {
            new { nameStemmed = new[] { new { value = searchText, op = "MATCHES" } } },
            new { description = new[] { new { value = searchText, op = "MATCHES" } } },
            new { tag = new[] { new { value = searchText, op = "EQUALS" } } }
        };

        if (HasMinimumWildcardLength(searchText))
        {
            filters.Add(new { name = new[] { new { value = searchText, op = "WILDCARD" } } });
            filters.Add(new { author = new[] { new { value = searchText, op = "WILDCARD" } } });
            filters.Add(new { uploader = new[] { new { value = searchText, op = "WILDCARD" } } });
            filters.Add(new { categoryName = new[] { new { value = searchText, op = "WILDCARD" } } });
        }

        foreach (var token in GetWildcardSearchTokens(searchText))
        {
            filters.Add(new { name = new[] { new { value = token, op = "WILDCARD" } } });
            filters.Add(new { author = new[] { new { value = token, op = "WILDCARD" } } });
            filters.Add(new { uploader = new[] { new { value = token, op = "WILDCARD" } } });
            filters.Add(new { categoryName = new[] { new { value = token, op = "WILDCARD" } } });
            filters.Add(new { tag = new[] { new { value = token, op = "EQUALS" } } });
        }

        if (int.TryParse(searchText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modId))
        {
            filters.Add(new { modId = new[] { new { value = modId.ToString(CultureInfo.InvariantCulture), op = "EQUALS" } } });
        }

        return filters.ToArray();
    }

    private static IEnumerable<string> GetWildcardSearchTokens(string searchText)
    {
        return Regex.Split(searchText, @"\s+")
            .Select(token => token.Trim())
            .Where(HasMinimumWildcardLength)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasMinimumWildcardLength(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length >= 2;
    }

    public async Task<NexusModDetails> GetModDetailsAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var game = await GetGameAsync(gameDomainName, cancellationToken);
            const string query = """
                                 query GetMod($modId: ID!, $gameId: ID!) {
                                   mod(modId: $modId, gameId: $gameId) {
                                     modId
                                     gameId
                                     name
                                     summary
                                     description
                                     version
                                     author
                                     downloads
                                     endorsements
                                     thumbnailUrl
                                     directDownloadEnabled
                                     status
                                     modRequirements {
                                       nexusRequirements(offset: 0, count: 100) {
                                         nodes {
                                           modId
                                           modName
                                           gameId
                                           externalRequirement
                                           notes
                                           url
                                         }
                                       }
                                     }
                                   }
                                 }
                                 """;

            using var document = await SendGraphQlAsync(
                query,
                new { modId = modId.ToString(), gameId = game.Id.ToString() },
                cancellationToken);

            var mod = document.RootElement.GetProperty("data").GetProperty("mod");
            var files = await GetModFilesAsync(game.DomainName, modId, cancellationToken);
            var thumbnailUrl = ReadString(mod, "thumbnailUrl")
                ?? await TryGetModThumbnailFromWebAsync(game.DomainName, modId, cancellationToken);

            return new NexusModDetails(
                ReadInt(mod, "modId") ?? modId,
                ReadInt(mod, "gameId") ?? game.Id,
                game.DomainName,
                ReadString(mod, "name") ?? $"Mod {modId}",
                ReadString(mod, "summary") ?? string.Empty,
                ReadString(mod, "description") ?? string.Empty,
                ReadString(mod, "version") ?? string.Empty,
                ReadString(mod, "author") ?? string.Empty,
                ReadInt(mod, "downloads") ?? 0,
                ReadInt(mod, "endorsements") ?? 0,
                ReadBool(mod, "directDownloadEnabled"),
                ReadString(mod, "status") ?? string.Empty,
                ParseRequirements(mod),
                files,
                thumbnailUrl);
        }
        catch (NexusModsException ex) when (options.EnableLegacyBrowserCookieAuth && ShouldTryBrowserCookieWebFallback(ex))
        {
            logger.Warning(
                "Nexus API details request for mod {ModId} failed with {StatusCode}; trying browser Cookie web fallback.",
                modId,
                ex.StatusCode);
            return await GetModDetailsFromWebAsync(gameDomainName, modId, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken = default)
    {
        var domain = NormalizeGameDomain(gameDomainName);
        JsonDocument document;
        try
        {
            document = await SendRestJsonAsync(
                $"games/{Uri.EscapeDataString(domain)}/mods/{modId}/files.json",
                cancellationToken);
        }
        catch (NexusModsException ex) when (options.EnableLegacyBrowserCookieAuth && ShouldTryBrowserCookieWebFallback(ex))
        {
            logger.Warning(
                "Nexus API files request for mod {ModId} failed with {StatusCode}; trying browser Cookie web fallback.",
                modId,
                ex.StatusCode);
            return await GetModFilesFromWebAsync(domain, modId, cancellationToken);
        }

        using (document)
        {
        var root = document.RootElement;
        var filesElement = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("files", out var filesProperty)
                ? filesProperty
                : default;

        if (filesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return filesElement
            .EnumerateArray()
            .Select(element => ParseModFile(element, modId))
            .Where(file => file.FileId > 0)
            .ToArray();
        }
    }

    public async Task<NexusModFile> GetPreferredFileAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken = default)
    {
        var files = await GetModFilesAsync(gameDomainName, modId, cancellationToken);
        var preferred = files
            .Where(file => file.IsManagerDownload)
            .OrderByDescending(file => file.IsPrimary)
            .ThenBy(file => FileCategoryRank(file.Category))
            .ThenByDescending(file => file.UploadedTimestamp)
            .FirstOrDefault()
            ?? files
                .OrderByDescending(file => file.IsPrimary)
                .ThenBy(file => FileCategoryRank(file.Category))
                .ThenByDescending(file => file.UploadedTimestamp)
                .FirstOrDefault();

        return preferred ?? throw new NexusModsException($"No downloadable files were found for mod {modId}.");
    }

    public async Task<NexusDownloadLink> GetDownloadLinkAsync(
        string gameDomainName,
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        return await GetDownloadLinkCoreAsync(
            gameDomainName,
            modId,
            fileId,
            authorization: null,
            cancellationToken);
    }

    public async Task<NexusDownloadLink> GetDownloadLinkAsync(
        string gameDomainName,
        int modId,
        int fileId,
        NexusDownloadAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        return await GetDownloadLinkCoreAsync(
            gameDomainName,
            modId,
            fileId,
            authorization,
            cancellationToken);
    }

    private async Task<NexusDownloadLink> GetDownloadLinkCoreAsync(
        string gameDomainName,
        int modId,
        int fileId,
        NexusDownloadAuthorization? authorization,
        CancellationToken cancellationToken)
    {
        var domain = NormalizeGameDomain(gameDomainName);
        JsonDocument document;
        try
        {
            var relativeUri = $"games/{Uri.EscapeDataString(domain)}/mods/{modId}/files/{fileId}/download_link.json";
            if (authorization is not null)
            {
                if (string.IsNullOrWhiteSpace(authorization.Key)
                    || authorization.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    throw new NexusModsException("The Nexus download authorization is missing or expired.");
                }

                relativeUri += $"?key={Uri.EscapeDataString(authorization.Key)}&expires={authorization.Expires.ToString(CultureInfo.InvariantCulture)}";
            }

            document = await SendRestJsonAsync(
                relativeUri,
                cancellationToken);
        }
        catch (NexusModsException ex) when (
            authorization is null
            && options.EnableLegacyBrowserCookieAuth
            && ShouldTryBrowserCookieWebFallback(ex))
        {
            logger.Warning(
                "Nexus API download link request for mod {ModId}, file {FileId} failed with {StatusCode}; trying browser Cookie web fallback.",
                modId,
                fileId,
                ex.StatusCode);
            return await GetDownloadLinkFromWebAsync(domain, modId, fileId, cancellationToken);
        }

        using (document)
        {
        var root = document.RootElement;
        var links = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("download_links", out var linksProperty)
                ? linksProperty
                : default;

        if (links.ValueKind != JsonValueKind.Array)
        {
            throw new NexusModsException($"Nexus did not return a download link for mod {modId}, file {fileId}.");
        }

        var usableLinks = new List<NexusDownloadLink>();
        foreach (var link in links.EnumerateArray())
        {
            var uriText = ReadString(link, "URI", "uri");
            if (TryCreateNexusUri(uriText, out var uri))
            {
                usableLinks.Add(new NexusDownloadLink(
                    uri,
                    ReadString(link, "name") ?? "Nexus Mods",
                    ReadString(link, "short_name", "shortName") ?? "Nexus"));
            }
        }

        var selectedLink = usableLinks
            .OrderByDescending(link => RankNexusDownloadUri(link.Uri))
            .FirstOrDefault();
        if (selectedLink is not null)
        {
            return selectedLink;
        }

        throw new NexusModsException($"Nexus returned no usable download URL for mod {modId}, file {fileId}.");
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<JsonDocument> SendGraphQlAsync(
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, options.GraphQlEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        var useCookieAuth = await ApplyCommonHeadersAsync(request, requireApiKey: false, allowCookieAuth: true, cancellationToken);
        using var response = await SendWithRateLimitRetryAsync(request, useCookieAuth, requireAuthentication: false, cancellationToken);
        await EnsureSuccessStatusAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        ThrowIfGraphQlErrors(document);
        return document;
    }

    private async Task<NexusModDetails> GetModDetailsFromWebAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken)
    {
        var domain = NormalizeGameDomain(gameDomainName);
        logger.Information("Loading Nexus mod {ModId} details from web fallback for {GameDomainName}.", modId, domain);
        var html = await SendNexusWebTextAsync(BuildModPageUri(domain, modId, null), "text/html", cancellationToken);
        var files = await GetModFilesFromWebAsync(domain, modId, cancellationToken);

        return new NexusModDetails(
            modId,
            ReadWebGameId(html) ?? 0,
            domain,
            ReadWebModName(html) ?? $"Mod {modId}",
            ReadWebMetaContent(html, "description") ?? string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            true,
            "CookieAuth",
            [],
            files,
            ReadWebMetaContent(html, "og:image")
                ?? ReadWebMetaContent(html, "twitter:image"));
    }

    private async Task<string?> TryGetModThumbnailFromWebAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await SendNexusWebTextAsync(
                BuildModPageUri(gameDomainName, modId, null),
                "text/html",
                cancellationToken);
            return ReadWebMetaContent(html, "og:image")
                ?? ReadWebMetaContent(html, "twitter:image");
        }
        catch (NexusModsException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<NexusModFile>> GetModFilesFromWebAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken)
    {
        logger.Information("Loading Nexus mod {ModId} files from web fallback for {GameDomainName}.", modId, gameDomainName);
        var html = await SendNexusWebTextAsync(BuildModPageUri(gameDomainName, modId, "files"), "text/html", cancellationToken);
        ThrowIfNexusLoginPage(html);
        var files = ParseWebFileList(html, modId);
        if (files.Count == 0)
        {
            logger.Warning(
                "Nexus web fallback found no downloadable file entries for mod {ModId}. The Nexus page may require additional account permissions or its markup may have changed.",
                modId);
        }

        return files;
    }

    private async Task<NexusDownloadLink> GetDownloadLinkFromWebAsync(
        string gameDomainName,
        int modId,
        int fileId,
        CancellationToken cancellationToken)
    {
        var filesPageUri = BuildModPageUri(gameDomainName, modId, "files", fileId);
        logger.Information(
            "Loading Nexus download link from web fallback for mod {ModId}, file {FileId}.",
            modId,
            fileId);
        var gameId = TryResolveKnownWebGameId(gameDomainName);
        if (gameId is not { } resolvedGameId)
        {
            var html = await SendNexusWebTextAsync(filesPageUri, "text/html", cancellationToken);
            ThrowIfNexusLoginPage(html);
            resolvedGameId = ReadWebGameId(html)
                ?? throw new NexusModsException("Nexus web download page did not expose a game id.", HttpStatusCode.NotFound);
        }

        foreach (var resolver in webDownloadLinkResolvers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var embeddedBrowserBody = await resolver.TryGenerateDownloadUrlAsync(
                    gameDomainName,
                    modId,
                    fileId,
                    resolvedGameId,
                    filesPageUri,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(embeddedBrowserBody))
                {
                    continue;
                }

                var embeddedBrowserUri = TryReadDownloadUri(embeddedBrowserBody);
                if (embeddedBrowserUri is null)
                {
                    logger.Warning(
                        "Nexus embedded browser download URL endpoint returned no usable URL for mod {ModId}, file {FileId}. Body summary: {BodySummary}",
                        modId,
                        fileId,
                        SummarizeNexusDownloadUrlBody(embeddedBrowserBody));
                    continue;
                }

                AssertNexusDownloadUri(embeddedBrowserUri);
                logger.Information(
                    "Nexus download link was generated in embedded browser context for mod {ModId}, file {FileId}.",
                    modId,
                    fileId);
                return new NexusDownloadLink(embeddedBrowserUri, "Nexus Mods", "CookieAuth");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Warning(
                    ex,
                    "Nexus embedded browser download URL generation failed for mod {ModId}, file {FileId}; trying HTTP Cookie fallback.",
                    modId,
                    fileId);
            }
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://www.nexusmods.com/Core/Libs/Common/Managers/Downloads?GenerateDownloadUrl"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fid"] = fileId.ToString(),
                ["game_id"] = resolvedGameId.ToString(CultureInfo.InvariantCulture)
            })
        };

        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.nexusmods.com");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.Referrer = filesPageUri;

        using var response = await SendWithRateLimitRetryAsync(
            request,
            useCookieAuth: true,
            requireAuthentication: true,
            cancellationToken);

        logger.Information(
            "Nexus web download URL endpoint returned HTTP {StatusCode} for mod {ModId}, file {FileId}.",
            response.StatusCode,
            modId,
            fileId);

        await EnsureSuccessStatusAsync(response, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var uri = TryReadDownloadUri(body)
            ?? throw new NexusModsException("Nexus web download endpoint did not return a usable download URL.", HttpStatusCode.NotFound);

        AssertNexusDownloadUri(uri);
        return new NexusDownloadLink(uri, "Nexus Mods", "CookieAuth");
    }

    private async Task<string> SendNexusWebTextAsync(
        Uri uri,
        string acceptMediaType,
        CancellationToken cancellationToken)
    {
        NexusCookieAuthService.AssertNexusModsUri(uri);
        foreach (var resolver in webPageResolvers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var browserHtml = await resolver.TryLoadPageHtmlAsync(uri, cancellationToken);
                if (string.IsNullOrWhiteSpace(browserHtml))
                {
                    continue;
                }

                logger.Information("Nexus web fallback loaded {Uri} in embedded browser context.", uri);
                ThrowIfNexusLoginPage(browserHtml);
                return browserHtml;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Nexus embedded browser page load failed for {Uri}; trying HTTP Cookie fallback.", uri);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(acceptMediaType));
        request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");

        using var response = await SendWithRateLimitRetryAsync(
            request,
            useCookieAuth: true,
            requireAuthentication: true,
            cancellationToken);

        logger.Information(
            "Nexus web fallback GET {Uri} returned HTTP {StatusCode}.",
            uri,
            response.StatusCode);

        await EnsureSuccessStatusAsync(response, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfNexusLoginPage(html);
        return html;
    }

    private static void ThrowIfNexusLoginPage(string html)
    {
        if (html.Contains("/users/login", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Log in to Nexus Mods", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Sign in to Nexus Mods", StringComparison.OrdinalIgnoreCase))
        {
            throw new NexusModsException(
                "Nexus returned a sign-in page. Browser Cookie auth was not accepted for this request.",
                HttpStatusCode.Unauthorized);
        }
    }

    private static bool ShouldTryBrowserCookieWebFallback(NexusModsException exception)
    {
        if (exception.IsRateLimited)
        {
            return false;
        }

        if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        var text = $"{exception.Message} {exception.ResponseBody}";
        return text.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || text.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri BuildModPageUri(
        string gameDomainName,
        int modId,
        string? tab,
        int? fileId = null)
    {
        var domain = NormalizeGameDomain(gameDomainName);
        var builder = new UriBuilder("https", "www.nexusmods.com")
        {
            Path = $"{Uri.EscapeDataString(domain)}/mods/{modId.ToString(CultureInfo.InvariantCulture)}"
        };

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(tab))
        {
            query.Add("tab=" + Uri.EscapeDataString(tab.Trim()));
        }

        if (fileId is not null)
        {
            query.Add("file_id=" + fileId.Value.ToString(CultureInfo.InvariantCulture));
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static int? TryResolveKnownWebGameId(string gameDomainName)
    {
        return NormalizeGameDomain(gameDomainName).ToLowerInvariant() switch
        {
            "kingdomcomedeliverance2" => 5851,
            _ => null
        };
    }

    private static int? ReadWebGameId(string html)
    {
        return TryReadIntFromAttributes(html, "data-game-id", "game-id", "game_id", "gameId")
            ?? TryReadIntByPattern(html, @"[""']game_id[""']\s*:\s*[""']?(?<value>\d+)")
            ?? TryReadIntByPattern(html, @"[""']gameId[""']\s*:\s*[""']?(?<value>\d+)")
            ?? TryReadIntByPattern(html, @"name\s*=\s*[""']game_id[""'][^>]*value\s*=\s*[""'](?<value>\d+)")
            ?? TryReadIntByPattern(html, @"value\s*=\s*[""'](?<value>\d+)[""'][^>]*name\s*=\s*[""']game_id[""']");
    }

    private static string? ReadWebModName(string html)
    {
        return ReadWebMetaContent(html, "og:title")
            ?? ReadWebMetaContent(html, "twitter:title")
            ?? ReadTextByPattern(html, @"<h1[^>]*>(?<value>.*?)</h1>")
            ?? ReadTextByPattern(html, @"<title[^>]*>(?<value>.*?)</title>");
    }

    private static string? ReadWebMetaContent(string html, string name)
    {
        foreach (Match match in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attributes = ParseHtmlAttributes(match.Value);
            if (!attributes.TryGetValue("content", out var content))
            {
                continue;
            }

            if (AttributeEquals(attributes, "name", name)
                || AttributeEquals(attributes, "property", name)
                || AttributeEquals(attributes, "name", "og:" + name)
                || AttributeEquals(attributes, "property", "og:" + name))
            {
                return CleanText(content);
            }
        }

        return null;
    }

    private static IReadOnlyList<NexusModFile> ParseWebFileList(string html, int fallbackModId)
    {
        var files = new Dictionary<int, NexusModFile>();

        foreach (var file in ParseWebFilesFromJson(html, fallbackModId).Concat(ParseWebFilesFromMarkup(html, fallbackModId)))
        {
            if (file.FileId > 0)
            {
                files.TryAdd(file.FileId, file);
            }
        }

        return files.Values
            .OrderByDescending(file => file.IsPrimary)
            .ThenBy(file => FileCategoryRank(file.Category))
            .ThenByDescending(file => file.UploadedTimestamp)
            .ToArray();
    }

    private static IReadOnlyList<NexusModFile> ParseWebFilesFromJson(string html, int fallbackModId)
    {
        var files = new List<NexusModFile>();
        foreach (Match match in Regex.Matches(html, @"\{[^{}]*(?:""file_id""|""fileId"")[^{}]*\}", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            try
            {
                using var document = JsonDocument.Parse(match.Value);
                var file = ParseModFile(document.RootElement, fallbackModId);
                if (file.FileId > 0)
                {
                    files.Add(NormalizeWebFile(file));
                }
            }
            catch (JsonException)
            {
            }
        }

        return files;
    }

    private static IEnumerable<NexusModFile> ParseWebFilesFromMarkup(string html, int fallbackModId)
    {
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        foreach (Match match in Regex.Matches(
            html,
            @"<(?<tag>a|button|li|div|article|tr)\b(?<attrs>[^>]*(?:data-file-id|file_id=|file_id%3D|fid=|fid%3D)[^>]*)>(?<body>.*?)</\k<tag>>|<(?<tag>a|button|li|div|article|tr)\b(?<attrs>[^>]*(?:data-file-id|file_id=|file_id%3D|fid=|fid%3D)[^>]*)/?>",
            Options))
        {
            var fragment = match.Value;
            var attributes = ParseHtmlAttributes(match.Groups["attrs"].Value);
            var fileId = TryReadFileId(attributes, fragment);
            if (fileId is null or <= 0)
            {
                continue;
            }

            var category = FirstNonEmpty(
                GetAttribute(attributes, "data-category-name"),
                GetAttribute(attributes, "data-category"),
                ReadTextByPattern(fragment, @"(?:MAIN|UPDATE|OPTIONAL|OLD|MISCELLANEOUS)\s+FILES"));

            var name = FirstNonEmpty(
                GetAttribute(attributes, "data-name"),
                GetAttribute(attributes, "name"),
                GetAttribute(attributes, "title"),
                GetAttribute(attributes, "aria-label"),
                ReadTextByPattern(fragment, @"<(?:h3|h4|dt|a|button)\b[^>]*>(?<value>.*?)</(?:h3|h4|dt|a|button)>"),
                $"File {fileId.Value}") ?? $"File {fileId.Value}";

            var fileName = FirstNonEmpty(
                GetAttribute(attributes, "data-file-name"),
                GetAttribute(attributes, "data-filename"),
                GetAttribute(attributes, "download"),
                TryExtractFileName(name),
                $"{SanitizeFallbackFileName(name)}-{fileId.Value.ToString(CultureInfo.InvariantCulture)}.zip")
                ?? $"file-{fileId.Value.ToString(CultureInfo.InvariantCulture)}.zip";

            var uploadedTimestamp = TryReadIntFromAttributes(
                attributes,
                "data-uploaded-timestamp",
                "data-uploaded",
                "data-date",
                "data-timestamp") ?? 0;

            var managerDownload = TryReadBoolFromAttributes(
                attributes,
                "data-manager",
                "data-manager-download",
                "data-is-manager-download") ?? true;

            var isPrimary = TryReadBoolFromAttributes(
                attributes,
                "data-primary",
                "data-is-primary") ?? IsPrimaryCategory(category);

            yield return new NexusModFile(
                fileId.Value,
                fallbackModId,
                name,
                fileName,
                category ?? string.Empty,
                FirstNonEmpty(GetAttribute(attributes, "data-version"), ReadTextByPattern(fragment, @"Version\s*(?<value>[0-9][^\s<]*)")) ?? string.Empty,
                TryReadSize(attributes, fragment),
                FirstNonEmpty(GetAttribute(attributes, "data-md5"), ReadTextByPattern(fragment, @"MD5\s*[:#]?\s*(?<value>[a-f0-9]{16,32})")),
                uploadedTimestamp,
                isPrimary,
                managerDownload,
                FirstNonEmpty(GetAttribute(attributes, "data-description"), GetAttribute(attributes, "data-file-description")));
        }
    }

    private static NexusModFile NormalizeWebFile(NexusModFile file)
    {
        var fileName = string.IsNullOrWhiteSpace(file.FileName)
            ? $"{SanitizeFallbackFileName(file.Name)}-{file.FileId.ToString(CultureInfo.InvariantCulture)}.zip"
            : file.FileName;

        return file with
        {
            FileName = fileName,
            IsManagerDownload = true,
            IsPrimary = file.IsPrimary || IsPrimaryCategory(file.Category)
        };
    }

    private static Uri? TryReadDownloadUri(string body)
    {
        var candidates = new List<Uri>();
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var value in EnumerateJsonStrings(document.RootElement))
            {
                if (TryCreateNexusUri(value, out var uri))
                {
                    candidates.Add(uri);
                }
            }
        }
        catch (JsonException)
        {
        }

        foreach (Match match in Regex.Matches(body, @"https?:\\?/\\?/[^\s""'<>\\]+", RegexOptions.IgnoreCase))
        {
            var candidate = match.Value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
            if (TryCreateNexusUri(WebUtility.HtmlDecode(candidate), out var uri))
            {
                candidates.Add(uri);
            }
        }

        foreach (Match match in Regex.Matches(
            body,
            @"(?:href|src|data-download-url|data-url|url)\s*=\s*(?:""(?<value>[^""]+)""|'(?<value>[^']+)'|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var candidate = match.Groups["value"].Value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
            if (TryCreateNexusUri(WebUtility.HtmlDecode(candidate), out var uri))
            {
                candidates.Add(uri);
            }
        }

        return candidates
            .DistinctBy(uri => uri.AbsoluteUri)
            .OrderByDescending(RankNexusDownloadUri)
            .FirstOrDefault();
    }

    private static string SummarizeNexusDownloadUrlBody(string body)
    {
        var normalized = Regex.Replace(body, @"\s+", " ").Trim();
        var preview = normalized.Length <= 500 ? normalized : normalized[..500] + "...";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var keys = string.Join(",", document.RootElement.EnumerateObject().Select(property => property.Name));
                return $"Length={body.Length}; JsonKeys={keys}; Preview={preview}";
            }
        }
        catch (JsonException)
        {
        }

        return $"Length={body.Length}; Preview={preview}";
    }

    private static IEnumerable<string> EnumerateJsonStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var nestedValue in EnumerateJsonStrings(property.Value))
                    {
                        yield return nestedValue;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nestedValue in EnumerateJsonStrings(item))
                    {
                        yield return nestedValue;
                    }
                }

                break;
        }
    }

    private static bool TryCreateNexusUri(string? value, out Uri uri)
    {
        value = value?.Trim().Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(value))
        {
            value = value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);

            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = "https:" + value;
            }
            else if (value.StartsWith("/", StringComparison.Ordinal))
            {
                value = "https://www.nexusmods.com" + value;
            }
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && IsNexusDownloadHost(uri.Host))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsNexusDownloadHost(string host)
    {
        return NexusCookieAuthService.IsNexusModsHost(host)
            || host.Equals("nexus-cdn.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("nxmcdn.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".nxmcdn.com", StringComparison.OrdinalIgnoreCase);
    }

    private static int RankNexusDownloadUri(Uri uri)
    {
        var rank = 0;
        if (uri.Host.Equals("premium-files.nexus-cdn.com", StringComparison.OrdinalIgnoreCase))
        {
            rank += 300;
        }
        else if (uri.Host.Equals("supporter-files.nexus-cdn.com", StringComparison.OrdinalIgnoreCase))
        {
            rank += 200;
        }

        if (!NexusCookieAuthService.IsNexusModsHost(uri.Host))
        {
            rank += 100;
        }

        if (uri.Host.Contains("staticdelivery", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Contains("download", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".nxmcdn.com", StringComparison.OrdinalIgnoreCase))
        {
            rank += 80;
        }

        if (uri.AbsolutePath.Contains("/files/", StringComparison.OrdinalIgnoreCase))
        {
            rank += 40;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            rank += 20;
        }

        return rank;
    }

    private static void AssertNexusDownloadUri(Uri uri)
    {
        if (!IsNexusDownloadHost(uri.Host))
        {
            throw new InvalidOperationException("Refusing to use a Nexus download URL from an untrusted domain.");
        }
    }

    private static Dictionary<string, string> ParseHtmlAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
            text,
            @"(?<name>[\w:-]+)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            attributes[match.Groups["name"].Value] = CleanText(match.Groups["value"].Value);
        }

        return attributes;
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name)
    {
        return attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool AttributeEquals(IReadOnlyDictionary<string, string> attributes, string name, string expected)
    {
        return attributes.TryGetValue(name, out var value)
            && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryReadFileId(IReadOnlyDictionary<string, string> attributes, string fragment)
    {
        return TryReadIntFromAttributes(attributes, "data-file-id", "file-id", "file_id", "fid")
            ?? TryReadIntByPattern(fragment, @"(?:file_id|file-id|fid)(?:=|%3D|[""':\s]+)(?<value>\d+)");
    }

    private static int? TryReadIntFromAttributes(string html, params string[] attributeNames)
    {
        return TryReadIntFromAttributes(ParseHtmlAttributes(html), attributeNames);
    }

    private static int? TryReadIntFromAttributes(IReadOnlyDictionary<string, string> attributes, params string[] attributeNames)
    {
        foreach (var attributeName in attributeNames)
        {
            if (attributes.TryGetValue(attributeName, out var value)
                && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static int? TryReadIntByPattern(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success
            && int.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static bool? TryReadBoolFromAttributes(IReadOnlyDictionary<string, string> attributes, params string[] attributeNames)
    {
        foreach (var attributeName in attributeNames)
        {
            if (!attributes.TryGetValue(attributeName, out var value))
            {
                continue;
            }

            if (bool.TryParse(value, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                return intValue != 0;
            }
        }

        return null;
    }

    private static long? TryReadSize(IReadOnlyDictionary<string, string> attributes, string fragment)
    {
        if (TryReadLongAttribute(attributes, "data-size-bytes", "data-size-in-bytes") is { } bytes)
        {
            return bytes;
        }

        if (TryReadLongAttribute(attributes, "data-size-kb", "data-size") is { } kilobytes
            && string.Equals(GetAttribute(attributes, "data-size-kb") ?? string.Empty, kilobytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return kilobytes * 1024;
        }

        return TryParseHumanSize(GetAttribute(attributes, "data-size"))
            ?? TryParseHumanSize(ReadTextByPattern(fragment, @"(?<value>\d+(?:\.\d+)?\s*(?:B|KB|MB|GB|KiB|MiB|GiB))"));
    }

    private static long? TryReadLongAttribute(IReadOnlyDictionary<string, string> attributes, params string[] attributeNames)
    {
        foreach (var attributeName in attributeNames)
        {
            if (attributes.TryGetValue(attributeName, out var value)
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static long? TryParseHumanSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(
            text,
            @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>B|KB|MB|GB|KiB|MiB|GiB)",
            RegexOptions.IgnoreCase);
        if (!match.Success
            || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024d,
            "GB" or "GIB" => 1024d * 1024d * 1024d,
            _ => 1d
        };

        return checked((long)Math.Round(value * multiplier));
    }

    private static string? ReadTextByPattern(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanText(match.Groups["value"].Value) : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsPrimaryCategory(string? category)
    {
        return category?.Contains("MAIN", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? TryExtractFileName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"(?<value>[\w .()\[\]-]+\.(?:zip|7z|rar|tar|gz|pak))", RegexOptions.IgnoreCase);
        return match.Success ? CleanText(match.Groups["value"].Value) : null;
    }

    private static string SanitizeFallbackFileName(string? value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value) ? "mod-file" : CleanText(value).ToLowerInvariant();
        sanitized = Regex.Replace(sanitized, @"[^\w.-]+", "-", RegexOptions.CultureInvariant).Trim('-', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "mod-file" : sanitized;
    }

    private static string CleanText(string text)
    {
        var withoutScripts = Regex.Replace(text, @"<script\b.*?</script>|<style\b.*?</style>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withoutTags = Regex.Replace(withoutScripts, "<.*?>", " ", RegexOptions.Singleline);
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private async Task<JsonDocument> SendRestJsonAsync(string relativePath, CancellationToken cancellationToken)
    {
        var baseUri = options.RestApiBaseUrl.EndsWith("/", StringComparison.Ordinal)
            ? options.RestApiBaseUrl
            : options.RestApiBaseUrl + "/";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUri), relativePath));
        var useCookieAuth = await ApplyCommonHeadersAsync(request, requireApiKey: true, allowCookieAuth: true, cancellationToken);

        using var response = await SendWithRateLimitRetryAsync(request, useCookieAuth, requireAuthentication: true, cancellationToken);
        await EnsureSuccessStatusAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<bool> ApplyCommonHeadersAsync(
        HttpRequestMessage request,
        bool requireApiKey,
        bool allowCookieAuth,
        CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Application-Name", "BohemiX");
        request.Headers.TryAddWithoutValidation("Application-Version", "0.1.0");
        request.Headers.TryAddWithoutValidation("Protocol-Version", "1.0");

        var apiKey = await apiKeyProvider.GetApiKeyAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            return false;
        }

        if (options.EnableLegacyBrowserCookieAuth && allowCookieAuth && request.RequestUri is not null)
        {
            NexusCookieAuthService.AssertNexusModsUri(request.RequestUri);
            return true;
        }

        if (requireApiKey)
        {
            throw new NexusModsException("A bound Nexus Mods account or API key is required.", HttpStatusCode.Unauthorized);
        }

        return false;
    }

    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        HttpRequestMessage request,
        bool useCookieAuth,
        bool requireAuthentication,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var attemptRequest = await CloneRequestAsync(request, cancellationToken);
            HttpResponseMessage response;
            using (var cookieLease = await CreateCookieAuthLeaseForSendAttemptAsync(
                       attemptRequest,
                       useCookieAuth,
                       requireAuthentication,
                       cancellationToken))
            {
                ApplyCookieHeaderToSendAttempt(attemptRequest, cookieLease);
                response = await httpClient.SendAsync(attemptRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }

            if (response.StatusCode != (HttpStatusCode)429 || attempt == 3)
            {
                return response;
            }

            var delay = ResolveRetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Unexpected Nexus Mods retry flow.");
    }

    private async Task<NexusCookieAuthLease?> CreateCookieAuthLeaseForSendAttemptAsync(
        HttpRequestMessage request,
        bool useCookieAuth,
        bool requireAuthentication,
        CancellationToken cancellationToken)
    {
        if (!useCookieAuth)
        {
            return null;
        }

        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Refusing to attach Nexus browser Cookie auth to a request without a URI.");
        }

        NexusCookieAuthService.AssertNexusModsUri(request.RequestUri);
        var cookieLease = await cookieAuthService.TryCreateCookieHeaderLeaseAsync(request.RequestUri, cancellationToken);
        if (cookieLease is null && requireAuthentication)
        {
            var probe = await cookieAuthService.ProbeAsync(cancellationToken);
            var message = probe.Success
                ? $"Nexus browser sign-in was detected from {probe.BrowserName}, but no Cookie header could be prepared for this request."
                : $"Nexus browser sign-in or API key is required. {FormatCookieAuthProbeFailure(probe)}";

            logger.Warning(
                "Nexus browser Cookie auth could not prepare a Cookie header for {Uri}: {ErrorCode} - {Message}",
                request.RequestUri,
                probe.ErrorCode,
                FormatCookieAuthProbeFailure(probe));
            throw new NexusModsException(message, HttpStatusCode.Unauthorized);
        }

        return cookieLease;
    }

    private static string FormatCookieAuthProbeFailure(NexusCookieAuthProbeResult probe)
    {
        if (probe.Attempts.Count == 0)
        {
            return probe.Message;
        }

        var attempts = string.Join("; ", probe.Attempts.Select(attempt => $"{attempt.BrowserName}: {attempt.Message}"));
        return $"{probe.Message} {attempts}";
    }

    private static void ApplyCookieHeaderToSendAttempt(
        HttpRequestMessage request,
        NexusCookieAuthLease? cookieLease)
    {
        if (cookieLease is null)
        {
            return;
        }

        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Refusing to attach Nexus browser Cookie auth to a request without a URI.");
        }

        NexusCookieAuthService.AssertNexusModsUri(request.RequestUri);
        request.Headers.TryAddWithoutValidation("Cookie", cookieLease.RevealHeaderValue());
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(contentBytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));
    }

    private static async Task EnsureSuccessStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAfterDate)
        {
            retryAfter = retryAfterDate - DateTimeOffset.UtcNow;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Nexus Mods authentication failed. Sign in in the browser or configure an API key.",
            HttpStatusCode.Forbidden => "Nexus Mods refused the request. The selected file may require browser sign-in or may not allow direct downloads for this account.",
            HttpStatusCode.NotFound => "Nexus Mods could not find the requested game, mod, or file.",
            (HttpStatusCode)429 => "Nexus Mods rate limit was reached. Retry after the provided delay.",
            _ => $"Nexus Mods API request failed with HTTP {(int)response.StatusCode}."
        };

        throw new NexusModsException(message, response.StatusCode, retryAfter, body);
    }

    private static void ThrowIfGraphQlErrors(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
        {
            return;
        }

        var first = errors.EnumerateArray().First();
        var message = ReadString(first, "message") ?? "Nexus Mods GraphQL request failed.";
        var status = TryMapGraphQlStatus(first);
        throw new NexusModsException(message, status);
    }

    private static HttpStatusCode? TryMapGraphQlStatus(JsonElement error)
    {
        if (error.TryGetProperty("extensions", out var extensions))
        {
            var status = ReadInt(extensions, "status", "statusCode", "httpStatus");
            if (status is 401)
            {
                return HttpStatusCode.Unauthorized;
            }

            if (status is 429)
            {
                return (HttpStatusCode)429;
            }
        }

        var message = ReadString(error, "message") ?? string.Empty;
        if (message.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return HttpStatusCode.Unauthorized;
        }

        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase))
        {
            return (HttpStatusCode)429;
        }

        return null;
    }

    private static NexusModSummary ParseModSummary(JsonElement element)
    {
        var sizeInBytes = ReadLong(element, "fileSize");
        if (sizeInBytes is not null)
        {
            sizeInBytes *= 1024;
        }

        return new NexusModSummary(
            ReadInt(element, "modId") ?? 0,
            ReadString(element, "name") ?? string.Empty,
            ReadString(element, "summary") ?? string.Empty,
            ReadString(element, "version") ?? string.Empty,
            ReadString(element, "author") ?? string.Empty,
            ReadInt(element, "downloads") ?? 0,
            ReadInt(element, "endorsements") ?? 0,
            sizeInBytes,
            ReadString(element, "thumbnailUrl"),
            ReadDateTimeOffset(element, "updatedAt"),
            ReadBool(element, "directDownloadEnabled"),
            ReadString(element, "status") ?? string.Empty);
    }

    private static IReadOnlyList<NexusModRequirement> ParseRequirements(JsonElement mod)
    {
        if (!mod.TryGetProperty("modRequirements", out var requirements)
            || !requirements.TryGetProperty("nexusRequirements", out var nexusRequirements)
            || !nexusRequirements.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return nodes
            .EnumerateArray()
            .Select(node => new NexusModRequirement(
                ReadInt(node, "modId"),
                ReadString(node, "modName") ?? string.Empty,
                ReadInt(node, "gameId"),
                ReadBool(node, "externalRequirement"),
                ReadString(node, "notes"),
                ReadString(node, "url")))
            .ToArray();
    }

    private static NexusModFile ParseModFile(JsonElement element, int fallbackModId)
    {
        var size = ReadLong(element, "size_in_bytes", "sizeInBytes");
        if (size is null && ReadLong(element, "size_kb", "sizeKB") is { } sizeKb)
        {
            size = sizeKb * 1024;
        }

        return new NexusModFile(
            ReadInt(element, "file_id", "fileId") ?? 0,
            ReadInt(element, "mod_id", "modId") ?? fallbackModId,
            ReadString(element, "name") ?? string.Empty,
            ReadString(element, "file_name", "fileName") ?? ReadString(element, "name") ?? $"mod-{fallbackModId}.zip",
            ReadString(element, "category_name", "category") ?? string.Empty,
            ReadString(element, "version") ?? string.Empty,
            size,
            ReadString(element, "md5"),
            ReadInt(element, "uploaded_timestamp", "date") ?? 0,
            ReadBool(element, "is_primary", "primary"),
            ReadBool(element, "manager"),
            ReadString(element, "description", "file_description", "description_html"));
    }

    private static int FileCategoryRank(string category)
    {
        return category.ToUpperInvariant() switch
        {
            "MAIN" or "MAIN FILES" => 0,
            "UPDATE" or "UPDATE FILES" => 1,
            "OPTIONAL" or "OPTIONAL FILES" => 2,
            _ => 3
        };
    }

    private static string NormalizeGameDomain(string gameDomainName)
    {
        return string.IsNullOrWhiteSpace(gameDomainName)
            ? "kingdomcomedeliverance2"
            : gameDomainName.Trim();
    }

    private static string? ReadString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, params string[] propertyNames)
    {
        var value = ReadLong(element, propertyNames);
        return value is null ? null : checked((int)value.Value);
    }

    private static long? ReadLong(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var stringNumber))
            {
                return stringNumber;
            }
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number != 0;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (bool.TryParse(text, out var boolValue))
                {
                    return boolValue;
                }

                if (int.TryParse(text, out var intValue))
                {
                    return intValue != 0;
                }
            }
        }

        return false;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, params string[] propertyNames)
    {
        var text = ReadString(element, propertyNames);
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }
}
