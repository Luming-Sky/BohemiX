using BohemiX.Core.Models;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using BohemiX.Infrastructure.Persistence;
using Dapper;

namespace BohemiX.Infrastructure.Services;

public sealed class AdventureProfileDataService(
    SqliteConnectionFactory connectionFactory,
    IApplicationPathService applicationPathService,
    IKcdAdventureSaveReader saveReader) : IAdventureProfileDataService
{
    public async Task<AdventureProfileData> LoadAsync(
        Guid? saveSlotId = null,
        CancellationToken cancellationToken = default)
    {
        var environmentId = applicationPathService.GetPaths().GameEnvironmentId.ToString("D");
        var requestedSaveSlotId = saveSlotId?.ToString("D");
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var saveData = await connection.QuerySingleOrDefaultAsync<SaveDataRow>(new CommandDefinition(
            """
            SELECT
                Id AS SaveSlotId,
                PhysicalName,
                COALESCE(PlayTimeSeconds, 0) AS PlayTimeSeconds
            FROM SaveSlots
            WHERE EnvironmentId = @EnvironmentId
              AND (@SaveSlotId IS NULL OR Id = @SaveSlotId)
            ORDER BY COALESCE(LastActivatedAtUtc, LastSavedAtUtc, UpdatedAtUtc) DESC
            LIMIT 1;
            """,
            new { EnvironmentId = environmentId, SaveSlotId = requestedSaveSlotId },
            cancellationToken: cancellationToken));

        var saveSlots = await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM SaveSlots WHERE EnvironmentId = @EnvironmentId;",
            new { EnvironmentId = environmentId },
            cancellationToken: cancellationToken));

        var installedMods = await connection.QuerySingleAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*)
            FROM mod_manifests
            WHERE environment_id = @EnvironmentId AND is_enabled = 1;
            """,
            new { EnvironmentId = environmentId },
            cancellationToken: cancellationToken));

        var installation = await connection.QuerySingleOrDefaultAsync<InstallationRow>(new CommandDefinition(
            """
            SELECT
                source AS Source,
                install_path AS InstallPath,
                executable_path AS ExecutablePath
            FROM game_installations
            WHERE environment_id = @EnvironmentId AND is_verified = 1
            ORDER BY source, install_path
            LIMIT 1;
            """,
            new { EnvironmentId = environmentId },
            cancellationToken: cancellationToken));

        var saveDirectory = saveData is null
            ? null
            : Path.Combine(
                applicationPathService.GetPaths().DataDirectory,
                "saves",
                "vault",
                saveData.PhysicalName);
        var parsedSave = saveDirectory is not null
            ? await saveReader.ReadAsync(saveDirectory, installation?.InstallPath, cancellationToken)
            : KcdAdventureSaveData.Empty;

        return new AdventureProfileData(
            string.Empty,
            FormatPlatform(installation),
            parsedSave.Difficulty,
            ToHours(saveData?.PlayTimeSeconds ?? 0),
            parsedSave.InGameDay,
            parsedSave.MainStoryCompleted,
            parsedSave.MainStoryTotal,
            parsedSave.AchievementsUnlocked,
            parsedSave.AchievementsTotal,
            parsedSave.LocationsDiscovered,
            parsedSave.LocationsTotal,
            Math.Max(0, saveSlots),
            Math.Max(0, installedMods));
    }

    private static int ToHours(long seconds) =>
        (int)Math.Clamp(Math.Floor(Math.Max(0, seconds) / 3600d), 0, int.MaxValue);

    private static string FormatPlatform(InstallationRow? installation)
    {
        if (installation is null)
        {
            return string.Empty;
        }

        if (installation.Source == (int)GameInstallSource.Steam
            || ContainsPlatformMarker(installation, "Steam"))
        {
            return "Steam Edition";
        }

        if (ContainsPlatformMarker(installation, "GOG"))
        {
            return "GOG Edition";
        }

        if (installation.Source == (int)GameInstallSource.Epic
            || ContainsPlatformMarker(installation, "Epic"))
        {
            return "Epic Games";
        }

        return installation.Source == (int)GameInstallSource.Manual
            ? "Manual Installation"
            : string.Empty;
    }

    private static bool ContainsPlatformMarker(InstallationRow installation, string marker) =>
        installation.InstallPath.Contains(marker, StringComparison.OrdinalIgnoreCase)
        || installation.ExecutablePath.Contains(marker, StringComparison.OrdinalIgnoreCase);

    private sealed class SaveDataRow
    {
        public string SaveSlotId { get; init; } = string.Empty;
        public string PhysicalName { get; init; } = string.Empty;
        public long PlayTimeSeconds { get; init; }
    }

    private sealed class InstallationRow
    {
        public int Source { get; init; }
        public string InstallPath { get; init; } = string.Empty;
        public string ExecutablePath { get; init; } = string.Empty;
    }
}
