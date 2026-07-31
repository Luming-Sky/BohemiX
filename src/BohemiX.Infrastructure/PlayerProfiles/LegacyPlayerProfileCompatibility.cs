using BohemiX.Core.PlayerProfiles;

namespace BohemiX.Infrastructure.PlayerProfiles;

// Keep obsolete account/environment fields confined to the persistence compatibility boundary.
#pragma warning disable CS0618
internal static class LegacyPlayerProfileCompatibility
{
    internal sealed record EnvironmentFields(string Platform, string? GameInstallPath);

    public static EnvironmentFields From(PlayerProfile profile) =>
        new(profile.Platform, profile.GameInstallPath);

    public static PlayerProfile Apply(PlayerProfile profile, PlayerProfileDraft draft) =>
        profile with
        {
            Platform = draft.Platform,
            GameInstallPath = draft.GameInstallPath
        };

    public static PlayerProfile Apply(PlayerProfile profile, PlayerProfileUpdate update) =>
        profile with
        {
            Platform = update.Platform,
            GameInstallPath = update.GameInstallPath
        };

    public static PlayerProfile Apply(PlayerProfile profile, EnvironmentFields environment) =>
        profile with
        {
            Platform = environment.Platform,
            GameInstallPath = environment.GameInstallPath
        };
}
#pragma warning restore CS0618
