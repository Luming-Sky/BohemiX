using System;
using System.Threading;
using BohemiX.App.Community;
using BohemiX.App.PlayerProfiles;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure;
using BohemiX.Modules.Alchemy;
using BohemiX.Modules.Alchemy.ViewModels;
using BohemiX.Modules.Forge;
using BohemiX.Modules.Forge.ViewModels;
using BohemiX.Modules.SaveManager;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BohemiX.App;

public static class ApplicationServiceRegistration
{
    public static IServiceCollection AddBohemiXApplication(
        this IServiceCollection services,
        ILogger logger)
    {
        services.AddSingleton(logger);
        services.AddBohemiXInfrastructure();
        services.AddAlchemyModule();
        services.AddForgeModule();
        services.AddSaveManagerModule();

        services.AddSingleton<IGamePathPickerService, GamePathPickerService>();
        services.AddSingleton<IAvatarCropService, AvatarCropService>();
        services.AddSingleton<IModCoverCacheService, ModCoverCacheService>();
        services.AddSingleton<IGameExitThumbnailCaptureService, GameExitThumbnailCaptureService>();
        services.AddSingleton(GameRuntimeMonitorOptions.Default);
        services.AddSingleton<IGameRuntimeMonitorService, GameRuntimeMonitorService>();
        services.AddSingleton<IApplicationErrorReporter, AvaloniaApplicationErrorReporter>();
        services.AddSingleton<ApplicationExceptionCoordinator>();
        services.AddSingleton<IMainWindowTextCatalog, MainWindowTextCatalog>();
        services.AddSingleton<ICommunityHubService, CommunityHubService>();
        services.AddSingleton<MoreSettingsViewModel>();

        services.AddSingleton<NexusEmbeddedBrowserAuthService>();
        services.AddSingleton<LazyNexusEmbeddedBrowserServices>();
        services.AddSingleton<INexusEmbeddedBrowserAuthService>(provider =>
            provider.GetRequiredService<LazyNexusEmbeddedBrowserServices>());
        services.AddSingleton<INexusWebDownloadLinkResolver>(provider =>
            provider.GetRequiredService<LazyNexusEmbeddedBrowserServices>());
        services.AddSingleton<INexusWebPageResolver>(provider =>
            provider.GetRequiredService<LazyNexusEmbeddedBrowserServices>());
        services.AddSingleton<INexusDownloadAuthorizationService>(provider =>
            provider.GetRequiredService<LazyNexusEmbeddedBrowserServices>());

        services.AddSingleton<PlayerProfileManagerViewModel>(provider => new PlayerProfileManagerViewModel(
            provider.GetRequiredService<IPlayerService>(),
            provider.GetRequiredService<IGamePathPickerService>(),
            provider.GetRequiredService<IAvatarCropService>(),
            provider.GetRequiredService<ILogger>(),
            provider.GetRequiredService<IPlayerSteamAccountBindingService>(),
            provider.GetRequiredService<IWorkshopService>(),
            provider.GetRequiredService<INexusAccountService>()));
        services.AddSingleton(provider => new Lazy<AlchemyWorkshopViewModel>(
            provider.GetRequiredService<AlchemyWorkshopViewModel>,
            LazyThreadSafetyMode.ExecutionAndPublication));
        services.AddSingleton(provider => new Lazy<ForgeWorkshopViewModel>(
            provider.GetRequiredService<ForgeWorkshopViewModel>,
            LazyThreadSafetyMode.ExecutionAndPublication));
        services.AddSingleton<MainWindowViewModel>();

        return services;
    }
}
