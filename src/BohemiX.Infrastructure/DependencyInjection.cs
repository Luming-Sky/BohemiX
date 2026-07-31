using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.PlayerProfiles;
using BohemiX.Infrastructure.Services;
using BohemiX.Infrastructure.Services.Saves;
using Microsoft.Extensions.DependencyInjection;

namespace BohemiX.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBohemiXInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<PlayerContext>();
        services.AddSingleton<IPlayerContext>(provider => provider.GetRequiredService<PlayerContext>());
        services.AddSingleton<IPlayerContextAccessor>(provider => provider.GetRequiredService<PlayerContext>());
        services.AddSingleton<ProfileConnectionFactory>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton(new NexusModsOptions(EnableLegacyBrowserCookieAuth: true));
        services.AddSingleton(new NexusSsoOptions(
            ApplicationSlug: Environment.GetEnvironmentVariable("BOHEMIX_NEXUS_SSO_APPLICATION_SLUG")?.Trim() is { Length: > 0 } slug
                ? slug
                : string.Empty));
        services.AddSingleton(new ModDownloaderOptions());
        services.AddSingleton(new WorkshopOptions());
        services.AddSingleton(new ModPackCatalogOptions(
            RemoteCatalogUri: Environment.GetEnvironmentVariable("BOHEMIX_MODPACK_CATALOG_URL")?.Trim(),
            RemoteSignatureUri: Environment.GetEnvironmentVariable("BOHEMIX_MODPACK_CATALOG_SIGNATURE_URL")?.Trim(),
            PublicKeyPem: Environment.GetEnvironmentVariable("BOHEMIX_MODPACK_CATALOG_PUBLIC_KEY")?.Trim() is { Length: > 0 } key
                ? key.Replace("\\n", "\n", StringComparison.Ordinal)
                : ModPackCatalogTrust.DefaultPublicKeyPem));
        services.AddSingleton<IApplicationPathService, ApplicationPathService>();
        services.AddSingleton<IApplicationUpdateValidationService, ApplicationUpdateValidationService>();
        services.AddSingleton<IApplicationUpdateCheckService>(_ =>
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.7");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return new ApplicationUpdateCheckService(client);
        });
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IPlayerSteamAccountBindingService, SqliteSteamAccountBindingService>();
        services.AddSingleton<IPlayerProvider, LocalPlayerProvider>();
        services.AddSingleton<IPlayerService, PlayerService>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IPlayerStatisticsService>(provider => new PlayerStatisticsService(
            provider.GetRequiredService<SqliteConnectionFactory>(),
            provider.GetRequiredService<IApplicationPathService>()));
        services.AddSingleton<IKcdAdventureSaveReader, KcdAdventureSaveReader>();
        services.AddSingleton<IAdventureProfileDataService, AdventureProfileDataService>();
        services.AddSingleton<IAppStartupService, AppStartupService>();
        services.AddSingleton<IGameInstallationStore, GameInstallationStore>();
        services.AddSingleton<IAchievementRuleService, AchievementRuleService>();
        services.AddSingleton<IExternalStoreService, ExternalStoreService>();
        services.AddSingleton<GameDiscoveryService>();
        services.AddSingleton<IGameDiscoveryService>(provider => provider.GetRequiredService<GameDiscoveryService>());
        services.AddSingleton<ILocalGameInstallationDetector>(provider => provider.GetRequiredService<GameDiscoveryService>());
        services.AddSingleton<IGameLaunchSessionService, GameLaunchSessionService>();
        services.AddSingleton<IGameLauncherService, GameLauncherService>();
        services.AddSingleton<IGameNewsService, SteamGameNewsService>();
        services.AddSingleton<IGameProcessMonitorService, GameProcessMonitorService>();
        services.AddSingleton<IModCatalogService, ModCatalogService>();
        services.AddSingleton<IModPackCatalogService>(provider =>
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.1");
            return new ModPackCatalogService(
                provider.GetRequiredService<IApplicationPathService>(),
                provider.GetRequiredService<ModPackCatalogOptions>(),
                client,
                provider.GetRequiredService<Serilog.ILogger>());
        });
        services.AddSingleton<IModConflictAnalyzer, ModConflictAnalyzer>();
        services.AddSingleton<IModConflictReviewService, ModConflictReviewService>();
        services.AddSingleton<IModDownloader, ModDownloader>();
        services.AddSingleton<IModPackageInstaller, ModPackageInstaller>();
        services.AddSingleton<IModPackImportService, ModPackImportService>();
        services.AddSingleton<IModHealthAnalyzer, ModHealthAnalyzer>();
        services.AddSingleton<IModLaunchPreflightService, ModLaunchPreflightService>();
        services.AddSingleton<IModLoadOrderPlanner, ModLoadOrderPlanner>();
        services.AddSingleton<IModManagementSnapshotService, ModManagementSnapshotService>();
        services.AddSingleton<IModTranslationService, MyMemoryModTranslationService>();
        services.AddSingleton<IModMountPlanBuilder, ModMountPlanBuilder>();
        services.AddSingleton<ISaveMonitorService, SaveMonitorService>();
        services.AddSingleton<IJunctionRouter, JunctionRouter>();
        services.AddSingleton<IKcdSaveParser, SaveParser>();
        services.AddSingleton<ISlotManagerFactory, SlotManagerFactory>();
        services.AddSingleton<IProcessCoordinatorFactory, ProcessCoordinatorFactory>();
        services.AddSingleton<SaveProfileService>();
        services.AddSingleton<ISaveProfileService>(provider => provider.GetRequiredService<SaveProfileService>());
        services.AddSingleton<ISaveRestoreValidationService>(provider => provider.GetRequiredService<SaveProfileService>());
        services.AddSingleton<EnvironmentNexusApiKeyProvider>();
        services.AddSingleton<NexusAccountCredentialStore>();
        services.AddSingleton<NexusApiKeyProvider>();
        services.AddSingleton<INexusApiKeyProvider>(provider => provider.GetRequiredService<NexusApiKeyProvider>());
        services.AddSingleton<INexusApiKeyStore>(provider => provider.GetRequiredService<EnvironmentNexusApiKeyProvider>());
        services.AddSingleton<INexusAccountService, NexusAccountService>();
        services.AddSingleton<NexusCookieAuthService>();
        services.AddSingleton<INexusCookieAuthService, CompositeNexusCookieAuthService>();
        services.AddSingleton<INexusModService, NexusModService>();
        services.AddSingleton<INexusCollectionService, NexusCollectionService>();
        services.AddSingleton<IModPackInstallService, ModPackInstallService>();
        services.AddSingleton<IWorkshopService, WorkshopService>();
        services.AddSingleton<ITrackerBridgeMonitorService, TrackerBridgeMonitorService>();
        services.AddSingleton<ITrackerDiagnosticsService, TrackerDiagnosticsService>();
        services.AddSingleton<ITrackerAchievementService, TrackerAchievementService>();
        services.AddSingleton<ITrackerModHealthService, TrackerModHealthService>();
        services.AddSingleton<ITrackerModInstallService, TrackerModInstallService>();
        services.AddSingleton<ITrackerModPackageService, TrackerModPackageService>();
        services.AddSingleton<ITrackerProjectionService, TrackerProjectionService>();
        services.AddSingleton<ITrackerRuntimeService, TrackerRuntimeService>();
        services.AddSingleton<IVfsSessionService, VfsSessionService>();

        return services;
    }
}
