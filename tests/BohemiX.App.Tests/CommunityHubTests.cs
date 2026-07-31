using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BohemiX.App.Community;
using BohemiX.App.Services;
using BohemiX.Core.Services;
using Serilog.Core;

namespace BohemiX.App.Tests;

public sealed class CommunityHubTests
{
    [Fact]
    public async Task LoadAsync_ReadsAndNormalizesRemoteCommunityContent()
    {
        const string json = """
            {
              "sponsorUrl": "https://example.com/sponsor",
              "specialThanks": [
                { "name": "  Alice  ", "description": "  Documentation  " }
              ],
              "sponsors": [
                { "name": "Bob", "tier": "Founder", "description": "Early supporter" }
              ]
            }
            """;
        var handler = new RecordingHandler(_ => JsonResponse(json));
        using var service = CreateService(handler, contentConfigured: true, feedbackConfigured: true);

        var result = await service.LoadAsync();

        Assert.True(result.IsContentConfigured);
        Assert.True(result.IsFeedbackConfigured);
        Assert.False(result.UsedFallback);
        Assert.False(result.RemoteLoadFailed);
        Assert.Equal("Alice", Assert.Single(result.Content.SpecialThanks).Name);
        Assert.Equal("Documentation", result.Content.SpecialThanks[0].Description);
        Assert.Equal("Founder", Assert.Single(result.Content.Sponsors).Tier);
        Assert.Equal("https://example.com/sponsor", result.Content.SponsorUrl.TrimEnd('/'));
    }

    [Fact]
    public async Task LoadAsync_UsesFallbackWithoutNetworkWhenEndpointIsUnconfigured()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network should not be used."));
        var fallback = new CommunityHubContent(
            [new CommunityPerson("Fallback credit")],
            [new CommunityPerson("Fallback sponsor")],
            string.Empty);
        var configuration = new CommunityHubConfiguration(null, null, fallback);
        using var service = new CommunityHubService(Logger.None, new HttpClient(handler), configuration);

        var result = await service.LoadAsync();

