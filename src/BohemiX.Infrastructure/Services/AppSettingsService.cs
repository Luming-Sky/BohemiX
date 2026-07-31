using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;
using System.Text.Json;

namespace BohemiX.Infrastructure.Services;

public sealed class AppSettingsService : IAppSettingsService
{
    private const string PreferredGameInstallPathKey = "preferredGameInstallPath";
    private const string PreferredGameExecutablePathKey = "preferredGameExecutablePath";
    private const string ModsDirectoryKey = "modsDirectory";
    private const string AllowNexusBrowserCookieAuthKey = "allowNexusBrowserCookieAuth";
    private const string SelectedLanguageKey = "selectedLanguage";
    private const string DefaultNavigationTargetKey = "defaultNavigationTarget";
    private const string ShowNavigationTooltipsKey = "showNavigationTooltips";
    private const string KeepTopNavigationVisibleKey = "keepTopNavigationVisible";
    private const string AutoDiscoverOnStartupKey = "autoDiscoverOnStartup";
    private const string EnableVfsBeforeLaunchKey = "enableVfsBeforeLaunch";
    private const string LaunchArgumentsKey = "launchArguments";
    private const string UseSteamProtocolKey = "useSteamProtocol";
    private const string MinimizeOnLaunchKey = "minimizeOnLaunch";
    private const string CloseAfterLaunchKey = "closeAfterLaunch";
    private const string CheckModConflictsBeforeLaunchKey = "checkModConflictsBeforeLaunch";
    private const string EnableAcrylicEffectsKey = "enableAcrylicEffects";
    private const string ReduceMotionKey = "reduceMotion";
    private const string BackgroundDimLevelKey = "backgroundDimLevel";
    private const string CompactSidebarKey = "compactSidebar";
    private const string ShowRuntimeCardInSidebarKey = "showRuntimeCardInSidebar";
    private const string OfficialSavePathKey = "officialSavePath";
    private const string AutoSnapshotOnGameExitKey = "autoSnapshotOnGameExit";
    private const string AutoSnapshotBeforeSwitchKey = "autoSnapshotBeforeSwitch";
    private const string AutoSnapshotRetentionKey = "autoSnapshotRetention";
    private const string AutoBackupSaveNodesKey = "autoBackupSaveNodes";
    private const string BackupNodeRetentionKey = "backupNodeRetention";
    private const string AutoMarkCriticalSaveNodesImportantKey = "autoMarkCriticalSaveNodesImportant";
    private const string AutoMarkManualSaveNodesImportantKey = "autoMarkManualSaveNodesImportant";
    private const string InstalledModGroupNamesKey = "installedModGroupNames";
    private const string InstalledModGroupAssignmentsKey = "installedModGroupAssignments";
    private const string StaticBackgroundPathKey = "staticBackgroundPath";
    private const string DynamicBackgroundPathKey = "dynamicBackgroundPath";
    private const string UseDynamicBackgroundKey = "useDynamicBackground";
    private const string BackgroundCropModeKey = "backgroundCropMode";
    private const string BackgroundCropXKey = "backgroundCropX";
    private const string BackgroundCropYKey = "backgroundCropY";
    private const string BackgroundCropWidthKey = "backgroundCropWidth";
    private const string BackgroundCropHeightKey = "backgroundCropHeight";
    private const string CardBackgroundPathKey = "cardBackgroundPath";
    private const string UseDynamicCardBackgroundKey = "useDynamicCardBackground";
    private const string StaticCardBackgroundPathKey = "staticCardBackgroundPath";
    private const string DynamicCardBackgroundPathKey = "dynamicCardBackgroundPath";
    private const string ForgeRenderQualityKey = "forgeRenderQuality";
    private const string ForgeTextureQualityKey = "forgeTextureQuality";

