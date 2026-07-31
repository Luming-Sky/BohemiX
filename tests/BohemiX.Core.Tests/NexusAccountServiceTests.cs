using System.Net;
using System.Text;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class NexusAccountServiceTests
{
    [Fact]
    public void ParseSsoMessage_ReadsConnectionTokenWithoutExposingAnApiKey()
    {
        var result = NexusAccountService.ParseSsoMessage(
            """{"success":true,"data":{"connection_token":"resume-token"},"error":null}""");

        Assert.True(result.Success);
        Assert.Equal("resume-token", result.ConnectionToken);
        Assert.Null(result.ApiKey);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParseSsoMessage_ReadsApprovedApiKey()
    {
        var result = NexusAccountService.ParseSsoMessage(
            """{"success":true,"data":{"api_key":"approved-key"},"error":null}""");

        Assert.True(result.Success);
        Assert.Equal("approved-key", result.ApiKey);
    }

    [Fact]
    public void ParseSsoMessage_PreservesServerError()
    {
        var result = NexusAccountService.ParseSsoMessage(
            """{"success":false,"data":null,"error":"application_not_found"}""");

        Assert.False(result.Success);
        Assert.Equal("application_not_found", result.Error);
    }

    [Fact]
    public void BuildAuthorizationUri_IncludesRequestAndApplicationSlug()
    {
        var requestId = Guid.Parse("7d0473b5-a8bb-48b5-8370-a0942de9b781");

        var uri = NexusAccountService.BuildAuthorizationUri(
            new NexusSsoOptions(ApplicationSlug: "bohemix preview"),
            requestId);

        Assert.Equal("www.nexusmods.com", uri.Host);
        Assert.Contains("id=7d0473b5-a8bb-48b5-8370-a0942de9b781", uri.Query);
        Assert.Contains("application=bohemix%20preview", uri.Query);
    }

    [Fact]
    public void ParseValidatedAccount_ReadsIdentityAndMembership()
    {
        var account = NexusAccountService.ParseValidatedAccount(
            """{"user_id":42,"name":"Henry","is_premium":true,"is_supporter":false}""");

        Assert.Equal(42, account.UserId);
        Assert.Equal("Henry", account.Name);
        Assert.True(account.IsPremium);
        Assert.False(account.IsSupporter);
    }

    [Fact]
    public async Task ValidateAccountAsync_SendsApiKeyAndParsesAccount()
    {
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal("https://api.nexusmods.com/v1/users/validate.json", request.RequestUri?.AbsoluteUri);
            Assert.Equal("secret-key", Assert.Single(request.Headers.GetValues("apikey")));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"user_id":"77","name":"Theresa","is_premium":false,"is_supporter":true}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var store = CreateStore(CreateTemporaryCredentialPath());
        using var service = new NexusAccountService(
            store,
            new NexusSsoOptions(),
            Logger.None,
            new HttpClient(handler),
            (_, _) => Task.CompletedTask);

        var account = await service.ValidateAccountAsync("secret-key");

        Assert.Equal(77, account.UserId);
        Assert.Equal("Theresa", account.Name);
        Assert.True(account.IsSupporter);
    }

    [Fact]
    public async Task BindWithApiKeyAsync_ValidatesAndPersistsTheBoundAccount()
    {
        var credentialPath = CreateTemporaryCredentialPath();
        var directory = Path.GetDirectoryName(credentialPath)!;
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new DelegateHandler(request =>
            {
                Assert.Equal("personal-api-key", Assert.Single(request.Headers.GetValues("apikey")));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"user_id":81,"name":"Katherine","is_premium":false,"is_supporter":true}""",
                        Encoding.UTF8,
                        "application/json")
                };
            });
            var store = CreateStore(credentialPath);
            using var service = new NexusAccountService(
                store,
                new NexusSsoOptions(),
                Logger.None,
                new HttpClient(handler),
                (_, _) => Task.CompletedTask);

            var account = await service.BindWithApiKeyAsync("personal-api-key");
            var stored = await store.LoadAsync();

            Assert.Equal("Katherine", account.Name);
            Assert.NotNull(stored);
            Assert.Equal("personal-api-key", stored.ApiKey);
            Assert.Equal(account, stored.ToBinding());
        }
        finally
        {
            if (File.Exists(credentialPath))
            {
                File.Delete(credentialPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task GetBoundAccountAsync_RecognizesEmbeddedBrowserSession()
    {
        var embeddedBrowser = new StubEmbeddedBrowserAuthService(isSignedIn: true);
        using var service = CreateServiceWithEmbeddedBrowser(embeddedBrowser);

        var account = await service.GetBoundAccountAsync();

        Assert.NotNull(account);
        Assert.Equal(0, account.UserId);
        Assert.Equal("Nexus Mods 账号", account.Name);
        Assert.Equal(1, embeddedBrowser.ProbeCalls);
    }

    [Fact]
    public async Task BindAsync_TrustsTheEmbeddedBrowserLoginValidation()
    {
        var embeddedBrowser = new StubEmbeddedBrowserAuthService(isSignedIn: false)
        {
            SignInResult = true
        };
        using var service = CreateServiceWithEmbeddedBrowser(embeddedBrowser);

        var account = await service.BindAsync();

        Assert.Equal(0, account.UserId);
        Assert.Equal(1, embeddedBrowser.SignInCalls);
        Assert.Equal(0, embeddedBrowser.ProbeCalls);
    }

    [Fact]
    public async Task CredentialStore_EncryptsRoundTripsAndClearsBinding()
    {
        var credentialPath = CreateTemporaryCredentialPath();
        var directory = Path.GetDirectoryName(credentialPath)!;
        Directory.CreateDirectory(directory);
        try
        {
            var store = CreateStore(credentialPath);
            var account = new NexusAccountBinding(123, "Hans", true, false);

            await store.SaveAsync("api-key-that-must-not-be-plain-text", account);

            var persistedBytes = await File.ReadAllBytesAsync(credentialPath);
            Assert.DoesNotContain("api-key-that-must-not-be-plain-text", Encoding.UTF8.GetString(persistedBytes));
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal("api-key-that-must-not-be-plain-text", loaded.ApiKey);
            Assert.Equal(account, loaded.ToBinding());

            await store.ClearAsync();
            Assert.False(File.Exists(credentialPath));
        }
        finally
        {
            if (File.Exists(credentialPath))
            {
                File.Delete(credentialPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public async Task ApiKeyProvider_PrefersBoundAccountCredential()
    {
        var credentialPath = CreateTemporaryCredentialPath();
        var directory = Path.GetDirectoryName(credentialPath)!;
        Directory.CreateDirectory(directory);
        try
        {
            var store = CreateStore(credentialPath);
            await store.SaveAsync("bound-account-key", new NexusAccountBinding(5, "Katherine", false, false));
            var environmentProvider = new EnvironmentNexusApiKeyProvider();
            environmentProvider.SetApiKey("manual-session-key");
            var provider = new NexusApiKeyProvider(store, environmentProvider);

            var key = await provider.GetApiKeyAsync();

            Assert.Equal("bound-account-key", key);
        }
        finally
        {
            if (File.Exists(credentialPath))
            {
                File.Delete(credentialPath);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    private static NexusAccountCredentialStore CreateStore(string path) => new(path, Logger.None);

    private static NexusAccountService CreateServiceWithEmbeddedBrowser(
        INexusEmbeddedBrowserAuthService embeddedBrowserAuthService)
    {
        return new NexusAccountService(
            CreateStore(CreateTemporaryCredentialPath()),
            new NexusSsoOptions(),
            Logger.None,
            new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))),
            (_, _) => Task.CompletedTask,
            embeddedBrowserAuthService);
    }

    private static string CreateTemporaryCredentialPath()
    {
        return Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"), "nexus-account.dat");
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(callback(request));
        }
    }

    private sealed class StubEmbeddedBrowserAuthService(bool isSignedIn) : INexusEmbeddedBrowserAuthService
    {
        public bool SignInResult { get; init; }

        public int SignInCalls { get; private set; }

        public int ProbeCalls { get; private set; }

        public Task<bool> SignInAsync(CancellationToken cancellationToken = default)
        {
            SignInCalls++;
            isSignedIn = SignInResult;
            return Task.FromResult(SignInResult);
        }

        public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return Task.FromResult(new NexusCookieAuthProbeResult(
                isSignedIn,
                isSignedIn ? "Embedded Edge" : null,
                isSignedIn ? null : "NO_SESSION",
                isSignedIn ? "Signed in" : "Not signed in",
                []));
        }

        public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
            Uri requestUri,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<NexusCookieAuthLease?>(null);

        public void ClearSession() => isSignedIn = false;
    }
}
