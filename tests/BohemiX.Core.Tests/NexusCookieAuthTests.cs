#pragma warning disable CS0618 // Compatibility cases intentionally exercise legacy account-scoped downloads.

using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class NexusCookieAuthTests
{
    [Fact]
    public void SecureCookieHeader_DisposeZerosNativeMemory()
    {
        var cookie = SecureCookieHeader.FromPlainText(
            "nexusmods_session=secret-cookie-value",
            retainNativeAllocationAfterDisposeForAudit: true);

        var address = cookie.DangerousGetAddressForSecurityAudit();
        Assert.NotEqual(IntPtr.Zero, address);
        Assert.Contains(cookie.DangerousCopyNativeBytesForSecurityAudit(), b => b != 0);

        cookie.Dispose();
        var bytesAfterDispose = cookie.DangerousCopyNativeBytesForSecurityAudit();

        Assert.All(bytesAfterDispose, value => Assert.Equal(0, value));
        cookie.ReleaseRetainedNativeAllocationForSecurityAudit();
    }

    [Theory]
    [InlineData("https://www.nexusmods.com/kingdomcomedeliverance2/mods/1")]
    [InlineData("https://api.nexusmods.com/v1/games/kingdomcomedeliverance2.json")]
    [InlineData("https://staticdelivery.nexusmods.com/mods/5851/images/1.zip")]
    public void AssertNexusModsUri_AllowsNexusDomains(string uri)
    {
        NexusCookieAuthService.AssertNexusModsUri(new Uri(uri));
    }

    [Theory]
    [InlineData("https://example.com/download")]
    [InlineData("https://nexusmods.com.evil.example/download")]
    public void AssertNexusModsUri_BlocksNonNexusDomains(string uri)
    {
        Assert.Throws<InvalidOperationException>(() => NexusCookieAuthService.AssertNexusModsUri(new Uri(uri)));
    }

    [Fact]
    public async Task CompositeCookieAuth_PrefersEmbeddedBrowserCookie()
    {
        var embedded = new StubEmbeddedBrowserAuthService("nexusmods_session=embedded-cookie");
        var browser = new NexusCookieAuthService(Logger.None, new DisabledAppSettingsService());
        var composite = new CompositeNexusCookieAuthService([embedded], browser, Logger.None);

        using var lease = await composite.TryCreateCookieHeaderLeaseAsync(new Uri("https://www.nexusmods.com/"));

        Assert.NotNull(lease);
        Assert.Equal("BohemiX embedded Edge", lease.BrowserName);
        Assert.Equal("nexusmods_session=embedded-cookie", lease.RevealHeaderValue());
        Assert.Equal(1, embedded.RequestCount);
    }

    [Fact]
    public async Task ModDownloader_DoesNotSendCookieToNonNexusDomains()
    {
        using var handler = new CapturingHttpMessageHandler();
        var cookieAuthService = new CapturingCookieAuthService("nexusmods_session=secret-cookie-value");
        using var downloader = CreateDownloader(cookieAuthService, handler);
        var destination = CreateTempDirectory();

        try
        {
            var request = new NexusModDownloadRequest(
                "kingdomcomedeliverance2",
                10,
                20,
                new Uri("https://example.com/mod.zip"),
                "mod.zip",
                destination,
                null);

            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(0, cookieAuthService.RequestCount);
            var captured = Assert.Single(handler.Requests);
            Assert.Equal("example.com", captured.Uri.Host);
            Assert.False(captured.HasCookieHeader);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_SendsCookieOnlyToNexusDomains()
    {
        using var handler = new CapturingHttpMessageHandler();
        var cookieAuthService = new CapturingCookieAuthService("nexusmods_session=secret-cookie-value");
        using var downloader = CreateDownloader(cookieAuthService, handler);
        var destination = CreateTempDirectory();

        try
        {
            var request = new NexusModDownloadRequest(
                "kingdomcomedeliverance2",
                10,
                20,
                new Uri("https://staticdelivery.nexusmods.com/mods/5851/files/mod.zip"),
                "mod.zip",
                destination,
                null);

            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(1, cookieAuthService.RequestCount);
            var captured = Assert.Single(handler.Requests);
            Assert.Equal("staticdelivery.nexusmods.com", captured.Uri.Host);
            Assert.True(captured.HasCookieHeader);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_FollowsRedirectWithoutLeakingCookieToNonNexusDomain()
    {
        var requests = new List<CapturedRequest>();
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            requests.Add(new CapturedRequest(
                request.RequestUri ?? new Uri("about:blank"),
                request.Headers.Contains("Cookie")));

            if (request.RequestUri?.Host == "staticdelivery.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers =
                    {
                        Location = new Uri("https://downloads.example-cdn.test/mod.zip")
                    }
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("redirected-download-bytes"))
            };
        });
        var cookieAuthService = new CapturingCookieAuthService("nexusmods_session=secret-cookie-value");
        using var downloader = CreateDownloader(cookieAuthService, handler);
        var destination = CreateTempDirectory();

        try
        {
            var request = new NexusModDownloadRequest(
                "kingdomcomedeliverance2",
                10,
                20,
                new Uri("https://staticdelivery.nexusmods.com/mods/5851/files/mod.zip"),
                "mod.zip",
                destination,
                null);

            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(1, cookieAuthService.RequestCount);
            Assert.Collection(
                requests,
                first =>
                {
                    Assert.Equal("staticdelivery.nexusmods.com", first.Uri.Host);
                    Assert.True(first.HasCookieHeader);
                },
                second =>
                {
                    Assert.Equal("downloads.example-cdn.test", second.Uri.Host);
                    Assert.False(second.HasCookieHeader);
                });
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task NexusModService_GetModFilesFallsBackToWebCookieAuth()
    {
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "api.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"message":"API key required"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("www.nexusmods.com", request.RequestUri?.Host);
            Assert.True(request.Headers.Contains("Cookie"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    <html data-game-id="5851">
                      <article data-file-id="222"
                               data-name="Main package"
                               data-file-name="main-package.zip"
                               data-category-name="MAIN FILES"
                               data-size-bytes="1048576"
                               data-md5="0123456789abcdef0123456789abcdef"
                               data-primary="true"
                               data-manager-download="true"></article>
                    </html>
                    """,
                    Encoding.UTF8,
                    "text/html")
            };
        });

        using var service = CreateNexusModService(new CapturingCookieAuthService("nexusmods_session=secret-cookie-value"), handler);

        var files = await service.GetModFilesAsync("kingdomcomedeliverance2", 111);

        var file = Assert.Single(files);
        Assert.Equal(222, file.FileId);
        Assert.Equal("Main package", file.Name);
        Assert.Equal("main-package.zip", file.FileName);
        Assert.Equal("MAIN FILES", file.Category);
        Assert.Equal(1048576, file.SizeInBytes);
        Assert.True(file.IsPrimary);
        Assert.True(file.IsManagerDownload);
    }

    [Fact]
    public async Task NexusModService_GetModFilesPrefersEmbeddedBrowserPageResolver()
    {
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "api.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"message\":\"API key required\"}", Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException("HTTP page fallback should not be used when the embedded browser loads the page.");
        });
        var resolver = new CapturingWebPageResolver(
            """
            <html data-game-id="5851">
              <article data-file-id="333" data-name="Browser package" data-file-name="browser-package.zip"
                       data-category-name="MAIN FILES" data-primary="true" data-manager-download="true"></article>
            </html>
            """);
        using var service = CreateNexusModService(
            new CapturingCookieAuthService("nexusmods_session=secret-cookie-value"),
            handler,
            webPageResolvers: [resolver]);

        var file = Assert.Single(await service.GetModFilesAsync("kingdomcomedeliverance2", 111));

        Assert.Equal(333, file.FileId);
        Assert.Equal(1, resolver.RequestCount);
        Assert.Equal(new Uri("https://www.nexusmods.com/kingdomcomedeliverance2/mods/111?tab=files"), resolver.Uri);
    }

    [Fact]
    public async Task NexusModService_GetDownloadLinkFallsBackToWebGenerateDownloadUrl()
    {
        var seenGenerateDownloadUrl = false;
        using var handler = new RoutingHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.Host == "api.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"message":"API key required"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("www.nexusmods.com", request.RequestUri?.Host);
            Assert.True(request.Headers.Contains("Cookie"));
            Assert.NotEqual(HttpMethod.Get, request.Method);

            seenGenerateDownloadUrl = true;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("GenerateDownloadUrl", request.RequestUri?.Query);
            Assert.Equal(new Uri("https://www.nexusmods.com/kingdomcomedeliverance2/mods/111?tab=files&file_id=222"), request.Headers.Referrer);
            Assert.Equal("fid=222&game_id=5851", await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"url":"https:\/\/staticdelivery.nexusmods.com\/mods\/5851\/files\/main-package.zip?token=abc"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var service = CreateNexusModService(new CapturingCookieAuthService("nexusmods_session=secret-cookie-value"), handler);

        var link = await service.GetDownloadLinkAsync("kingdomcomedeliverance2", 111, 222);

        Assert.True(seenGenerateDownloadUrl);
        Assert.Equal("staticdelivery.nexusmods.com", link.Uri.Host);
        Assert.Equal("/mods/5851/files/main-package.zip", link.Uri.AbsolutePath);
        Assert.Equal("CookieAuth", link.ShortName);
    }

    [Fact]
    public async Task NexusModService_GetDownloadLinkPrefersEntitledCdnHosts()
    {
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            Assert.Equal("api.nexusmods.com", request.RequestUri?.Host);
            var supporterOnly = request.RequestUri?.AbsolutePath.Contains("/223/", StringComparison.Ordinal) == true;
            var json = supporterOnly
                ? """
                  [
                    {"URI":"https://files.nexus-cdn.com/mods/5851/files/main-package.zip?token=standard","name":"Standard","short_name":"standard"},
                    {"URI":"https://supporter-files.nexus-cdn.com/mods/5851/files/main-package.zip?token=supporter","name":"Supporter","short_name":"supporter"}
                  ]
                  """
                : """
                  [
                    {"URI":"https://files.nexus-cdn.com/mods/5851/files/main-package.zip?token=standard","name":"Standard","short_name":"standard"},
                    {"URI":"https://supporter-files.nexus-cdn.com/mods/5851/files/main-package.zip?token=supporter","name":"Supporter","short_name":"supporter"},
                    {"URI":"https://premium-files.nexus-cdn.com/mods/5851/files/main-package.zip?token=premium","name":"Premium","short_name":"premium"}
                  ]
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        using var service = CreateNexusModService(
            new CapturingCookieAuthService("nexusmods_session=test-cookie"),
            handler);

        var premiumLink = await service.GetDownloadLinkAsync("kingdomcomedeliverance2", 111, 222);
        var supporterLink = await service.GetDownloadLinkAsync("kingdomcomedeliverance2", 111, 223);

        Assert.Equal("premium-files.nexus-cdn.com", premiumLink.Uri.Host);
        Assert.Equal("premium", premiumLink.ShortName);
        Assert.Equal("supporter-files.nexus-cdn.com", supporterLink.Uri.Host);
        Assert.Equal("supporter", supporterLink.ShortName);
    }

    [Fact]
    public async Task NexusModService_SearchModsRetriesWhenFirstResponseStreamDisconnects()
    {
        var requestCount = 0;
        using var handler = new RoutingHttpMessageHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new DisconnectingDownloadStream(Encoding.UTF8.GetBytes("{")))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":{"mods":{"nodes":[],"totalCount":0}}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var service = new NexusModService(
            new EmptyNexusApiKeyProvider(),
            new NullCookieAuthService(),
            new NexusModsOptions(),
            Logger.None,
            handler);

        var result = await service.SearchModsAsync(new NexusModSearchRequest(
            "kingdomcomedeliverance2",
            "combat",
            0,
            10));

        Assert.Equal(2, requestCount);
        Assert.Empty(result.Mods);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task NexusModService_GetDownloadLinkPrefersEmbeddedBrowserGenerateDownloadUrl()
    {
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "api.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"message":"API key required"}""", Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException("HTTP web download URL fallback should not be used when embedded browser resolves the URL.");
        });
        var resolver = new CapturingWebDownloadLinkResolver(
            """{"url":"https:\/\/staticdelivery.nexusmods.com\/mods\/5851\/files\/main-package.zip?token=embedded"}""");

        using var service = CreateNexusModService(
            new CapturingCookieAuthService("nexusmods_session=secret-cookie-value"),
            handler,
            [resolver]);

        var link = await service.GetDownloadLinkAsync("kingdomcomedeliverance2", 111, 222);

        Assert.Equal(1, resolver.RequestCount);
        Assert.Equal("kingdomcomedeliverance2", resolver.GameDomainName);
        Assert.Equal(111, resolver.ModId);
        Assert.Equal(222, resolver.FileId);
        Assert.Equal(5851, resolver.GameId);
        Assert.Equal(new Uri("https://www.nexusmods.com/kingdomcomedeliverance2/mods/111?tab=files&file_id=222"), resolver.Referrer);
        Assert.Equal("staticdelivery.nexusmods.com", link.Uri.Host);
        Assert.Equal("embedded", link.Uri.Query.TrimStart('?').Split('=').Last());
    }

    [Theory]
    [InlineData("""{"URI":"https:\/\/download.nexus-cdn.com\/mods\/5851\/files\/main-package.zip?token=embedded"}""", "download.nexus-cdn.com")]
    [InlineData("""{"url":"\/\/download.nxmcdn.com\/mods\/5851\/files\/main-package.zip?token=embedded"}""", "download.nxmcdn.com")]
    [InlineData("""<a class="btn" data-download-url="https://staticdelivery.nexusmods.com/mods/5851/files/main-package.zip?token=embedded">Download</a>""", "staticdelivery.nexusmods.com")]
    public async Task NexusModService_GetDownloadLinkParsesEmbeddedBrowserDownloadUrlVariants(
        string embeddedBody,
        string expectedHost)
    {
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "api.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"message":"API key required"}""", Encoding.UTF8, "application/json")
                };
            }

            throw new InvalidOperationException("HTTP web download URL fallback should not be used when embedded browser resolves the URL.");
        });
        var resolver = new CapturingWebDownloadLinkResolver(embeddedBody);

        using var service = CreateNexusModService(
            new CapturingCookieAuthService("nexusmods_session=secret-cookie-value"),
            handler,
            [resolver]);

        var link = await service.GetDownloadLinkAsync("kingdomcomedeliverance2", 111, 222);

        Assert.Equal(expectedHost, link.Uri.Host);
        Assert.Contains("token=embedded", link.Uri.Query);
    }

    [Fact]
    public async Task ModDownloader_EnqueueWithDependenciesUsesWebCookieFallbackWithoutApiKey()
    {
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "api.nexusmods.com")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"message":"API key required"}""", Encoding.UTF8, "application/json")
                };
            }

            Assert.Equal("www.nexusmods.com", request.RequestUri?.Host);
            Assert.True(request.Headers.Contains("Cookie"));
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"url":"https:\/\/staticdelivery.nexusmods.com\/mods\/5851\/files\/main-package.zip"}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var html = request.RequestUri?.Query.Contains("tab=files", StringComparison.OrdinalIgnoreCase) == true
                ? """
                  <html data-game-id="5851">
                    <article data-file-id="222"
                             data-name="Main package"
                             data-file-name="main-package.zip"
                             data-category-name="MAIN FILES"
                             data-primary="true"
                             data-manager-download="true"></article>
                  </html>
                  """
                : """
                  <html data-game-id="5851">
                    <h1>Cookie fallback mod</h1>
                    <meta name="description" content="Loaded from the Nexus web page">
                  </html>
                  """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
        });

        var cookieAuthService = new CapturingCookieAuthService("nexusmods_session=secret-cookie-value");
        using var nexusModService = CreateNexusModService(cookieAuthService, handler);
        using var downloader = new ModDownloader(
            nexusModService,
            cookieAuthService,
            new EmptyModCatalogService(),
            new ModDownloaderOptions(MaxConcurrentDownloads: 1, BufferSize: 4096),
            Logger.None,
            new CapturingHttpMessageHandler());
        var destination = CreateTempDirectory();

        try
        {
            var items = await downloader.EnqueueWithDependenciesAsync("kingdomcomedeliverance2", 111, destination);

            var item = Assert.Single(items);
            Assert.Equal(ModDownloadStatus.Pending, item.Status);
            Assert.Equal(111, item.Request.ModId);
            Assert.Equal(222, item.Request.FileId);
            Assert.Equal("main-package.zip", item.Request.FileName);
            Assert.Equal("staticdelivery.nexusmods.com", item.Request.DownloadUri.Host);
            Assert.True(cookieAuthService.RequestCount >= 3);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_ReconnectsWhenResponseBodyStopsProducingData()
    {
        var destination = CreateTempDirectory();
        var attemptCount = 0;
        var responseBody = Encoding.UTF8.GetBytes("completed-after-reconnect");
        using var handler = new RoutingHttpMessageHandler(_ =>
        {
            attemptCount++;
            HttpContent content = attemptCount == 1
                ? new StreamContent(new NeverRespondingDownloadStream())
                : new ByteArrayContent(responseBody);
            content.Headers.ContentLength = responseBody.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var downloader = new ModDownloader(
            new ThrowingNexusModService(),
            new NullCookieAuthService(),
            new EmptyModCatalogService(),
            new ModDownloaderOptions(
                MaxConcurrentDownloads: 1,
                BufferSize: 4096,
                DownloadInactivityTimeout: TimeSpan.FromMilliseconds(50)),
            Logger.None,
            handler);

        try
        {
            var request = CreateDownloadRequest(9, 90, destination, "reconnect.bin");
            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(2, attemptCount);
            Assert.Equal(responseBody, await File.ReadAllBytesAsync(result.FinalPath!));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_UsesHttp11AndResumesWhenResponseDisconnects()
    {
        var destination = CreateTempDirectory();
        var responseBody = Encoding.UTF8.GetBytes("completed-after-http2-disconnect");
        var firstChunk = responseBody[..10];
        var requests = new List<(Version Version, long? RangeStart)>();
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            requests.Add((request.Version, request.Headers.Range?.Ranges.Single().From));
            if (requests.Count == 1)
            {
                var content = new StreamContent(new DisconnectingDownloadStream(firstChunk));
                content.Headers.ContentLength = responseBody.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            var remaining = responseBody[firstChunk.Length..];
            var resumedContent = new ByteArrayContent(remaining);
            resumedContent.Headers.ContentLength = remaining.Length;
            resumedContent.Headers.ContentRange = new ContentRangeHeaderValue(
                firstChunk.Length,
                responseBody.Length - 1,
                responseBody.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = resumedContent };
        });
        using var downloader = CreateDownloader(new NullCookieAuthService(), handler);

        try
        {
            var request = CreateDownloadRequest(10, 100, destination, "resume-after-disconnect.bin");
            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(2, requests.Count);
            Assert.Equal(HttpVersion.Version11, requests[0].Version);
            Assert.Null(requests[0].RangeStart);
            Assert.Equal(HttpVersion.Version11, requests[1].Version);
            Assert.Equal(firstChunk.Length, requests[1].RangeStart);
            Assert.Equal(responseBody, await File.ReadAllBytesAsync(result.FinalPath!));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_ResumesWhenResponseEndsBeforeContentLength()
    {
        var destination = CreateTempDirectory();
        var responseBody = Encoding.UTF8.GetBytes("completed-after-short-response");
        var firstChunk = responseBody[..8];
        var attemptCount = 0;
        using var handler = new RoutingHttpMessageHandler(request =>
        {
            attemptCount++;
            if (attemptCount == 1)
            {
                var shortContent = new ByteArrayContent(firstChunk);
                shortContent.Headers.ContentLength = responseBody.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = shortContent };
            }

            Assert.Equal(firstChunk.Length, request.Headers.Range?.Ranges.Single().From);
            var remaining = responseBody[firstChunk.Length..];
            var resumedContent = new ByteArrayContent(remaining);
            resumedContent.Headers.ContentLength = remaining.Length;
            resumedContent.Headers.ContentRange = new ContentRangeHeaderValue(
                firstChunk.Length,
                responseBody.Length - 1,
                responseBody.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = resumedContent };
        });
        using var downloader = CreateDownloader(new NullCookieAuthService(), handler);

        try
        {
            var request = CreateDownloadRequest(11, 110, destination, "resume-after-short-response.bin");
            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(2, attemptCount);
            Assert.Equal(responseBody, await File.ReadAllBytesAsync(result.FinalPath!));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_ReplacesUnreadableCachedArchive()
    {
        var destination = CreateTempDirectory();
        var fileName = "replace-corrupt-cache.7z";
        var finalPath = Path.Combine(destination, fileName);
        await File.WriteAllTextAsync(finalPath, "not an archive");
        var archiveBytes = CreateZipBytes();
        var requestCount = 0;
        using var handler = new RoutingHttpMessageHandler(_ =>
        {
            requestCount++;
            var content = new ByteArrayContent(archiveBytes);
            content.Headers.ContentLength = archiveBytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var downloader = CreateDownloader(new NullCookieAuthService(), handler);

        try
        {
            var request = CreateDownloadRequest(12, 120, destination, fileName);
            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(1, requestCount);
            Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(finalPath));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_ClearCompletedQueueItemsRemovesSuccessfulDownload()
    {
        var destination = CreateTempDirectory();
        var archiveBytes = CreateZipBytes();
        using var handler = new RoutingHttpMessageHandler(_ =>
        {
            var content = new ByteArrayContent(archiveBytes);
            content.Headers.ContentLength = archiveBytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var downloader = CreateDownloader(new NullCookieAuthService(), handler);

        try
        {
            var request = CreateDownloadRequest(13, 130, destination, "completed.zip");
            var result = await downloader.DownloadAsync(request);

            Assert.True(result.Success);
            Assert.Equal(ModDownloadStatus.Completed, Assert.Single(downloader.Queue).Status);
            Assert.Equal(1, downloader.ClearCompletedQueueItems());
            Assert.Empty(downloader.Queue);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_PauseQueueKeepsTemporaryFileForResume()
    {
        var destination = CreateTempDirectory();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new RoutingHttpMessageHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingDownloadStream(firstChunkWritten))
            };
        });

        using var downloader = CreateDownloader(new NullCookieAuthService(), handler);
        var request = CreateDownloadRequest(1, 10, destination, "pause.zip");
        var downloadTask = downloader.DownloadAsync(request);

        await firstChunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, downloader.PauseQueue());
        var result = await downloadTask;

        Assert.False(result.Success);
        Assert.Equal(ModDownloadStatus.Paused, result.Status);
        Assert.True(File.Exists(Path.Combine(destination, "pause.zip.tmp")));
        Assert.Equal(ModDownloadStatus.Paused, Assert.Single(downloader.Queue).Status);

        Directory.Delete(destination, recursive: true);
    }

    [Fact]
    public async Task ModDownloader_CancelQueueDeletesTemporaryFile()
    {
        var destination = CreateTempDirectory();
        var firstChunkWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new RoutingHttpMessageHandler(request =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingDownloadStream(firstChunkWritten))
            };
        });

        using var downloader = CreateDownloader(new NullCookieAuthService(), handler);
        var request = CreateDownloadRequest(2, 20, destination, "cancel.zip");
        var downloadTask = downloader.DownloadAsync(request);

        await firstChunkWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, downloader.CancelQueue());
        var result = await downloadTask;

        Assert.False(result.Success);
        Assert.Equal(ModDownloadStatus.Canceled, result.Status);
        Assert.False(File.Exists(Path.Combine(destination, "cancel.zip.tmp")));
        Assert.Equal(ModDownloadStatus.Canceled, Assert.Single(downloader.Queue).Status);
        Assert.Equal(1, downloader.ClearCanceledQueueItems());
        Assert.Empty(downloader.Queue);

        Directory.Delete(destination, recursive: true);
    }

    [Fact]
    public async Task ModDownloader_ClearInactiveQueueItemsKeepsPendingItems()
    {
        var destination = CreateTempDirectory();
        using var handler = new CapturingHttpMessageHandler();
        using var downloader = new ModDownloader(
            new StubNexusModService(),
            new NullCookieAuthService(),
            new EmptyModCatalogService(),
            new ModDownloaderOptions(MaxConcurrentDownloads: 1, BufferSize: 4096),
            Logger.None,
            handler);

        await downloader.EnqueueWithDependenciesAsync("kingdomcomedeliverance2", 3, destination);
        Assert.Equal(0, downloader.ClearInactiveQueueItems());
        Assert.Equal(ModDownloadStatus.Pending, Assert.Single(downloader.Queue).Status);

        Assert.Equal(1, downloader.CancelQueue());
        Assert.Equal(1, downloader.ClearInactiveQueueItems());
        Assert.Empty(downloader.Queue);

        Directory.Delete(destination, recursive: true);
    }

    [Fact]
    public async Task ModDownloader_EnqueueFileWithDependenciesUsesSelectedFile()
    {
        var destination = CreateTempDirectory();
        using var downloader = new ModDownloader(
            new StubNexusModService(),
            new NullCookieAuthService(),
            new EmptyModCatalogService(),
            new ModDownloaderOptions(MaxConcurrentDownloads: 1, BufferSize: 4096),
            Logger.None,
            new CapturingHttpMessageHandler());
        var selectedFile = new NexusModFile(31, 3, "Optional package", "optional.zip", "OPTIONAL FILES", "2.0", null, null, 0, false, true);

        try
        {
            var items = await downloader.EnqueueFileWithDependenciesAsync(
                "kingdomcomedeliverance2",
                3,
                selectedFile,
                destination);

            var item = Assert.Single(items);
            Assert.Equal(31, item.Request.FileId);
            Assert.Equal("optional.zip", item.Request.FileName);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task ModDownloader_QueueAndBulkActionsAreIsolatedByCurrentPlayer()
    {
        var firstDestination = CreateTempDirectory();
        var secondDestination = CreateTempDirectory();
        var firstPlayer = CreatePlayer("Henry");
        var secondPlayer = CreatePlayer("Theresa");
        var playerContext = new MutablePlayerContext(firstPlayer);
        using var downloader = new ModDownloader(
            new StubNexusModService(),
            new NullCookieAuthService(),
            new EmptyModCatalogService(),
            new ModDownloaderOptions(MaxConcurrentDownloads: 1, BufferSize: 4096),
            Logger.None,
            playerContext,
            new CapturingHttpMessageHandler());

        try
        {
            var firstItem = Assert.Single(await downloader.EnqueueWithDependenciesAsync(
                "kingdomcomedeliverance2",
                3,
                firstDestination));
            Assert.Equal(firstPlayer.Id, firstItem.Request.PlayerId);

            playerContext.SetCurrentPlayer(secondPlayer);
            var secondItem = Assert.Single(await downloader.EnqueueWithDependenciesAsync(
                "kingdomcomedeliverance2",
                3,
                secondDestination));
            Assert.Equal(secondPlayer.Id, secondItem.Request.PlayerId);
            Assert.NotEqual(firstItem.QueueKey, secondItem.QueueKey);
            Assert.Equal(secondItem.QueueKey, Assert.Single(downloader.Queue).QueueKey);

            Assert.Equal(1, downloader.CancelQueue());
            Assert.Equal(ModDownloadStatus.Canceled, Assert.Single(downloader.Queue).Status);

            playerContext.SetCurrentPlayer(firstPlayer);
            Assert.Equal(firstItem.QueueKey, Assert.Single(downloader.Queue).QueueKey);
            Assert.Equal(ModDownloadStatus.Pending, Assert.Single(downloader.Queue).Status);
            Assert.Equal(0, downloader.ClearCanceledQueueItems());

            playerContext.SetCurrentPlayer(secondPlayer);
            Assert.Equal(1, downloader.ClearCanceledQueueItems());
            Assert.Empty(downloader.Queue);
        }
        finally
        {
            Directory.Delete(firstDestination, recursive: true);
            Directory.Delete(secondDestination, recursive: true);
        }
    }

    private static ModDownloader CreateDownloader(
        INexusCookieAuthService cookieAuthService,
        HttpMessageHandler handler)
    {
        return new ModDownloader(
            new ThrowingNexusModService(),
            cookieAuthService,
            new EmptyModCatalogService(),
            new ModDownloaderOptions(MaxConcurrentDownloads: 1, BufferSize: 4096),
            Logger.None,
            handler);
    }

    private static NexusModDownloadRequest CreateDownloadRequest(
        int modId,
        int fileId,
        string destination,
        string fileName)
    {
        return new NexusModDownloadRequest(
            "kingdomcomedeliverance2",
            modId,
            fileId,
            new Uri($"https://downloads.example.test/{fileName}"),
            fileName,
            destination,
            null);
    }

    private static NexusModService CreateNexusModService(
        INexusCookieAuthService cookieAuthService,
        HttpMessageHandler handler,
        IEnumerable<INexusWebDownloadLinkResolver>? webDownloadLinkResolvers = null,
        IEnumerable<INexusWebPageResolver>? webPageResolvers = null)
    {
        return new NexusModService(
            new EmptyNexusApiKeyProvider(),
            cookieAuthService,
            new NexusModsOptions(EnableLegacyBrowserCookieAuth: true),
            Logger.None,
            handler,
            webDownloadLinkResolvers,
            webPageResolvers);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bohemix-cookie-auth-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreateZipBytes()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("mod.manifest");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("valid archive payload");
        }

        return stream.ToArray();
    }

    private static PlayerProfile CreatePlayer(string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlayerProfile(
            Guid.NewGuid(),
            displayName,
            null,
            PlayerPlatformIds.Steam,
            PlayerProviderIds.Local,
            null,
            now,
            now,
            null,
            null,
            null,
            null,
            false,
            null,
            1);
    }

    private sealed class MutablePlayerContext : IPlayerContext
    {
        public MutablePlayerContext(PlayerProfile currentPlayer)
        {
            CurrentPlayer = currentPlayer;
        }

        public PlayerProfile? CurrentPlayer { get; private set; }

        public event EventHandler<PlayerChangedEventArgs>? CurrentPlayerChanged;

        public void SetCurrentPlayer(PlayerProfile player)
        {
            var previous = CurrentPlayer;
            CurrentPlayer = player;
            CurrentPlayerChanged?.Invoke(
                this,
                new PlayerChangedEventArgs(previous, player, PlayerChangeReason.Switched));
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler, IDisposable
    {
        private static readonly byte[] ResponseBody = Encoding.UTF8.GetBytes("download-bytes");

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri ?? new Uri("about:blank"),
                request.Headers.Contains("Cookie")));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ResponseBody)
            };

            response.Content.Headers.ContentLength = ResponseBody.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class RoutingHttpMessageHandler : HttpMessageHandler, IDisposable
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> route;

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        {
            this.route = request => Task.FromResult(route(request));
        }

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> route)
        {
            this.route = route;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return route(request);
        }
    }

    private sealed class BlockingDownloadStream(TaskCompletionSource firstChunkWritten) : Stream
    {
        private readonly byte[] firstChunk = Encoding.UTF8.GetBytes("partial-download-bytes");
        private bool hasReadFirstChunk;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!hasReadFirstChunk)
            {
                hasReadFirstChunk = true;
                var read = Math.Min(count, firstChunk.Length);
                firstChunk.AsSpan(0, read).CopyTo(buffer.AsSpan(offset, read));
                firstChunkWritten.SetResult();
                return read;
            }

            try
            {
                Task.Delay(Timeout.InfiniteTimeSpan).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!hasReadFirstChunk)
            {
                hasReadFirstChunk = true;
                firstChunk.AsSpan(0, Math.Min(buffer.Length, firstChunk.Length)).CopyTo(buffer.Span);
                firstChunkWritten.SetResult();
                return Math.Min(buffer.Length, firstChunk.Length);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NeverRespondingDownloadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DisconnectingDownloadStream(byte[] firstChunk) : Stream
    {
        private bool hasReadFirstChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (hasReadFirstChunk)
            {
                return ValueTask.FromException<int>(new IOException("Simulated unexpected EOF."));
            }

            hasReadFirstChunk = true;
            var read = Math.Min(buffer.Length, firstChunk.Length);
            firstChunk.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CapturedRequest(Uri Uri, bool HasCookieHeader);

    private sealed class EmptyNexusApiKeyProvider : INexusApiKeyProvider
    {
        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }

    private sealed class DisabledAppSettingsService : IAppSettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AppSettings(null, null, string.Empty, false));
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubEmbeddedBrowserAuthService(string cookieHeader) : INexusEmbeddedBrowserAuthService
    {
        public int RequestCount { get; private set; }

        public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusCookieAuthProbeResult(true, "BohemiX embedded Edge", null, "Detected.", []));
        }

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
            Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult<NexusCookieAuthLease?>(
                new NexusCookieAuthLease("BohemiX embedded Edge", SecureCookieHeader.FromPlainText(cookieHeader)));
        }

        public void ClearSession()
        {
        }
    }

    private sealed class CapturingCookieAuthService(string cookieHeader) : INexusCookieAuthService
    {
        public int RequestCount { get; private set; }

        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusCookieAuthProbeResult(true, "Test", null, "Nexus browser sign-in detected from Test.", []));
        }

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
            Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult<NexusCookieAuthLease?>(
                new NexusCookieAuthLease("Test", SecureCookieHeader.FromPlainText(cookieHeader)));
        }

        public void ClearSession()
        {
        }
    }

    private sealed class NullCookieAuthService : INexusCookieAuthService
    {
        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusCookieAuthProbeResult(false, null, "Unavailable", "Unavailable.", []));
        }

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
            Uri requestUri,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NexusCookieAuthLease?>(null);
        }

        public void ClearSession()
        {
        }
    }

    private sealed class CapturingWebDownloadLinkResolver(string body) : INexusWebDownloadLinkResolver
    {
        public int RequestCount { get; private set; }

        public string? GameDomainName { get; private set; }

        public int ModId { get; private set; }

        public int FileId { get; private set; }

        public int GameId { get; private set; }

        public Uri? Referrer { get; private set; }

        public Task<string?> TryGenerateDownloadUrlAsync(
            string gameDomainName,
            int modId,
            int fileId,
            int gameId,
            Uri referrer,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            GameDomainName = gameDomainName;
            ModId = modId;
            FileId = fileId;
            GameId = gameId;
            Referrer = referrer;
            return Task.FromResult<string?>(body);
        }
    }

    private sealed class CapturingWebPageResolver(string html) : INexusWebPageResolver
    {
        public int RequestCount { get; private set; }

        public Uri? Uri { get; private set; }

        public Task<string?> TryLoadPageHtmlAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            Uri = uri;
            return Task.FromResult<string?>(html);
        }
    }

    private sealed class EmptyModCatalogService : IModCatalogService
    {
        public Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ModManifest>>([]);
        }

        public Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveModEnabledStatesAsync(
            IReadOnlyDictionary<string, bool> enabledStates,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateModMetadataAsync(
            ModManifest mod,
            string displayName,
            string version,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNexusModService : INexusModService
    {
        public Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NexusModSearchResult> SearchModsAsync(
            NexusModSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NexusModDetails> GetModDetailsAsync(
            string gameDomainName,
            int modId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(
            string gameDomainName,
            int modId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NexusModFile> GetPreferredFileAsync(
            string gameDomainName,
            int modId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<NexusDownloadLink> GetDownloadLinkAsync(
            string gameDomainName,
            int modId,
            int fileId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubNexusModService : INexusModService
    {
        public Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusGameInfo(5851, gameDomainName, "KCD2", 1));
        }

        public Task<NexusModSearchResult> SearchModsAsync(
            NexusModSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusModSearchResult([], 0));
        }

        public Task<NexusModDetails> GetModDetailsAsync(
            string gameDomainName,
            int modId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusModDetails(
                modId,
                5851,
                gameDomainName,
                "Queued test mod",
                string.Empty,
                string.Empty,
                "1.0",
                "Test",
                0,
                0,
                true,
                "published",
                [],
                []));
        }

        public Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(
            string gameDomainName,
            int modId,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<NexusModFile> files =
            [
                new NexusModFile(30, modId, "Main", "queued.zip", "MAIN FILES", "1.0", null, null, 0, true, true)
            ];
            return Task.FromResult(files);
        }

        public Task<NexusModFile> GetPreferredFileAsync(
            string gameDomainName,
            int modId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusModFile(30, modId, "Main", "queued.zip", "MAIN FILES", "1.0", null, null, 0, true, true));
        }

        public Task<NexusDownloadLink> GetDownloadLinkAsync(
            string gameDomainName,
            int modId,
            int fileId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NexusDownloadLink(new Uri("https://downloads.example.test/queued.zip"), "Main", "Test"));
        }
    }
}

#pragma warning restore CS0618
