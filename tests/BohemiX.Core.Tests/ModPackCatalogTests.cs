using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class ModPackCatalogTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidatorAcceptsOfficialNexusAndSteamCollections()
    {
        var document = CreateDocument(
            CreateNexusEntry(),
            CreateSteamEntry());

        var errors = ModPackCatalogValidator.Validate(document);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidatorAcceptsOfficialSteamCdnThumbnailHost()
    {
        var document = CreateDocument(CreateSteamEntry() with
        {
            ThumbnailUrl = "https://cdn.steamusercontent.com/ugc/example/preview.jpg",
            ThumbnailIsRepresentative = true
        });

        Assert.Empty(ModPackCatalogValidator.Validate(document));
    }

    [Theory]
    [InlineData("http://www.nexusmods.com/kingdomcomedeliverance2/collections/starter")]
    [InlineData("https://example.com/kingdomcomedeliverance2/collections/starter")]
    [InlineData("https://www.nexusmods.com/kingdomcomedeliverance2/collections/starter/download")]
    [InlineData("https://www.nexusmods.com/kingdomcomedeliverance2/collections/other")]
    public void ValidatorRejectsUnsafeOrMismatchedNexusUrls(string url)
    {
        var document = CreateDocument(CreateNexusEntry() with { OfficialPageUrl = url });

        Assert.NotEmpty(ModPackCatalogValidator.Validate(document));
    }

    [Fact]
    public void ValidatorRejectsDuplicateIdsAndNonPublicRights()
    {
        var first = CreateNexusEntry();
        var second = CreateSteamEntry() with
        {
            Id = first.Id,
            IsPublic = false,
            IsActive = false
        };

        var errors = ModPackCatalogValidator.Validate(CreateDocument(first, second));

        Assert.Contains(errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("active, public", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SignatureVerificationRejectsTamperedCatalog()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Serialize(CreateDocument(CreateNexusEntry()));
        var signature = key.SignData(catalog, HashAlgorithmName.SHA256);

        Assert.True(ModPackCatalogService.VerifySignature(catalog, signature, key.ExportSubjectPublicKeyInfoPem()));

        catalog[^2] ^= 1;
        Assert.False(ModPackCatalogService.VerifySignature(catalog, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public async Task ServiceCachesVerifiedRemoteAndFallsBackToLastKnownGood()
    {
        Directory.CreateDirectory(tempRoot);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Serialize(CreateDocument(CreateNexusEntry()));
        var signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(catalog, HashAlgorithmName.SHA256)));
        var options = CreateOptions(key.ExportSubjectPublicKeyInfoPem());
        var paths = new TestApplicationPathService(tempRoot);

        using (var client = new HttpClient(new CatalogHandler(catalog, signature)))
        {
            var service = new ModPackCatalogService(paths, options, client, new LoggerConfiguration().CreateLogger());
            var remote = await service.GetCatalogAsync();
            Assert.Equal(ModPackCatalogSource.Remote, remote.Source);
            Assert.Single(remote.Entries);
        }

        using (var client = new HttpClient(new FailingHandler()))
        {
            var service = new ModPackCatalogService(paths, options, client, new LoggerConfiguration().CreateLogger());
            var fallback = await service.GetCatalogAsync();
            Assert.Equal(ModPackCatalogSource.LastKnownGood, fallback.Source);
            Assert.Single(fallback.Entries);
            Assert.NotNull(fallback.Warning);
        }
    }

    [Fact]
    public async Task ServiceWithoutRemoteConfigurationUsesEmbeddedCatalogInsteadOfCache()
    {
        Directory.CreateDirectory(tempRoot);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = Serialize(CreateDocument(CreateNexusEntry()));
        var signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(catalog, HashAlgorithmName.SHA256)));
        var paths = new TestApplicationPathService(tempRoot);

        using (var client = new HttpClient(new CatalogHandler(catalog, signature)))
        {
            var remoteService = new ModPackCatalogService(
                paths,
                CreateOptions(key.ExportSubjectPublicKeyInfoPem()),
                client,
                new LoggerConfiguration().CreateLogger());
            Assert.Equal(ModPackCatalogSource.Remote, (await remoteService.GetCatalogAsync()).Source);
        }

        using (var client = new HttpClient(new FailingHandler()))
        {
            var embeddedService = new ModPackCatalogService(
                paths,
                new ModPackCatalogOptions(PublicKeyPem: key.ExportSubjectPublicKeyInfoPem()),
                client,
                new LoggerConfiguration().CreateLogger());
            var result = await embeddedService.GetCatalogAsync();

            Assert.Equal(ModPackCatalogSource.Embedded, result.Source);
            Assert.Equal(398, result.Entries.Count);
        }
    }

    [Fact]
    public async Task ServiceRejectsRedirectThenUsesEmbeddedCatalog()
    {
        Directory.CreateDirectory(tempRoot);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key.ExportSubjectPublicKeyInfoPem()) with { MaximumResponseBytes = 1024 * 1024 };
        using var client = new HttpClient(new RedirectHandler());
        var service = new ModPackCatalogService(
            new TestApplicationPathService(tempRoot),
            options,
            client,
            new LoggerConfiguration().CreateLogger());

        var result = await service.GetCatalogAsync();

        Assert.Equal(ModPackCatalogSource.Embedded, result.Source);
        Assert.Equal(398, result.Entries.Count);
        Assert.Equal(56, result.Entries.Count(entry => entry.Platform == ModPackPlatform.NexusCollection));
        Assert.Equal(342, result.Entries.Count(entry => entry.Platform == ModPackPlatform.SteamWorkshopCollection));
        Assert.Contains(result.Entries, entry => entry.Id == "nexus-henry-from-skalitz");
        Assert.Contains(result.Entries, entry => entry.Id == "nexus-immersive-and-difficult");
        Assert.Contains(result.Entries, entry => entry.Id == "nexus-4k-overhaul-better-gameplay");
        Assert.Contains(result.Entries, entry => entry.Id == "steam-3753996635");
        Assert.Equal(43, result.Entries.Count(entry => entry.ContainsAdultContent));
        Assert.Equal(156, result.Entries.Count(entry => entry.ThumbnailIsRepresentative));
        Assert.Equal(
            7,
            result.Entries.Count(entry => entry.Platform == ModPackPlatform.SteamWorkshopCollection
                && string.IsNullOrWhiteSpace(entry.ThumbnailUrl)));
        Assert.Contains("Redirects", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LimitedReaderRejectsOversizedPayload()
    {
        await using var stream = new MemoryStream(new byte[33]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModPackCatalogService.ReadLimitedAsync(stream, 32, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static ModPackCatalogOptions CreateOptions(string publicKeyPem) => new(
        "https://catalog.bohemix.example/catalog.json",
        "https://catalog.bohemix.example/catalog.json.sig",
        publicKeyPem);

    private static byte[] Serialize(ModPackCatalogDocument document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

    private static ModPackCatalogDocument CreateDocument(params ModPackCatalogEntry[] entries) =>
        new(1, new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero), entries);

    private static ModPackCatalogEntry CreateNexusEntry() => new(
        "reviewed-starter",
        "Reviewed Starter",
        "A reviewed public collection.",
        ModPackPlatform.NexusCollection,
        "starter",
        "https://www.nexusmods.com/kingdomcomedeliverance2/collections/starter",
        "Curator",
        ModPackCategory.EssentialsTools,
        ["starter", "tools"],
        12,
        "KCD2 1.4",
        new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
        ThumbnailUrl: "https://media.nexusmods.com/a/b/t/med/reviewed-starter.webp");

    private static ModPackCatalogEntry CreateSteamEntry() => new(
        "reviewed-steam-pack",
        "Reviewed Steam Pack",
        "A reviewed public Steam collection.",
        ModPackPlatform.SteamWorkshopCollection,
        "1234567890",
        "https://steamcommunity.com/sharedfiles/filedetails/?id=1234567890",
        "Steam curator",
        ModPackCategory.VanillaPlus,
        ["vanilla"],
        8,
        "KCD2 1.4",
        new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
        ThumbnailUrl: "https://images.steamusercontent.com/ugc/example/preview/");

    private sealed class TestApplicationPathService(string root) : IApplicationPathService
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

    private sealed class CatalogHandler(byte[] catalog, byte[] signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal)
                ? signature
                : catalog;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Network unavailable.");
    }

    private sealed class RedirectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://other.example/catalog.json") },
                RequestMessage = request
            });
    }
}
