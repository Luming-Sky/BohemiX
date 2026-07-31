using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed partial class SteamGameNewsService : IGameNewsService, IDisposable
{
    internal const int Kcd2AppId = 1771300;
    internal const int MaximumCachedItems = 8;
    private const string OfficialFeedName = "steam_community_announcements";
    private const string CacheFileName = "game-news-1771300.json";
    private static readonly Uri FeedUri = new(
        $"https://api.steampowered.com/ISteamNews/GetNewsForApp/v0002/?appid={Kcd2AppId}&count=8&maxlength=0&format=json");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> AllowedArticleHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamstore-a.akamaihd.net",
        "store.steampowered.com",
        "steamcommunity.com",
        "www.steamcommunity.com"
    };
    private static readonly HashSet<string> AllowedImageHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "clan.cloudflare.steamstatic.com",
        "shared.cloudflare.steamstatic.com",
        "cdn.cloudflare.steamstatic.com",
        "steamcdn-a.akamaihd.net"
    };
    private const int MaximumImageBytes = 6 * 1024 * 1024;

    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    public SteamGameNewsService(IApplicationPathService applicationPathService, ILogger logger)
        : this(applicationPathService, logger, new HttpClientHandler())
    {
    }

    internal SteamGameNewsService(
        IApplicationPathService applicationPathService,
        ILogger logger,
        HttpMessageHandler handler)
    {
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<SteamGameNewsService>();
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = RequestTimeout
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.1");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<GameNewsFeed?> LoadCachedAsync(CancellationToken cancellationToken = default)
    {
        var cachePath = GetCachePath();
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var cached = await JsonSerializer.DeserializeAsync(
                stream,
                GameNewsJsonContext.Default.GameNewsFeed,
                cancellationToken).ConfigureAwait(false);
            return cached is null
                ? null
                : cached with { IsFromCache = true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Warning(ex, "Unable to read the cached KCD2 news feed from {CachePath}", cachePath);
            return null;
        }
    }

    public async Task<GameNewsFeed> RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                FeedUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var parsedItems = ParseNewsItems(document.RootElement);
            var items = await CacheImagesAsync(parsedItems, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                throw new InvalidDataException("Steam returned no valid official KCD2 announcements.");
            }

            var feed = new GameNewsFeed(items, DateTimeOffset.UtcNow);
            await WriteCacheAsync(feed, cancellationToken).ConfigureAwait(false);
            return feed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or InvalidDataException)
        {
            logger.Warning(ex, "Unable to refresh the official KCD2 Steam news feed");
            var cached = await LoadCachedAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached with { IsStale = true };
            }

            throw;
        }
    }

    internal static IReadOnlyList<GameNewsItem> ParseNewsItems(JsonElement root)
    {
        if (!root.TryGetProperty("appnews", out var appNews)
            || !appNews.TryGetProperty("newsitems", out var newsItems)
            || newsItems.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<GameNewsItem>(MaximumCachedItems);
        foreach (var item in newsItems.EnumerateArray())
        {
            if (parsed.Count >= MaximumCachedItems)
            {
                break;
            }

            if (!TryReadString(item, "feedname", out var feedName)
                || !string.Equals(feedName, OfficialFeedName, StringComparison.OrdinalIgnoreCase)
                || !TryReadString(item, "gid", out var id)
                || !TryReadString(item, "title", out var title)
                || !TryReadString(item, "url", out var url)
                || !IsAllowedArticleUrl(url))
            {
                continue;
            }

            var summary = TryReadString(item, "contents", out var contents)
                ? NormalizeSummary(contents)
                : string.Empty;
            var imageUrl = TryReadString(item, "contents", out var imageContents)
                ? TryExtractImageUrl(imageContents)
                : null;
            var author = TryReadString(item, "author", out var authorValue)
                ? authorValue
                : "Warhorse Studios";
            var source = TryReadString(item, "feedlabel", out var sourceValue)
                ? sourceValue
                : "Steam Community";
            var publishedAt = item.TryGetProperty("date", out var date)
                              && date.TryGetInt64(out var unixTime)
                ? DateTimeOffset.FromUnixTimeSeconds(unixTime)
                : DateTimeOffset.MinValue;

            parsed.Add(new GameNewsItem(
                id.Trim(),
                title.Trim(),
                summary,
                url.Trim(),
                author.Trim(),
                source.Trim(),
                publishedAt,
                imageUrl));
        }

        return parsed
            .OrderByDescending(item => item.PublishedAtUtc)
            .ToArray();
    }

    internal static bool IsAllowedArticleUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && AllowedArticleHosts.Contains(uri.Host);
    }

    internal static bool IsAllowedImageUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && AllowedImageHosts.Contains(uri.Host);
    }

    internal static string? TryExtractImageUrl(string value)
    {
        var match = SteamImageRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        var candidate = match.Groups["url"].Value.Trim();
        const string clanImageToken = "{STEAM_CLAN_IMAGE}/";
        if (candidate.StartsWith(clanImageToken, StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://clan.cloudflare.steamstatic.com/images/"
                        + candidate[clanImageToken.Length..];
        }

        return IsAllowedImageUrl(candidate) ? candidate : null;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static string NormalizeSummary(string value)
    {
        var decoded = WebUtility.HtmlDecode(value).Trim();
        decoded = BbCodeRegex().Replace(decoded, " ");
        var builder = new StringBuilder(Math.Min(decoded.Length, 240));
        var pendingSpace = false;
        foreach (var character in decoded)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length >= 220)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private async Task<IReadOnlyList<GameNewsItem>> CacheImagesAsync(
        IReadOnlyList<GameNewsItem> items,
        CancellationToken cancellationToken)
    {
        var rows = new List<GameNewsItem>(items.Count);
        foreach (var item in items)
        {
            rows.Add(item with
            {
                ImagePath = await TryCacheImageAsync(item, cancellationToken).ConfigureAwait(false)
            });
        }

        return rows;
    }

    private async Task<string?> TryCacheImageAsync(GameNewsItem item, CancellationToken cancellationToken)
    {
        if (!IsAllowedImageUrl(item.ImageUrl))
        {
            return null;
        }

        var extension = Path.GetExtension(new Uri(item.ImageUrl!).AbsolutePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            extension = ".jpg";
        }

        var imageDirectory = Path.Combine(
            applicationPathService.GetPaths().DataDirectory,
            "cache",
            "game-news-images");
        Directory.CreateDirectory(imageDirectory);
        var imageFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Id))) + extension;
        var imagePath = Path.Combine(imageDirectory, imageFileName);
        if (File.Exists(imagePath) && new FileInfo(imagePath).Length > 0)
        {
            return imagePath;
        }

        var temporaryPath = imagePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await httpClient.GetAsync(
                item.ImageUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || response.Content.Headers.ContentLength > MaximumImageBytes)
            {
                return null;
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                var totalBytes = 0;
                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    totalBytes += bytesRead;
                    if (totalBytes > MaximumImageBytes)
                    {
                        return null;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, imagePath, overwrite: true);
            return imagePath;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or TaskCanceledException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            logger.Debug(ex, "Unable to cache Steam news image {ImageUrl}", item.ImageUrl);
            return null;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [GeneratedRegex("\\[img\\s+src=\\\"(?<url>[^\\\"]+)\\\"\\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamImageRegex();

    [GeneratedRegex("\\[[^\\]]+\\]", RegexOptions.CultureInvariant)]
    private static partial Regex BbCodeRegex();

    private async Task WriteCacheAsync(GameNewsFeed feed, CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath();
        var cacheDirectory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDirectory);
        var temporaryPath = cachePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8192,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    feed,
                    GameNewsJsonContext.Default.GameNewsFeed,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetCachePath() => Path.Combine(
        applicationPathService.GetPaths().DataDirectory,
        "cache",
        CacheFileName);

    public void Dispose() => httpClient.Dispose();
}
