using System.IO.Compression;
using System.Net;
using System.Text;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class NexusCollectionServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MetadataQueryUsesCurrentNexusCollectionSchema()
    {
        Assert.DoesNotContain("visible", NexusCollectionService.LatestPublishedRevisionQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("fileSize", NexusCollectionService.LatestPublishedRevisionQuery, StringComparison.Ordinal);
        Assert.Contains("collectionStatus", NexusCollectionService.LatestPublishedRevisionQuery, StringComparison.Ordinal);
        Assert.Contains("assetsSizeBytes", NexusCollectionService.LatestPublishedRevisionQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataParserAcceptsListedPublishedRevisionAndNewSizeField()
    {
        using var service = new NexusCollectionService(
            new TestPathService(tempRoot),
            new EmptyApiKeyProvider(),
            new EmptyCookieAuthService(),
            new LoggerConfiguration().CreateLogger(),
            new JsonHandler("""
                {
                  "data": {
                    "collection": {
                      "id": 344239,
                      "slug": "tye91z",
                      "name": "Henry From Skalitz",
                      "summary": "Public collection",
                      "collectionStatus": "listed",
                      "game": { "domainName": "kingdomcomedeliverance2" },
                      "latestPublishedRevision": {
                        "id": 740488,
                        "revisionNumber": 41,
                        "revisionStatus": "published",
                        "status": "published",
                        "adultContent": false,
                        "downloadLink": "/v2/collections/344239/revisions/740488/download_link",
                        "assetsSizeBytes": "8944",
                        "totalSize": "3497549569",
                        "modCount": 73
                      }
                    }
                  }
                }
                """));

        var revision = await service.GetLatestPublishedRevisionAsync("tye91z", false);

        Assert.Equal(41, revision.RevisionNumber);
        Assert.Equal(8944, revision.FileSizeInBytes);
        Assert.Equal(3497549569, revision.TotalSizeInBytes);
        Assert.Equal(73, revision.ModCount);
    }

    [Fact]
    public async Task PackageDownloadUsesBrowserCookiesWithoutApiKey()
    {
        var cookieAuth = new TrackingCookieAuthService("nexusmods_session=test-session");
        var handler = new CollectionPackageHandler(CreateCollectionArchive());
        using var service = new NexusCollectionService(
            new TestPathService(tempRoot),
            new EmptyApiKeyProvider(),
            cookieAuth,
            new LoggerConfiguration().CreateLogger(),
            handler);

        var package = await service.DownloadAndReadPackageAsync(CreateRevision(), Guid.NewGuid());

        Assert.Empty(package.Manifest.Items);
        Assert.Equal(1, cookieAuth.LeaseRequests);
        Assert.Equal("nexusmods_session=test-session", handler.Cookie);
        Assert.Null(handler.ApiKey);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task PackageDownloadSendsApiKeyAndReadsOfficialArchive()
    {
        var handler = new CollectionPackageHandler(CreateCollectionArchive());
        using var service = new NexusCollectionService(
            new TestPathService(tempRoot),
            new StaticApiKeyProvider("test-api-key"),
            new EmptyCookieAuthService(),
            new LoggerConfiguration().CreateLogger(),
            handler);

        var package = await service.DownloadAndReadPackageAsync(CreateRevision(), Guid.NewGuid());

        Assert.Empty(package.Manifest.Items);
        Assert.Equal("test-api-key", handler.ApiKey);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public void ManifestParserPinsNexusFilesAndClassifiesUnsupportedSources()
    {
        var manifest = NexusCollectionService.ParseManifest(Encoding.UTF8.GetBytes("""
            {
              "info": { "name": "Test", "author": "Curator", "domainName": "kingdomcomedeliverance2" },
              "mods": [
                { "name": "Pinned", "phase": 1, "source": { "type": "nexus", "modId": 12, "fileId": 34, "md5": "AABB" } },
                { "name": "Duplicate", "source": { "type": "nexus", "modId": 12, "fileId": 34 } },
                { "name": "Manual", "optional": true, "source": { "type": "manual", "url": "https://www.nexusmods.com/" } }
              ],
              "loadOrder": ["nexus-12-34"]
            }
            """));

        Assert.Equal("kingdomcomedeliverance2", manifest.Info.DomainName);
        Assert.Equal(2, manifest.Items.Count);
        var nexus = Assert.Single(manifest.Items, item => item.Source == ModPackInstallItemSource.Nexus);
        Assert.Equal(12, nexus.ModId);
        Assert.Equal(34, nexus.FileId);
        Assert.Equal(1, nexus.Phase);
        Assert.Contains("nexus-12-34", manifest.LoadOrder);
    }

    [Fact]
    public async Task PackageReaderExtractsOnlyDeclaredBundledDirectory()
    {
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, "collection.zip");
        var bundledDirectory = Path.Combine(tempRoot, "bundled");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "collection.json", """
                { "info": { "domainName": "kingdomcomedeliverance2" }, "mods": [] }
                """);
            WriteEntry(archive, "bundled/inside.zip", "bundle-data");
            WriteEntry(archive, "unrelated/ignored.txt", "ignored");
        }

        var manifest = await NexusCollectionService.ReadPackageAsync(archivePath, bundledDirectory, CancellationToken.None);

        Assert.Empty(manifest.Items);
        Assert.True(File.Exists(Path.Combine(bundledDirectory, "inside.zip")));
        Assert.False(File.Exists(Path.Combine(tempRoot, "unrelated", "ignored.txt")));
    }

    [Fact]
    public async Task PackageReaderRejectsBundledPathTraversal()
    {
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "collection.json", """
                { "info": { "domainName": "kingdomcomedeliverance2" }, "mods": [] }
                """);
            WriteEntry(archive, "bundled/../../outside.txt", "unsafe");
        }

        await Assert.ThrowsAsync<NexusModsException>(() => NexusCollectionService.ReadPackageAsync(
            archivePath,
            Path.Combine(tempRoot, "bundled"),
            CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(tempRoot, "outside.txt")));
    }

    [Fact]
    public async Task PackageReaderWrapsUnsupportedArchiveFormat()
    {
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, "invalid.package");
        await File.WriteAllTextAsync(archivePath, "not an archive");

        var exception = await Assert.ThrowsAsync<NexusModsException>(() => NexusCollectionService.ReadPackageAsync(
            archivePath,
            Path.Combine(tempRoot, "bundled"),
            CancellationToken.None));

        Assert.Contains("supported compressed archive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static NexusCollectionRevisionInfo CreateRevision() => new(
        344239,
        740488,
        "tye91z",
        "Henry From Skalitz",
        "Public collection",
        "kingdomcomedeliverance2",
        41,
        "published",
        false,
        "/v2/collections/344239/revisions/740488/download_link",
        1024,
        1024,
        0);

    private static byte[] CreateCollectionArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "collection.json", """
                { "info": { "domainName": "kingdomcomedeliverance2" }, "mods": [] }
                """);
        }

        return stream.ToArray();
    }

    private sealed class EmptyApiKeyProvider : INexusApiKeyProvider
    {
        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    }

    private sealed class StaticApiKeyProvider(string apiKey) : INexusApiKeyProvider
    {
        public ValueTask<string?> GetApiKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(apiKey);
    }

    private sealed class EmptyCookieAuthService : INexusCookieAuthService
    {
        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusCookieAuthProbeResult(false, null, "none", "none", []));

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(Uri requestUri, CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusCookieAuthLease?>(null);

        public void ClearSession()
        {
        }
    }

    private sealed class TrackingCookieAuthService(string? cookieHeader = null) : INexusCookieAuthService
    {
        public int LeaseRequests { get; private set; }

        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new NexusCookieAuthProbeResult(true, "test", null, "test", []));

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(Uri requestUri, CancellationToken cancellationToken = default)
        {
            LeaseRequests++;
            return Task.FromResult(string.IsNullOrWhiteSpace(cookieHeader)
                ? null
                : new NexusCookieAuthLease(
                    "test",
                    SecureCookieHeader.FromPlainText(cookieHeader)));
        }

        public void ClearSession()
        {
        }
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
    }

    private sealed class RequestCountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class CollectionPackageHandler(byte[] archive) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? ApiKey { get; private set; }

        public string? Cookie { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                ApiKey = request.Headers.TryGetValues("apikey", out var values) ? values.Single() : null;
                Cookie = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"download_link\":{\"URI\":\"https://premium-files.nexus-cdn.com/collection.package\"}}",
                        Encoding.UTF8,
                        "application/json"),
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive),
                RequestMessage = request
            });
        }
    }

    private sealed class TestPathService(string root) : IApplicationPathService
    {
        public ApplicationPaths GetPaths() => new(
            root,
            Path.Combine(root, "bohemix.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "Mods"),
            Path.Combine(root, "native"),
            Path.Combine(root, "tracker"),
            Path.Combine(root, "tracker", "events.jsonl"),
            Path.Combine(root, "tracker", "rules.json"),
            CacheDirectory: Path.Combine(root, "cache"));

        public GlobalApplicationPaths GetGlobalPaths() => throw new NotSupportedException();
    }
}
