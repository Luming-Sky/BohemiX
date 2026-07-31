using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace BohemiX.App.Community;

public sealed record CommunityPerson(
    string Name,
    string Description = "",
    string Tier = "",
    string Url = "",
    string AvatarUrl = "",
    Bitmap? AvatarImage = null) : IDisposable
{
    public bool HasTier => !string.IsNullOrWhiteSpace(Tier);

    public bool HasAvatar => AvatarImage is not null;

    public void Dispose() => AvatarImage?.Dispose();
}

public sealed record CommunityHubContent(
    IReadOnlyList<CommunityPerson> SpecialThanks,
    IReadOnlyList<CommunityPerson> Sponsors,
    string SponsorUrl);

public sealed record CommunityHubConfiguration(
    Uri? ContentEndpoint,
    Uri? FeedbackEndpoint,
    CommunityHubContent FallbackContent,
    AfdianCommunityConfiguration? Afdian = null);

public sealed record AfdianCommunityConfiguration(
    Uri ApiEndpoint,
    string SponsorUrl,
    string UserId,
    string Token,
    decimal SpecialHonorMinimumAmount,
    IReadOnlyList<string> SpecialHonorPlanNames,
    int MaximumPages = 20,
    int SpecialHonorDurationMonths = 0,
    IReadOnlyList<string>? EchoCaveEligiblePlanNames = null)
{
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(Token);

    public bool HasEchoCaveEligibilityRestriction =>
        EchoCaveEligiblePlanNames is { Count: > 0 };
}

public sealed record CommunityHubLoadResult(
    CommunityHubContent Content,
    bool IsContentConfigured,
    bool IsFeedbackConfigured,
    bool UsedFallback,
    bool RemoteLoadFailed,
    bool IsEchoCaveAccessRestricted = false);

public sealed record EchoCaveAccessResult(
    bool IsConfigured,
    bool IsEligible,
    string SponsorName = "",
    string PlanName = "");

public sealed record EchoCaveSubmission(
    string Subject,
    string Message,
    string Contact,
    string AppVersion,
    string Language,
    string AfdianSupporterIdentifier = "");

public interface ICommunityHubService
{
    Task<CommunityHubLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task<EchoCaveAccessResult> VerifyEchoCaveAccessAsync(
        string supporterIdentifier,
        CancellationToken cancellationToken = default);

    Task SubmitEchoAsync(EchoCaveSubmission submission, CancellationToken cancellationToken = default);
}