    private readonly IApplicationPathService applicationPathService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public AppSettingsService(
        IApplicationPathService applicationPathService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<AppSettingsService>();
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var paths = applicationPathService.GetPaths();

        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<SettingRow>(new CommandDefinition(
            "SELECT key AS Key, value AS Value FROM app_settings",
            cancellationToken: cancellationToken));

        var values = rows.ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        return new AppSettings(
            values.GetValueOrDefault(PreferredGameInstallPathKey),
            values.GetValueOrDefault(PreferredGameExecutablePathKey),
            values.GetValueOrDefault(ModsDirectoryKey) ?? paths.ModsDirectory,
            ParseBool(values.GetValueOrDefault(AllowNexusBrowserCookieAuthKey)),
            values.GetValueOrDefault(SelectedLanguageKey) ?? "简体中文",
            values.GetValueOrDefault(DefaultNavigationTargetKey) ?? "Dashboard",
            ParseBool(values.GetValueOrDefault(ShowNavigationTooltipsKey), true),
            ParseBool(values.GetValueOrDefault(KeepTopNavigationVisibleKey), true),
            ParseBool(values.GetValueOrDefault(AutoDiscoverOnStartupKey), true),
            ParseBool(values.GetValueOrDefault(EnableVfsBeforeLaunchKey), true),
            values.GetValueOrDefault(LaunchArgumentsKey) ?? string.Empty,
            ParseBool(values.GetValueOrDefault(UseSteamProtocolKey)),
            ParseBool(values.GetValueOrDefault(MinimizeOnLaunchKey)),
            ParseBool(values.GetValueOrDefault(CloseAfterLaunchKey)),
            ParseBool(values.GetValueOrDefault(CheckModConflictsBeforeLaunchKey), true),
            ParseBool(values.GetValueOrDefault(EnableAcrylicEffectsKey), true),
            ParseBool(values.GetValueOrDefault(ReduceMotionKey)),
            ParseDouble(values.GetValueOrDefault(BackgroundDimLevelKey), 68),
            ParseBool(values.GetValueOrDefault(CompactSidebarKey)),
            ParseBool(values.GetValueOrDefault(ShowRuntimeCardInSidebarKey), true),
            NullIfWhiteSpace(values.GetValueOrDefault(OfficialSavePathKey)),
            ParseBool(values.GetValueOrDefault(AutoSnapshotOnGameExitKey), true),
            ParseBool(values.GetValueOrDefault(AutoSnapshotBeforeSwitchKey), true),
            Math.Clamp(ParseInt(values.GetValueOrDefault(AutoSnapshotRetentionKey), 20), 1, 200),
            ParseBool(values.GetValueOrDefault(AutoBackupSaveNodesKey), true),
            Math.Clamp(ParseInt(values.GetValueOrDefault(BackupNodeRetentionKey), 10), 1, 200),
            ParseBool(values.GetValueOrDefault(AutoMarkCriticalSaveNodesImportantKey), true),
            ParseBool(values.GetValueOrDefault(AutoMarkManualSaveNodesImportantKey), true),
            ParseStringDictionary(values.GetValueOrDefault(InstalledModGroupNamesKey)),
            ParseStringDictionary(values.GetValueOrDefault(InstalledModGroupAssignmentsKey)),
            NullIfWhiteSpace(values.GetValueOrDefault(StaticBackgroundPathKey)),
            NullIfWhiteSpace(values.GetValueOrDefault(DynamicBackgroundPathKey)),
            ParseBool(values.GetValueOrDefault(UseDynamicBackgroundKey)),
            Math.Clamp(ParseInt(values.GetValueOrDefault(BackgroundCropModeKey), 0), 0, 4),
            Math.Clamp(ParseDouble(values.GetValueOrDefault(BackgroundCropXKey), 0), 0, 1),
            Math.Clamp(ParseDouble(values.GetValueOrDefault(BackgroundCropYKey), 0), 0, 1),
            Math.Clamp(ParseDouble(values.GetValueOrDefault(BackgroundCropWidthKey), 1), 0.01, 1),
            Math.Clamp(ParseDouble(values.GetValueOrDefault(BackgroundCropHeightKey), 1), 0.01, 1),
            NullIfWhiteSpace(values.GetValueOrDefault(CardBackgroundPathKey)),
            ParseBool(values.GetValueOrDefault(UseDynamicCardBackgroundKey)),
            NullIfWhiteSpace(values.GetValueOrDefault(StaticCardBackgroundPathKey)) ??
                NullIfWhiteSpace(values.GetValueOrDefault(CardBackgroundPathKey)),
            NullIfWhiteSpace(values.GetValueOrDefault(DynamicCardBackgroundPathKey)),
            NormalizeQuality(values.GetValueOrDefault(ForgeRenderQualityKey)),
            NormalizeQuality(values.GetValueOrDefault(ForgeTextureQualityKey)));
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertAsync(connection, PreferredGameInstallPathKey, settings.PreferredGameInstallPath, transaction, cancellationToken);
        await UpsertAsync(connection, PreferredGameExecutablePathKey, settings.PreferredGameExecutablePath, transaction, cancellationToken);
        await UpsertAsync(connection, ModsDirectoryKey, settings.ModsDirectory, transaction, cancellationToken);
        await UpsertAsync(connection, AllowNexusBrowserCookieAuthKey, settings.AllowNexusBrowserCookieAuth ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, SelectedLanguageKey, settings.SelectedLanguage, transaction, cancellationToken);
        await UpsertAsync(connection, DefaultNavigationTargetKey, settings.DefaultNavigationTarget, transaction, cancellationToken);
        await UpsertAsync(connection, ShowNavigationTooltipsKey, settings.ShowNavigationTooltips ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, KeepTopNavigationVisibleKey, settings.KeepTopNavigationVisible ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, AutoDiscoverOnStartupKey, settings.AutoDiscoverOnStartup ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, EnableVfsBeforeLaunchKey, settings.EnableVfsBeforeLaunch ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, LaunchArgumentsKey, settings.LaunchArguments, transaction, cancellationToken);
        await UpsertAsync(connection, UseSteamProtocolKey, settings.UseSteamProtocol ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, MinimizeOnLaunchKey, settings.MinimizeOnLaunch ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, CloseAfterLaunchKey, settings.CloseAfterLaunch ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, CheckModConflictsBeforeLaunchKey, settings.CheckModConflictsBeforeLaunch ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, EnableAcrylicEffectsKey, settings.EnableAcrylicEffects ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, ReduceMotionKey, settings.ReduceMotion ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, BackgroundDimLevelKey, settings.BackgroundDimLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, CompactSidebarKey, settings.CompactSidebar ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, ShowRuntimeCardInSidebarKey, settings.ShowRuntimeCardInSidebar ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, OfficialSavePathKey, settings.OfficialSavePath, transaction, cancellationToken);
        await UpsertAsync(connection, AutoSnapshotOnGameExitKey, settings.AutoSnapshotOnGameExit ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, AutoSnapshotBeforeSwitchKey, settings.AutoSnapshotBeforeSwitch ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, AutoSnapshotRetentionKey, Math.Clamp(settings.AutoSnapshotRetention, 1, 200).ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, AutoBackupSaveNodesKey, settings.AutoBackupSaveNodes ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, BackupNodeRetentionKey, Math.Clamp(settings.BackupNodeRetention, 1, 200).ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, AutoMarkCriticalSaveNodesImportantKey, settings.AutoMarkCriticalSaveNodesImportant ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, AutoMarkManualSaveNodesImportantKey, settings.AutoMarkManualSaveNodesImportant ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(
            connection,
            InstalledModGroupNamesKey,
            JsonSerializer.Serialize(settings.InstalledModGroupNames ?? new Dictionary<string, string>()),
            transaction,
            cancellationToken);
        await UpsertAsync(
            connection,
            InstalledModGroupAssignmentsKey,
            JsonSerializer.Serialize(settings.InstalledModGroupAssignments ?? new Dictionary<string, string>()),
            transaction,
            cancellationToken);
        await UpsertAsync(connection, StaticBackgroundPathKey, settings.StaticBackgroundPath, transaction, cancellationToken);
        await UpsertAsync(connection, DynamicBackgroundPathKey, settings.DynamicBackgroundPath, transaction, cancellationToken);
        await UpsertAsync(connection, UseDynamicBackgroundKey, settings.UseDynamicBackground ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, BackgroundCropModeKey, settings.BackgroundCropMode.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, BackgroundCropXKey, settings.BackgroundCropX.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, BackgroundCropYKey, settings.BackgroundCropY.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, BackgroundCropWidthKey, settings.BackgroundCropWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, BackgroundCropHeightKey, settings.BackgroundCropHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), transaction, cancellationToken);
        await UpsertAsync(connection, CardBackgroundPathKey, settings.CardBackgroundPath, transaction, cancellationToken);
        await UpsertAsync(connection, UseDynamicCardBackgroundKey, settings.UseDynamicCardBackground ? "true" : "false", transaction, cancellationToken);
        await UpsertAsync(connection, StaticCardBackgroundPathKey, settings.StaticCardBackgroundPath, transaction, cancellationToken);
        await UpsertAsync(connection, DynamicCardBackgroundPathKey, settings.DynamicCardBackgroundPath, transaction, cancellationToken);
        await UpsertAsync(connection, ForgeRenderQualityKey, NormalizeQuality(settings.ForgeRenderQuality), transaction, cancellationToken);
        await UpsertAsync(connection, ForgeTextureQualityKey, NormalizeQuality(settings.ForgeTextureQuality), transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        logger.Information("Application settings saved");
    }

    private static bool ParseBool(string? value, bool defaultValue = false)
    {
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }

    private static double ParseDouble(string? value, double defaultValue)
    {
        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var result)
            ? result
            : defaultValue;
    }

    private static int ParseInt(string? value, int defaultValue)
    {
        return int.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var result)
            ? result
            : defaultValue;
    }

    private static IReadOnlyDictionary<string, string> ParseStringDictionary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var storedNames = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (storedNames is null)
            {
                return result;
            }

            foreach (var (groupKey, customName) in storedNames)
            {
                if (!string.IsNullOrWhiteSpace(groupKey) && !string.IsNullOrWhiteSpace(customName))
                {
                    result[groupKey] = customName;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeQuality(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => "Low",
        "high" => "High",
        _ => "Medium"
    };

    private static Task UpsertAsync(
        System.Data.IDbConnection connection,
        string key,
        string? value,
        System.Data.IDbTransaction transaction,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO app_settings (key, value)
            VALUES (@Key, @Value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """,
            new { Key = key, Value = value ?? string.Empty },
            transaction,
            cancellationToken: cancellationToken));
    }

    private sealed class SettingRow
    {
        public string Key { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;
    }
}