        Assert.False(result.IsContentConfigured);
        Assert.True(result.UsedFallback);
        Assert.Equal("Fallback credit", Assert.Single(result.Content.SpecialThanks).Name);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LoadAsync_QueriesAllAfdianPagesAndBuildsSponsorLists()
    {
        const string firstPage = """
            {
              "ec": 200,
              "em": "ok",
              "data": {
                "total_page": 2,
                "list": [
                  {
                    "all_sum_amount": "50.00",
                    "last_pay_time": 100,
                    "current_plan": { "name": "Long-term Support" },
                    "sponsor_plans": [],
                    "user": { "user_id": "alice-id", "name": " Alice " }
                  },
                  {
                    "all_sum_amount": "20.00",
                    "last_pay_time": 200,
                    "current_plan": { "name": "Supporter" },
                    "sponsor_plans": [],
                    "user": { "user_id": "bob-id", "name": "Bob" }
                  }
                ]
              }
            }
            """;
        const string secondPage = """
            {
              "ec": 200,
              "em": "ok",
              "data": {
                "total_page": 2,
                "list": [
                  {
                    "all_sum_amount": "25.00",
                    "last_pay_time": 250,
                    "current_plan": { "name": "Supporter Plus" },
                    "sponsor_plans": [],
                    "user": { "user_id": "bob-id", "name": "Bob" }
                  },
                  {
                    "all_sum_amount": "120.00",
                    "last_pay_time": 300,
                    "current_plan": { "name": "Founder" },
                    "sponsor_plans": [],
                    "user": { "user_id": "carol-id", "name": "Carol" }
                  }
                ]
              }
            }
            """;
        var handler = new RecordingHandler(_ => JsonResponse(firstPage));
        handler.ResponseFactory = _ => JsonResponse(handler.RequestCount == 1 ? firstPage : secondPage);
        var configuration = new CommunityHubConfiguration(
            null,
            null,
            new CommunityHubContent([], [], "https://afdian.com/a/Lume_Sky"),
            new AfdianCommunityConfiguration(
                new Uri("https://afdian.com/api/open/query-sponsor"),
                "https://afdian.com/a/Lume_Sky",
                "creator-id",
                "secret-token",
                100m,
                ["Long-term Support"],
                MaximumPages: 10));
        using var service = new CommunityHubService(Logger.None, new HttpClient(handler), configuration);

        var result = await service.LoadAsync();

        Assert.False(result.UsedFallback);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("Bob", Assert.Single(result.Content.Sponsors).Name);
        Assert.Equal(["Carol", "Alice"], result.Content.SpecialThanks.Select(person => person.Name));
        Assert.Empty(result.Content.Sponsors[0].Tier);
        Assert.Equal("https://afdian.com/a/Lume_Sky", result.Content.SponsorUrl.TrimEnd('/'));

        using var request = JsonDocument.Parse(handler.Bodies[0]);
        var root = request.RootElement;
        var parameters = root.GetProperty("params").GetString()!;
        var timestamp = root.GetProperty("ts").GetInt64();
        var expectedSignInput = $"secret-tokenparams{parameters}ts{timestamp}user_idcreator-id";
        var expectedSign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(expectedSignInput)))
            .ToLowerInvariant();
        Assert.Equal("creator-id", root.GetProperty("user_id").GetString());
        Assert.Equal("{\"page\":1}", parameters);
        Assert.Equal(expectedSign, root.GetProperty("sign").GetString());
    }

    [Fact]
    public async Task LoadAsync_UsesFallbackWhenAfdianRejectsTheRequest()
    {
        const string json = """
            { "ec": 400001, "em": "invalid credentials", "data": null }
            """;
        var handler = new RecordingHandler(_ => JsonResponse(json));
        var fallback = new CommunityHubContent(
            [new CommunityPerson("Fallback honor")],
            [new CommunityPerson("Fallback sponsor")],
            "https://afdian.com/a/Lume_Sky");
        var configuration = new CommunityHubConfiguration(
            null,
            null,
            fallback,
            new AfdianCommunityConfiguration(
                new Uri("https://afdian.com/api/open/query-sponsor"),
                "https://afdian.com/a/Lume_Sky",
                "creator-id",
                "bad-token",
                100m,
                []));
        using var service = new CommunityHubService(Logger.None, new HttpClient(handler), configuration);

        var result = await service.LoadAsync();

        Assert.True(result.UsedFallback);
        Assert.True(result.RemoteLoadFailed);
        Assert.True(result.IsContentConfigured);
        Assert.Equal("Fallback honor", Assert.Single(result.Content.SpecialThanks).Name);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task LoadAsync_ExpiresSpecialHonorAfterConfiguredDuration()
    {
        var now = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);
        var activePayTime = now.AddMonths(-3).AddDays(1).ToUnixTimeSeconds();
        var expiredPayTime = now.AddMonths(-3).AddDays(-1).ToUnixTimeSeconds();
        var json = $$"""
            {
              "ec": 200,
              "em": "ok",
              "data": {
                "total_page": 1,
                "list": [
                  {
                    "all_sum_amount": "60.00",
                    "last_pay_time": {{activePayTime}},
                    "current_plan": { "name": "这期神了！" },
                    "user": { "user_id": "active-id", "name": "Active" }
                  },
                  {
                    "all_sum_amount": "60.00",
                    "last_pay_time": {{expiredPayTime}},
                    "current_plan": { "name": "这期神了！" },
                    "user": { "user_id": "expired-id", "name": "Expired" }
                  }
                ]
              }
            }
            """;
        var handler = new RecordingHandler(_ => JsonResponse(json));
        var configuration = new CommunityHubConfiguration(
            null,
            null,
            new CommunityHubContent([], [], "https://afdian.com/a/Lume_Sky"),
            new AfdianCommunityConfiguration(
                new Uri("https://afdian.com/api/open/query-sponsor"),
                "https://afdian.com/a/Lume_Sky",
                "creator-id",
                "secret-token",
                0m,
                ["这期神了！"],
                SpecialHonorDurationMonths: 3));
        using var service = new CommunityHubService(
            Logger.None,
            new HttpClient(handler),
            configuration,
            _ => null,
            new FixedTimeProvider(now));

        var result = await service.LoadAsync();

        Assert.Equal("Expired", Assert.Single(result.Content.Sponsors).Name);
        Assert.Equal("Active", Assert.Single(result.Content.SpecialThanks).Name);
    }

    [Fact]
    public async Task LoadAsync_LoadsOnlySpecialHonorAvatarsAndFallsBackPerPerson()
    {
        const string json = """
            {
              "ec": 200,
              "em": "ok",
              "data": {
                "total_page": 1,
                "list": [
                  {
                    "all_sum_amount": "60.00",
                    "last_pay_time": 300,
                    "current_plan": { "name": "Honor" },
                    "user": {
                      "user_id": "portrait-id",
                      "name": "Portrait",
                      "avatar": "https://cdn.example.com/portrait.png"
                    }
                  },
                  {
                    "all_sum_amount": "60.00",
                    "last_pay_time": 200,
                    "current_plan": { "name": "Honor" },
                    "user": {
                      "user_id": "fallback-id",
                      "name": "Fallback",
                      "avatar": "https://cdn.example.com/missing.png"
                    }
                  },
                  {
                    "all_sum_amount": "25.00",
                    "last_pay_time": 100,
                    "current_plan": { "name": "Supporter" },
                    "user": {
                      "user_id": "sponsor-id",
                      "name": "Sponsor",
                      "avatar": "https://cdn.example.com/unused.png"
                    }
                  }
                ]
              }
            }
            """;
        var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/open/query-sponsor" => JsonResponse(json),
            "/portrait.png" => PngResponse(),
            "/missing.png" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var configuration = new CommunityHubConfiguration(
            null,
            null,
            new CommunityHubContent([], [], "https://afdian.com/a/Lume_Sky"),
            new AfdianCommunityConfiguration(
                new Uri("https://afdian.com/api/open/query-sponsor"),
                "https://afdian.com/a/Lume_Sky",
                "creator-id",
                "secret-token",
                0m,
                ["Honor"]));
        using var service = new CommunityHubService(Logger.None, new HttpClient(handler), configuration);

        var result = await service.LoadAsync();

        Assert.Equal("Sponsor", Assert.Single(result.Content.Sponsors).Name);
        Assert.Equal(["Fallback", "Portrait"], result.Content.SpecialThanks.Select(person => person.Name));
        var portrait = result.Content.SpecialThanks.Single(person => person.Name == "Portrait");
        Assert.Equal("https://cdn.example.com/portrait.png", portrait.AvatarUrl);
        Assert.Contains(handler.RequestUris, uri => uri.AbsolutePath == "/portrait.png");
        var fallback = result.Content.SpecialThanks.Single(person => person.Name == "Fallback");
        Assert.Equal("https://cdn.example.com/missing.png", fallback.AvatarUrl);
        Assert.False(fallback.HasAvatar);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.AbsolutePath == "/unused.png");
    }

    [Fact]
    public async Task SubmitEchoAsync_PostsOnlyTheDeclaredFeedbackPayload()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var service = CreateService(handler, contentConfigured: false, feedbackConfigured: true);
        var submission = new EchoCaveSubmission(
            "Suggestion",
            "Please add this feature.",
            "user@example.com",
            "1.2.3",
            "zh-CN");

        await service.SubmitEchoAsync(submission);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Contains("\"subject\":\"Suggestion\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"Please add this feature.\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"contact\":\"user@example.com\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"appVersion\":\"1.2.3\"", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"language\":\"zh-CN\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFeedbackEndpoint_PrefersValidEnvironmentEndpoint()
    {
        var endpoint = CommunityHubService.ResolveFeedbackEndpoint(
            "https://bohemix-echo-cave.bohemix-lume-sky.workers.dev",
            "BOHEMIX_ECHO_CAVE_ENDPOINT",
            name => name switch
            {
                "BOHEMIX_ECHO_CAVE_ENDPOINT" => "https://example.com/echo-cave",
                _ => null
            });

        Assert.Equal(new Uri("https://example.com/echo-cave"), endpoint);
    }

    [Fact]
    public void ResolveFeedbackEndpoint_InvalidEnvironmentEndpointFallsBackToConfiguredEndpoint()
    {
        var endpoint = CommunityHubService.ResolveFeedbackEndpoint(
            "https://bohemix-echo-cave.bohemix-lume-sky.workers.dev",
            "BOHEMIX_ECHO_CAVE_ENDPOINT",
            name => name switch
            {
                "BOHEMIX_ECHO_CAVE_ENDPOINT" => "http://not-secure.example.com/echo-cave",
                _ => null
            });

        Assert.Equal(new Uri("https://bohemix-echo-cave.bohemix-lume-sky.workers.dev"), endpoint);
    }

    [Fact]
    public async Task EchoCaveAccess_RequiresAnEligibleAfdianPlanBeforeSubmission()
    {
        const string sponsors = """
            {
              "ec": 200,
              "em": "ok",
              "data": {
                "total_page": 1,
                "list": [
                  {
                    "current_plan": { "name": "Echo Support" },
                    "user": { "user_id": "eligible-id", "name": "Eligible" }
                  },
                  {
                    "current_plan": { "name": "Basic Support" },
                    "user": { "user_id": "basic-id", "name": "Basic" }
                  }
                ]
              }
            }
            """;
        var handler = new RecordingHandler(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/open/query-sponsor" => JsonResponse(sponsors),
            "/echo" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var configuration = new CommunityHubConfiguration(
            null,
            new Uri("https://example.com/echo"),
            new CommunityHubContent([], [], "https://afdian.com/a/Lume_Sky"),
            new AfdianCommunityConfiguration(
                new Uri("https://afdian.com/api/open/query-sponsor"),
                "https://afdian.com/a/Lume_Sky",
                "creator-id",
                "secret-token",
                0m,
                [],
                EchoCaveEligiblePlanNames: ["Echo Support"]));
        using var service = new CommunityHubService(Logger.None, new HttpClient(handler), configuration);

        var eligible = await service.VerifyEchoCaveAccessAsync("eligible-id");
        var ineligible = await service.VerifyEchoCaveAccessAsync("basic-id");

        Assert.True(eligible.IsConfigured);
        Assert.True(eligible.IsEligible);
        Assert.Equal("Echo Support", eligible.PlanName);
        Assert.False(ineligible.IsEligible);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SubmitEchoAsync(
            new EchoCaveSubmission(
                "Suggestion",
                "Please add this feature.",
                "",
                "1.2.3",
                "en-US",
                "basic-id")));
        Assert.DoesNotContain(handler.RequestUris, uri => uri.AbsolutePath == "/echo");

        await service.SubmitEchoAsync(new EchoCaveSubmission(
            "Suggestion",
            "Please add this feature.",
            "",
            "1.2.3",
            "en-US",
            "eligible-id"));

        Assert.Equal("/echo", handler.RequestUris[^1].AbsolutePath);
        Assert.Contains("\"afdianSupporterIdentifier\":\"eligible-id\"", handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreSettingsViewModel_ValidatesAndSubmitsFeedback()
    {
        var service = new FakeCommunityHubService();
        var viewModel = new MoreSettingsViewModel(service, new EmptyPathPickerService(), Logger.None);
        await viewModel.EnsureInitializedAsync();

        Assert.Equal("Contributor", Assert.Single(viewModel.SpecialThanks).Name);
        Assert.False(viewModel.CanSubmitFeedback);

        viewModel.FeedbackSubject = "Feature request";
        viewModel.FeedbackMessage = "Please add a compact display mode.";
        viewModel.FeedbackContact = "tester@example.com";

        Assert.True(viewModel.CanSubmitFeedback);
        await viewModel.SubmitFeedbackCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastSubmission);
        Assert.Equal("Feature request", service.LastSubmission!.Subject);
        Assert.Empty(service.LastSubmission.Contact);
        Assert.True(viewModel.IsFeedbackSuccess);
        Assert.Empty(viewModel.FeedbackSubject);
        Assert.Empty(viewModel.FeedbackMessage);
    }

    [Fact]
    public async Task MoreSettingsViewModel_RequiresAfdianVerificationWhenAccessIsRestricted()
    {
        var service = new FakeCommunityHubService
        {
            IsEchoCaveAccessRestricted = true,
            EchoCaveAccessResult = new EchoCaveAccessResult(true, true, "Eligible", "Echo Support")
        };
        using var viewModel = new MoreSettingsViewModel(service, new EmptyPathPickerService(), Logger.None);
        await viewModel.EnsureInitializedAsync();
        viewModel.FeedbackSubject = "Feature request";
        viewModel.FeedbackMessage = "Please add a compact display mode.";

        Assert.False(viewModel.CanSubmitFeedback);
        Assert.True(viewModel.IsEchoCaveFeatureLocked);
        Assert.Equal(0.16, viewModel.EchoCaveContentOpacity);
        Assert.Equal("回声洞投稿", viewModel.EchoCaveTitle);

        viewModel.AfdianSupporterIdentifier = "eligible-id";
        await viewModel.VerifyEchoCaveAccessCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsEchoCaveAccessGranted);
        Assert.False(viewModel.IsEchoCaveFeatureLocked);
        Assert.Equal(1, viewModel.EchoCaveContentOpacity);
        Assert.True(viewModel.CanSubmitFeedback);
    }

    [Fact]
    public async Task MoreSettingsViewModel_DeactivateReleasesContentAndReloadsOnReturn()
    {
        var service = new FakeCommunityHubService();
        using var viewModel = new MoreSettingsViewModel(service, new EmptyPathPickerService(), Logger.None);

        await viewModel.ActivateVisualResourcesAsync();
        Assert.Single(viewModel.SpecialThanks);
        Assert.Single(viewModel.Sponsors);
        Assert.Equal(1, service.LoadCount);

        viewModel.DeactivateVisualResources();
        viewModel.DeactivateVisualResources();
        Assert.Empty(viewModel.SpecialThanks);
        Assert.Empty(viewModel.Sponsors);
        Assert.False(viewModel.IsEchoLineOneCaretVisible);

        await viewModel.ActivateVisualResourcesAsync();
        Assert.Single(viewModel.SpecialThanks);
        Assert.Single(viewModel.Sponsors);
        Assert.Equal(2, service.LoadCount);
    }

    [Fact]
    public void MoreSettingsViewModel_CustomWindowMediaUsesLiveBackdropBehindCards()
    {
        var viewModel = new MoreSettingsViewModel(
            new FakeCommunityHubService(),
            new EmptyPathPickerService(),
            Logger.None);
        var existingPath = typeof(CommunityHubTests).Assembly.Location;
        viewModel.IsEchoCaveAccessRestricted = false;

        Assert.True(viewModel.ShowBackdropReflection);

        viewModel.StaticBackgroundPath = existingPath;

        Assert.False(viewModel.ShowBackdropReflection);

        viewModel.StaticBackgroundPath = null;
        viewModel.DynamicBackgroundPath = existingPath;
        viewModel.UseDynamicBackground = true;

        Assert.False(viewModel.ShowBackdropReflection);
    }

    [Fact]
    public void MoreSettingsViewModel_AppearanceAccessFollowsSponsorTier()
    {
        var viewModel = new MoreSettingsViewModel(
            new FakeCommunityHubService(),
            new EmptyPathPickerService(),
            Logger.None);
        var existingPath = typeof(CommunityHubTests).Assembly.Location;
        viewModel.StaticBackgroundPath = existingPath;
        viewModel.CardBackgroundPath = existingPath;

        viewModel.IsEchoCaveAccessRestricted = true;

        Assert.False(viewModel.CanUseStaticAppearance);
        Assert.False(viewModel.CanUseDynamicAppearance);
        Assert.True(viewModel.IsSponsorFeatureLocked);
        Assert.False(viewModel.ShowStaticBackground);
        Assert.Null(viewModel.EffectiveCardBackgroundUri);
        viewModel.WindowBackgroundMode = 1;
        Assert.False(viewModel.UseDynamicBackground);

        viewModel.AppearanceAccessLevel = SponsorAppearanceAccessLevel.Static;

        Assert.True(viewModel.CanUseStaticAppearance);
        Assert.False(viewModel.CanUseDynamicAppearance);
        Assert.False(viewModel.IsSponsorFeatureLocked);
        Assert.True(viewModel.IsDynamicAppearanceUpgradeRequired);
        Assert.True(viewModel.ShowStaticBackground);
        Assert.Equal(existingPath, viewModel.EffectiveCardBackgroundUri);
        viewModel.WindowBackgroundMode = 1;
        Assert.False(viewModel.UseDynamicBackground);

        viewModel.AppearanceAccessLevel = SponsorAppearanceAccessLevel.Dynamic;

        Assert.True(viewModel.CanUseDynamicAppearance);
        Assert.False(viewModel.IsDynamicAppearanceUpgradeRequired);
        viewModel.WindowBackgroundMode = 1;
        Assert.True(viewModel.UseDynamicBackground);
    }

    [Fact]
    public void MoreSettingsViewModel_ExposesCompleteLegalNoticesWithValidLinks()
    {
        using var viewModel = new MoreSettingsViewModel(
            new FakeCommunityHubService(),
            new EmptyPathPickerService(),
            Logger.None);

        Assert.Equal(21, viewModel.LegalProjects.Count);
        Assert.Equal(viewModel.LegalProjects.Count, viewModel.LegalProjects.Select(item => item.Name).Distinct().Count());
        Assert.Contains(viewModel.LegalProjects, item => item.Name == "BohemiX" && item.License == "MIT");
        Assert.Contains(viewModel.LegalProjects, item => item.Name == "usvfs" && item.License.Contains("Section 7", StringComparison.Ordinal));
        Assert.Contains(viewModel.LegalProjects, item => item.Name == "Freesound / Kenney audio" && item.License == "CC0-1.0");
        Assert.All(viewModel.LegalProjects, item =>
        {
            Assert.True(Uri.TryCreate(item.ProjectUrl, UriKind.Absolute, out var projectUri));
            Assert.Equal(Uri.UriSchemeHttps, projectUri!.Scheme);
            Assert.True(Uri.TryCreate(item.LicenseUrl, UriKind.Absolute, out var licenseUri));
            Assert.Equal(Uri.UriSchemeHttps, licenseUri!.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(item.License));
        });

        viewModel.ApplyLanguage(useEnglish: true);

        Assert.Equal(21, viewModel.LegalProjects.Count);
        Assert.All(viewModel.LegalProjects, item => Assert.Equal("Project", item.ProjectButtonText));
        Assert.Equal("Echo Cave Submission", viewModel.EchoCaveTitle);
    }

    [Fact]
    public async Task MoreSettingsViewModel_BreadPlanUnlocksStaticAppearanceButNotEchoCave()
    {
        var service = new FakeCommunityHubService
        {
            IsEchoCaveAccessRestricted = true,
            EchoCaveAccessResult = new EchoCaveAccessResult(
                IsConfigured: true,
                IsEligible: false,
                SponsorName: "Bread Supporter",
                PlanName: "面包")
        };
        using var viewModel = new MoreSettingsViewModel(
            service,
            new EmptyPathPickerService(),
            Logger.None);
        await viewModel.EnsureInitializedAsync();

        viewModel.AfdianSupporterIdentifier = "bread-supporter";
        await viewModel.VerifyEchoCaveAccessCommand.ExecuteAsync(null);

        Assert.Equal(SponsorAppearanceAccessLevel.Static, viewModel.AppearanceAccessLevel);
        Assert.True(viewModel.CanUseStaticAppearance);
        Assert.False(viewModel.CanUseDynamicAppearance);
        Assert.False(viewModel.IsEchoCaveAccessGranted);
        Assert.True(viewModel.IsEchoCaveFeatureLocked);
        Assert.Equal(0.16, viewModel.EchoCaveContentOpacity);
    }

    [Fact]
    public async Task MoreSettingsViewModel_ChecksForGitHubUpdatesAndShowsFriendlyStatus()
    {
        var updateCheckService = new FakeUpdateCheckService(
            new ApplicationUpdateCheckResult(
                ApplicationUpdateAvailability.UpdateAvailable,
                "v0.7.0",
                "v0.8.0",
                new Uri("https://github.com/Luming-Sky/BohemiX/releases/tag/v0.8.0")));
        using var viewModel = new MoreSettingsViewModel(
            new FakeCommunityHubService(),
            new EmptyPathPickerService(),
            updateCheckService,
            Logger.None);

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("BohemiX", viewModel.ApplicationName);
        Assert.StartsWith("v", viewModel.ApplicationVersion, StringComparison.Ordinal);
        Assert.True(viewModel.HasUpdateCheckResult);
        Assert.True(viewModel.IsUpdateCheckSuccessful);
        Assert.Equal("发现新版本 v0.8.0。", viewModel.UpdateCheckStatusText);
        Assert.Equal(viewModel.ApplicationVersion, updateCheckService.LastCurrentVersion);
    }

    private static CommunityHubService CreateService(
        HttpMessageHandler handler,
        bool contentConfigured,
        bool feedbackConfigured)
    {
        var configuration = new CommunityHubConfiguration(
            contentConfigured ? new Uri("https://example.com/community.json") : null,
            feedbackConfigured ? new Uri("https://example.com/echo") : null,
            new CommunityHubContent([], [], string.Empty));
        return new CommunityHubService(Logger.None, new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage PngResponse()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAEAQH/69xk2QAAAABJRU5ErkJggg==";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Convert.FromBase64String(png))
        };
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } = responseFactory;

        public int RequestCount { get; private set; }

        public HttpMethod? LastMethod { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        public List<string> Bodies { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            if (request.RequestUri is not null)
            {
                RequestUris.Add(request.RequestUri);
            }
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                Bodies.Add(LastBody);
            }

            return ResponseFactory(request);
        }
    }

    private sealed class FakeCommunityHubService : ICommunityHubService
    {
        public EchoCaveSubmission? LastSubmission { get; private set; }

        public int LoadCount { get; private set; }

        public bool IsEchoCaveAccessRestricted { get; init; }

        public EchoCaveAccessResult EchoCaveAccessResult { get; init; } =
            new(false, false);

        public Task<CommunityHubLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            var content = new CommunityHubContent(
                [new CommunityPerson("Contributor", "Testing")],
                [new CommunityPerson("Sponsor", "Support", "Founder")],
                "https://example.com/sponsor");
            return Task.FromResult(new CommunityHubLoadResult(
                content,
                IsContentConfigured: true,
                IsFeedbackConfigured: true,
                UsedFallback: false,
                RemoteLoadFailed: false,
                IsEchoCaveAccessRestricted: IsEchoCaveAccessRestricted));
        }

        public Task<EchoCaveAccessResult> VerifyEchoCaveAccessAsync(
            string supporterIdentifier,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EchoCaveAccessResult);

        public Task SubmitEchoAsync(
            EchoCaveSubmission submission,
            CancellationToken cancellationToken = default)
        {
            LastSubmission = submission;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyPathPickerService : IGamePathPickerService
    {
        public Task<string?> PickGameExecutableAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickGameDirectoryAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSaveDirectoryAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickModPackageAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickModPackAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickAvatarAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickAppearanceMediaAsync(bool animated) => Task.FromResult<string?>(null);
    }

    private sealed class FakeUpdateCheckService(ApplicationUpdateCheckResult result)
        : IApplicationUpdateCheckService
    {
        public string? LastCurrentVersion { get; private set; }

        public Task<ApplicationUpdateCheckResult> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            LastCurrentVersion = currentVersion;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
