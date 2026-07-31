using BohemiX.Core.PlayerProfiles;

namespace BohemiX.Infrastructure.PlayerProfiles;

public sealed class LocalPlayerProvider : IPlayerProvider
{
    private const int CurrentProfileVersion = 1;

    public string Id => PlayerProviderIds.Local;

    public string DisplayName => "Local Account";

    public bool RequiresNetwork => false;

    public Task<PlayerProfile> CreateAsync(
        PlayerProfileDraft draft,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var profile = new PlayerProfile(
            Guid.NewGuid(),
            ValidateDisplayName(draft.DisplayName),
            NullIfWhiteSpace(draft.Avatar),
            Id,
            ValidateBio(draft.Bio),
            now,
            now,
            CloudId: null,
            Email: null,
            AccessToken: null,
            RefreshToken: null,
            IsCloudUser: false,
            SyncTime: null,
            Version: CurrentProfileVersion);
        return Task.FromResult(LegacyPlayerProfileCompatibility.Apply(profile, draft));
    }

    public Task<PlayerProfile> UpdateAsync(
        PlayerProfile current,
        PlayerProfileUpdate update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = current with
        {
            DisplayName = ValidateDisplayName(update.DisplayName),
            Avatar = NullIfWhiteSpace(update.Avatar),
            Bio = ValidateBio(update.Bio),
            Version = Math.Max(current.Version, CurrentProfileVersion)
        };
        return Task.FromResult(LegacyPlayerProfileCompatibility.Apply(profile, update));
    }

    private static string ValidateDisplayName(string value)
    {
        var displayName = value?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 64 || displayName.Any(char.IsControl))
        {
            throw new ArgumentException("Display name must contain 1 to 64 visible characters.", nameof(value));
        }

        return displayName;
    }

    private static string? ValidateBio(string? value)
    {
        var bio = NullIfWhiteSpace(value);
        if (bio is { Length: > 280 } || bio?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("Biography must contain at most 280 visible characters.", nameof(value));
        }

        return bio;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
