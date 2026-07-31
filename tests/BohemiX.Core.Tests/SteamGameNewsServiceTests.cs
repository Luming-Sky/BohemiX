using System.Net;
using System.Text;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class SteamGameNewsServiceTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Refresh_FiltersToOfficialAnnouncementsAndWritesCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var service = CreateService(root, new StaticResponseHandler(HttpStatusCode.OK, FeedPayload));

            var feed = await service.RefreshAsync();
            var cached = await service.LoadCachedAsync();

            var item = Assert.Single(feed.Items);
            Assert.Equal("Official announcement", item.Title);
            Assert.Equal("https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/official", item.Url);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_783_695_613), item.PublishedAtUtc);
            Assert.NotNull(cached);
            Assert.True(cached!.IsFromCache);
            Assert.Equal(item, Assert.Single(cached.Items));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Refresh_WhenNetworkFails_ReturnsStaleCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using (var online = CreateService(root, new StaticResponseHandler(HttpStatusCode.OK, FeedPayload)))
            {
                await online.RefreshAsync();
            }

            using var offline = CreateService(root, new ThrowingHandler());
            var feed = await offline.RefreshAsync();

            Assert.True(feed.IsFromCache);
            Assert.True(feed.IsStale);
            Assert.Single(feed.Items);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Refresh_WhenNetworkFailsWithoutCache_Throws()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var service = CreateService(root, new ThrowingHandler());
            await Assert.ThrowsAsync<HttpRequestException>(() => service.RefreshAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadCached_WhenCacheIsCorrupt_ReturnsNull()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cacheDirectory = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "game-news-1771300.json"), "{not-json");
            using var service = CreateService(root, new ThrowingHandler());

            Assert.Null(await service.LoadCachedAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://steamcommunity.com/games/1771300/announcements/detail/1", true)]
    [InlineData("https://store.steampowered.com/news/app/1771300/view/1", true)]
    [InlineData("http://steamcommunity.com/games/1771300/announcements/detail/1", false)]
    [InlineData("https://example.com/news/1", false)]
    public void IsAllowedArticleUrl_RestrictsSchemeAndHost(string url, bool expected)
    {
        Assert.Equal(expected, SteamGameNewsService.IsAllowedArticleUrl(url));
    }

    [Theory]
    [InlineData("https://clan.cloudflare.steamstatic.com/images/44983656/update.jpg", true)]
    [InlineData("https://shared.cloudflare.steamstatic.com/store_item_assets/update.png", true)]
    [InlineData("http://clan.cloudflare.steamstatic.com/images/update.jpg", false)]
    [InlineData("https://example.com/update.jpg", false)]
    public void IsAllowedImageUrl_RestrictsSchemeAndHost(string url, bool expected)
    {
        Assert.Equal(expected, SteamGameNewsService.IsAllowedImageUrl(url));
    }

    [Fact]
    public void TryExtractImageUrl_ExpandsSteamClanImageMacro()
    {
        const string contents = "[img src=\"{STEAM_CLAN_IMAGE}/44983656/update.jpg\"]";

        var result = SteamGameNewsService.TryExtractImageUrl(contents);

        Assert.Equal(
            "https://clan.cloudflare.steamstatic.com/images/44983656/update.jpg",
            result);
    }

    [Fact]
    public async Task Refresh_CleansSummaryAndCachesOfficialAnnouncementImage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var imageBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            using var service = CreateService(root, new FeedAndImageResponseHandler(FeedWithImagePayload, imageBytes));

            var feed = await service.RefreshAsync();

            var item = Assert.Single(feed.Items);
            Assert.Equal("Patch & notes are live.", item.Summary);
            Assert.Equal(
                "https://clan.cloudflare.steamstatic.com/images/44983656/update.png",
                item.ImageUrl);
            Assert.NotNull(item.ImagePath);
            Assert.True(File.Exists(item.ImagePath));
            Assert.Equal(imageBytes, await File.ReadAllBytesAsync(item.ImagePath!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SteamGameNewsService CreateService(string root, HttpMessageHandler handler) =>
        new(new ApplicationPathService(root), Logger, handler);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bohemix-news-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private const string FeedPayload = """
        {
          "appnews": {
            "newsitems": [
              {
                "gid": "official",
                "title": "Official announcement",
                "url": "https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/official",
                "author": "Warhorse.T",
                "contents": "A   useful\n official update.",
                "feedlabel": "Community Announcements",
                "feedname": "steam_community_announcements",
                "date": 1783695613
              },
              {
                "gid": "syndicated",
                "title": "Syndicated article",
                "url": "https://example.com/article",
                "author": "Publisher",
                "contents": "Not official.",
                "feedlabel": "External",
                "feedname": "externalpost",
                "date": 1783695614
              }
            ]
          }
        }
        """;

    private const string FeedWithImagePayload = """
        {
          "appnews": {
            "newsitems": [
              {
                "gid": "official-with-image",
                "title": "Official update with image",
                "url": "https://steamcommunity.com/games/1771300/announcements/detail/2",
                "author": "Warhorse.T",
                "contents": "[img src=\"{STEAM_CLAN_IMAGE}/44983656/update.png\"][b]Patch[/b] &amp; [url=https://steamcommunity.com]notes[/url] are live.",
                "feedlabel": "Community Announcements",
                "feedname": "steam_community_announcements",
                "date": 1783695615
              }
            ]
          }
        }
        """;

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Offline");
        }
    }

    private sealed class FeedAndImageResponseHandler(string feedPayload, byte[] imageBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Equals("api.steampowered.com", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(feedPayload, Encoding.UTF8, "application/json")
                });
            }

            var content = new ByteArrayContent(imageBytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }
}
