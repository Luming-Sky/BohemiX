using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;

namespace BohemiX.App.Community;

public sealed class CommunityHubService : ICommunityHubService, IDisposable
{
    private const int AvatarDecodeWidth = 160;
    private const string ConfigurationAssetUri = "avares://BohemiX.App/Assets/Community/community-hub.json";
    private const int MaxPeoplePerSection = 200;
    private const int AfdianSuccessCode = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly ILogger logger;
    private readonly CommunityHubConfiguration? suppliedConfiguration;
    private readonly Func<string, string?> environmentVariableReader;
    private readonly TimeProvider timeProvider;
    private CommunityHubConfiguration? loadedConfiguration;

    public CommunityHubService(ILogger logger)
        : this(
            logger,
            new HttpClient { Timeout = TimeSpan.FromSeconds(12) },
            suppliedConfiguration: null,
            Environment.GetEnvironmentVariable,
            TimeProvider.System)
    {
    }

    public CommunityHubService(
        ILogger logger,
        HttpClient httpClient,
        CommunityHubConfiguration? suppliedConfiguration)
        : this(
            logger,
            httpClient,
            suppliedConfiguration,
            Environment.GetEnvironmentVariable,
            TimeProvider.System)
    {
    }

    public CommunityHubService(
        ILogger logger,
        HttpClient httpClient,
        CommunityHubConfiguration? suppliedConfiguration,
        Func<string, string?> environmentVariableReader,
        TimeProvider timeProvider)
    {
        this.logger = logger.ForContext<CommunityHubService>();
        this.httpClient = httpClient;
        this.suppliedConfiguration = suppliedConfiguration;
        this.environmentVariableReader = environmentVariableReader;
        this.timeProvider = timeProvider;
    }

    public async Task<CommunityHubLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (configuration.ContentEndpoint is null && configuration.Afdian is { HasCredentials: true } afdian)
        {
            try
            {
                var content = await LoadAfdianContentAsync(afdian, cancellationToken);
                return new CommunityHubLoadResult(
                    content,
                    IsContentConfigured: true,
                    IsFeedbackConfigured: configuration.FeedbackEndpoint is not null,
                    UsedFallback: false,
                    RemoteLoadFailed: false,
                    IsEchoCaveAccessRestricted: afdian.HasEchoCaveEligibilityRestriction);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.Warning("Timed out while loading Afdian sponsor content");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                logger.Warning(ex, "Unable to load Afdian sponsor content");
            }

            return new CommunityHubLoadResult(
                configuration.FallbackContent,
                IsContentConfigured: true,
                IsFeedbackConfigured: configuration.FeedbackEndpoint is not null,
                UsedFallback: true,
                RemoteLoadFailed: true,
                IsEchoCaveAccessRestricted: afdian.HasEchoCaveEligibilityRestriction);
        }

        if (configuration.ContentEndpoint is null)
        {
            return new CommunityHubLoadResult(
                configuration.FallbackContent,
                IsContentConfigured: false,
                IsFeedbackConfigured: configuration.FeedbackEndpoint is not null,
                UsedFallback: true,
                RemoteLoadFailed: false,
                IsEchoCaveAccessRestricted: configuration.Afdian?.HasEchoCaveEligibilityRestriction == true);
        }

        try
        {
            using var response = await httpClient.GetAsync(
                configuration.ContentEndpoint,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await JsonSerializer.DeserializeAsync<CommunityHubContentDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            var content = NormalizeContent(document, configuration.FallbackContent.SponsorUrl);

            return new CommunityHubLoadResult(
                content,
                IsContentConfigured: true,
                IsFeedbackConfigured: configuration.FeedbackEndpoint is not null,
                UsedFallback: false,
                RemoteLoadFailed: false,
                IsEchoCaveAccessRestricted: configuration.Afdian?.HasEchoCaveEligibilityRestriction == true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warning("Timed out while loading community hub content");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            logger.Warning(ex, "Unable to load community hub content");
        }

        return new CommunityHubLoadResult(
            configuration.FallbackContent,
            IsContentConfigured: true,
            IsFeedbackConfigured: configuration.FeedbackEndpoint is not null,
            UsedFallback: true,
            RemoteLoadFailed: true,
            IsEchoCaveAccessRestricted: configuration.Afdian?.HasEchoCaveEligibilityRestriction == true);
    }

    public async Task<EchoCaveAccessResult> VerifyEchoCaveAccessAsync(
        string supporterIdentifier,
        CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync(cancellationToken);
        var afdian = configuration.Afdian;
        if (afdian is null || !afdian.HasEchoCaveEligibilityRestriction)
        {
            return new EchoCaveAccessResult(IsConfigured: false, IsEligible: false);
        }

        if (!afdian.HasCredentials)
        {
            if (configuration.FeedbackEndpoint is not null)
            {
                return await VerifyEchoCaveAccessWithEndpointAsync(
                    configuration.FeedbackEndpoint,
                    supporterIdentifier,
                    cancellationToken);
            }

            throw new InvalidOperationException("Afdian credentials or an echo cave verification endpoint are required.");
        }

        var identifier = supporterIdentifier.Trim();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return new EchoCaveAccessResult(IsConfigured: true, IsEligible: false);
        }

        var matches = (await LoadAfdianSponsorsAsync(afdian, cancellationToken))
            .Where(sponsor => MatchesSupporterIdentifier(sponsor, identifier))
            .ToArray();
        if (matches.Length != 1)
        {
            return new EchoCaveAccessResult(IsConfigured: true, IsEligible: false);
        }

        var sponsor = matches[0];
        var planName = sponsor.CurrentPlan?.Name?.Trim() ?? string.Empty;
        var isEligible = !string.IsNullOrWhiteSpace(planName)
            && afdian.EchoCaveEligiblePlanNames!.Any(candidate =>
                planName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        return new EchoCaveAccessResult(
            IsConfigured: true,
            IsEligible: isEligible,
            SponsorName: TrimTo(sponsor.User?.Name, 80),
            PlanName: TrimTo(planName, 80));
    }

    private async Task<EchoCaveAccessResult> VerifyEchoCaveAccessWithEndpointAsync(
        Uri feedbackEndpoint,
        string supporterIdentifier,
        CancellationToken cancellationToken)
    {
        var verificationEndpoint = new Uri(feedbackEndpoint, "verify");
        using var response = await httpClient.PostAsJsonAsync(
            verificationEndpoint,
            new { afdianSupporterIdentifier = supporterIdentifier.Trim() },
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EchoCaveAccessResult>(
            JsonOptions,
            cancellationToken);
        return result ?? throw new InvalidOperationException(
            "The echo cave verification endpoint returned an empty response.");
    }

    public async Task SubmitEchoAsync(
        EchoCaveSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (configuration.FeedbackEndpoint is null)
        {
            throw new InvalidOperationException("The echo cave feedback endpoint is not configured.");
        }

        if (configuration.Afdian?.HasEchoCaveEligibilityRestriction == true)
        {
            var access = await VerifyEchoCaveAccessAsync(
                submission.AfdianSupporterIdentifier,
                cancellationToken);
            if (!access.IsEligible)
            {
                throw new UnauthorizedAccessException("The Afdian account does not have access to the echo cave.");
            }
        }

        using var response = await httpClient.PostAsJsonAsync(
            configuration.FeedbackEndpoint,
            submission,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    private async Task<CommunityHubConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        if (suppliedConfiguration is not null)
        {
            return suppliedConfiguration;
        }

        if (loadedConfiguration is not null)
        {
            return loadedConfiguration;
        }

        try
        {
            await using var stream = AssetLoader.Open(new Uri(ConfigurationAssetUri));
            var document = await JsonSerializer.DeserializeAsync<CommunityHubConfigurationDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            var configuredFeedbackEndpoint = ParseHttpsUri(document?.FeedbackEndpoint);
            var feedbackEndpointVariable = document?.FeedbackEndpointEnvironmentVariable?.Trim();
            var environmentFeedbackEndpoint = string.IsNullOrWhiteSpace(feedbackEndpointVariable)
                ? null
                : ParseHttpsUri(environmentVariableReader(feedbackEndpointVariable));
            loadedConfiguration = new CommunityHubConfiguration(
                ParseHttpsUri(document?.ContentEndpoint),
                environmentFeedbackEndpoint ?? configuredFeedbackEndpoint,
                NormalizeContent(
                    new CommunityHubContentDocument
                    {
                        SponsorUrl = document?.FallbackSponsorUrl,
                        SpecialThanks = document?.FallbackSpecialThanks ?? [],
                        Sponsors = document?.FallbackSponsors ?? []
                    },
                    fallbackSponsorUrl: string.Empty),
                ParseAfdianConfiguration(document?.Afdian));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            logger.Warning(ex, "Unable to load community hub configuration");
            loadedConfiguration = EmptyConfiguration();
        }

        return loadedConfiguration;
    }

    private static CommunityHubConfiguration EmptyConfiguration()
    {
        return new CommunityHubConfiguration(
            ContentEndpoint: null,
            FeedbackEndpoint: null,
            new CommunityHubContent([], [], string.Empty));
    }

    private static CommunityHubContent NormalizeContent(
        CommunityHubContentDocument? document,
        string fallbackSponsorUrl)
    {
        var specialThanks = NormalizePeople(document?.SpecialThanks);
        var sponsors = NormalizePeople(document?.Sponsors);
        var sponsorUrl = NormalizeExternalUrl(document?.SponsorUrl) ?? NormalizeExternalUrl(fallbackSponsorUrl) ?? string.Empty;
        return new CommunityHubContent(specialThanks, sponsors, sponsorUrl);
    }

    private static IReadOnlyList<CommunityPerson> NormalizePeople(IEnumerable<CommunityPersonDocument>? people)
    {
        if (people is null)
        {
            return [];
        }

        return people
            .Where(person => !string.IsNullOrWhiteSpace(person.Name))
            .Take(MaxPeoplePerSection)
            .Select(person => new CommunityPerson(
                TrimTo(person.Name, 80),
                TrimTo(person.Description, 240),
                TrimTo(person.Tier, 60),
                NormalizeExternalUrl(person.Url) ?? string.Empty))
            .ToArray();
    }

    private static string TrimTo(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static Uri? ParseHttpsUri(string? value)
    {
        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
               && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static string? NormalizeExternalUrl(string? value)
    {
        return ParseHttpsUri(value)?.AbsoluteUri;
    }

    private async Task<CommunityHubContent> LoadAfdianContentAsync(
        AfdianCommunityConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var records = await LoadAfdianSponsorsAsync(configuration, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var allSpecialHonorRecords = records
            .Where(sponsor => IsSpecialHonor(sponsor, configuration, now))
            .ToArray();
        var specialHonorSet = allSpecialHonorRecords.ToHashSet();
        var specialHonorRecords = allSpecialHonorRecords
            .OrderByDescending(sponsor => ParseAmount(sponsor.AllSumAmount))
            .ThenBy(sponsor => sponsor.User?.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPeoplePerSection)
            .ToArray();
        var sponsorPeople = records
            .Where(sponsor => !specialHonorSet.Contains(sponsor))
            .OrderByDescending(sponsor => sponsor.LastPayTime)
            .ThenBy(sponsor => sponsor.User?.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPeoplePerSection)
            .Select(sponsor => new CommunityPerson(TrimTo(sponsor.User?.Name, 80)))
            .ToArray();
        var specialHonorPeople = await Task.WhenAll(
            specialHonorRecords.Select(sponsor => MapAfdianSpecialHonorAsync(sponsor, cancellationToken)));

        var sponsorUrl = NormalizeExternalUrl(configuration.SponsorUrl) ?? string.Empty;
        return new CommunityHubContent(specialHonorPeople, sponsorPeople, sponsorUrl);
    }

    private async Task<IReadOnlyList<AfdianSponsorDocument>> LoadAfdianSponsorsAsync(
        AfdianCommunityConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var sponsors = new Dictionary<string, AfdianSponsorDocument>(StringComparer.OrdinalIgnoreCase);
        var maximumPages = Math.Clamp(configuration.MaximumPages, 1, 100);
        var totalPages = maximumPages;

        for (var page = 1; page <= totalPages; page++)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var parameters = JsonSerializer.Serialize(new { page }, JsonOptions);
            var request = new AfdianRequestDocument
            {
                UserId = configuration.UserId,
                Timestamp = timestamp,
                Parameters = parameters,
                Sign = CreateAfdianSignature(configuration.Token, configuration.UserId, parameters, timestamp)
            };

            using var response = await httpClient.PostAsJsonAsync(
                configuration.ApiEndpoint,
                request,
                JsonOptions,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<AfdianResponseDocument>(
                JsonOptions,
                cancellationToken);
            if (payload?.Ec != AfdianSuccessCode)
            {
                throw new HttpRequestException(
                    $"Afdian returned an error ({payload?.Ec}): {payload?.Message ?? "unknown error"}");
            }

            var data = payload.Data;
            if (data is null)
            {
                break;
            }

            totalPages = Math.Min(maximumPages, Math.Max(1, data.TotalPage));
            foreach (var sponsor in data.List ?? [])
            {
                var name = sponsor.User?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var key = sponsor.User?.UserId?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = name;
                }

                if (!sponsors.TryGetValue(key, out var existing)
                    || ParseAmount(sponsor.AllSumAmount) > ParseAmount(existing.AllSumAmount))
                {
                    sponsors[key] = sponsor;
                }
            }

            if (page >= data.TotalPage || data.List is null || data.List.Count == 0)
            {
                break;
            }
        }

        return sponsors.Values.ToArray();
    }

    private static bool MatchesSupporterIdentifier(
        AfdianSponsorDocument sponsor,
        string identifier)
    {
        var userId = sponsor.User?.UserId?.Trim();
        if (!string.IsNullOrWhiteSpace(userId)
            && userId.Equals(identifier, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = sponsor.User?.Name?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            && name.Equals(identifier, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CommunityPerson> MapAfdianSpecialHonorAsync(
        AfdianSponsorDocument sponsor,
        CancellationToken cancellationToken)
    {
        var avatarUrl = NormalizeExternalUrl(sponsor.User?.Avatar) ?? string.Empty;
        Bitmap? avatarImage = null;
        if (!string.IsNullOrWhiteSpace(avatarUrl))
        {
            try
            {
                using var response = await httpClient.GetAsync(
                    avatarUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                avatarImage = Bitmap.DecodeToWidth(
                    stream,
                    AvatarDecodeWidth,
                    BitmapInterpolationMode.HighQuality);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.Warning("Timed out while loading Afdian avatar for {SponsorName}", sponsor.User?.Name);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or InvalidOperationException)
            {
                logger.Warning(ex, "Unable to load Afdian avatar for {SponsorName}", sponsor.User?.Name);
            }
        }

        return new CommunityPerson(
            TrimTo(sponsor.User?.Name, 80),
            AvatarUrl: avatarUrl,
            AvatarImage: avatarImage);
    }

    private static bool IsSpecialHonor(
        AfdianSponsorDocument sponsor,
        AfdianCommunityConfiguration configuration,
        DateTimeOffset now)
    {
        var planName = sponsor.CurrentPlan?.Name?.Trim();
        var matchesPlan = !string.IsNullOrWhiteSpace(planName)
            && configuration.SpecialHonorPlanNames.Any(candidate =>
                planName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        var minimum = configuration.SpecialHonorMinimumAmount;
        var meetsAmount = minimum > 0 && ParseAmount(sponsor.AllSumAmount) >= minimum;
        if (!matchesPlan && !meetsAmount)
        {
            return false;
        }

        var durationMonths = configuration.SpecialHonorDurationMonths;
        if (durationMonths <= 0)
        {
            return true;
        }

        return sponsor.LastPayTime > 0
            && DateTimeOffset.FromUnixTimeSeconds(sponsor.LastPayTime)
                .AddMonths(durationMonths) >= now;
    }

    private static decimal ParseAmount(string? amount) =>
        decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0m;

    private static string CreateAfdianSignature(
        string token,
        string userId,
        string parameters,
        long timestamp)
    {
        var input = $"{token}params{parameters}ts{timestamp}user_id{userId}";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    private AfdianCommunityConfiguration? ParseAfdianConfiguration(
        AfdianConfigurationDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        var endpoint = ParseHttpsUri(document.ApiEndpoint);
        if (endpoint is null)
        {
            return null;
        }

        var configuredUserId = document.UserId?.Trim();
        var userIdVariable = document.UserIdEnvironmentVariable?.Trim();
        var tokenVariable = document.TokenEnvironmentVariable?.Trim();
        var userId = !string.IsNullOrWhiteSpace(configuredUserId)
            ? configuredUserId
            : string.IsNullOrWhiteSpace(userIdVariable)
                ? string.Empty
                : environmentVariableReader(userIdVariable) ?? string.Empty;
        var token = string.IsNullOrWhiteSpace(tokenVariable)
            ? string.Empty
            : environmentVariableReader(tokenVariable) ?? string.Empty;
        var threshold = Math.Max(0, document.SpecialHonorMinimumAmount);
        var plans = (document.SpecialHonorPlanNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var echoCavePlans = (document.EchoCaveEligiblePlanNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new AfdianCommunityConfiguration(
            endpoint,
            document.SponsorUrl?.Trim() ?? string.Empty,
            userId.Trim(),
            token.Trim(),
            threshold,
            plans,
            document.MaximumPages,
            Math.Max(0, document.SpecialHonorDurationMonths),
            echoCavePlans);
    }

    private sealed class CommunityHubConfigurationDocument
    {
        public string? ContentEndpoint { get; init; }

        public string? FeedbackEndpoint { get; init; }

        public string? FeedbackEndpointEnvironmentVariable { get; init; }

        public string? FallbackSponsorUrl { get; init; }

        public List<CommunityPersonDocument> FallbackSpecialThanks { get; init; } = [];

        public List<CommunityPersonDocument> FallbackSponsors { get; init; } = [];

        public AfdianConfigurationDocument? Afdian { get; init; }
    }

    private sealed class AfdianConfigurationDocument
    {
        public string? ApiEndpoint { get; init; }

        public string? SponsorUrl { get; init; }

        public string? UserId { get; init; }

        public string? UserIdEnvironmentVariable { get; init; }

        public string? TokenEnvironmentVariable { get; init; }

        public decimal SpecialHonorMinimumAmount { get; init; }

        public List<string>? SpecialHonorPlanNames { get; init; }

        public List<string>? EchoCaveEligiblePlanNames { get; init; }

        public int MaximumPages { get; init; } = 20;

        public int SpecialHonorDurationMonths { get; init; }
    }

    private sealed class CommunityHubContentDocument
    {
        public string? SponsorUrl { get; init; }

        public List<CommunityPersonDocument> SpecialThanks { get; init; } = [];

        public List<CommunityPersonDocument> Sponsors { get; init; } = [];
    }

    private sealed class CommunityPersonDocument
    {
        public string? Name { get; init; }

        public string? Description { get; init; }

        public string? Tier { get; init; }

        public string? Url { get; init; }
    }

    private sealed class AfdianRequestDocument
    {
        [JsonPropertyName("user_id")]
        public string UserId { get; init; } = string.Empty;

        [JsonPropertyName("ts")]
        public long Timestamp { get; init; }

        [JsonPropertyName("params")]
        public string Parameters { get; init; } = string.Empty;

        [JsonPropertyName("sign")]
        public string Sign { get; init; } = string.Empty;
    }

    private sealed class AfdianResponseDocument
    {
        [JsonPropertyName("ec")]
        public int Ec { get; init; }

        [JsonPropertyName("em")]
        public string? Message { get; init; }

        [JsonPropertyName("data")]
        public AfdianSponsorDataDocument? Data { get; init; }
    }

    private sealed class AfdianSponsorDataDocument
    {
        [JsonPropertyName("total_page")]
        public int TotalPage { get; init; }

        [JsonPropertyName("list")]
        public List<AfdianSponsorDocument>? List { get; init; }
    }

    private sealed class AfdianSponsorDocument
    {
        [JsonPropertyName("sponsor_plans")]
        public List<AfdianPlanDocument>? SponsorPlans { get; init; }

        [JsonPropertyName("current_plan")]
        public AfdianPlanDocument? CurrentPlan { get; init; }

        [JsonPropertyName("all_sum_amount")]
        public string? AllSumAmount { get; init; }

        [JsonPropertyName("last_pay_time")]
        public long LastPayTime { get; init; }

        [JsonPropertyName("user")]
        public AfdianUserDocument? User { get; init; }
    }

    private sealed class AfdianPlanDocument
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class AfdianUserDocument
    {
        [JsonPropertyName("user_id")]
        public string? UserId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("avatar")]
        public string? Avatar { get; init; }
    }
}
