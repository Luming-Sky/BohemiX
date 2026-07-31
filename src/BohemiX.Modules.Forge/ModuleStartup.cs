using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Services;
using BohemiX.Modules.Forge.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BohemiX.Modules.Forge;

public static class ModuleStartup
{
    public static IServiceCollection AddForgeModule(this IServiceCollection services)
    {
        services.AddSingleton<IForgeCatalog, JsonForgeCatalog>();
        services.AddSingleton<IForgeQualityEvaluator, ForgeQualityEvaluator>();
        services.AddSingleton<ForgeEngine>();
        services.AddSingleton<IForgeAudioService, ProceduralForgeAudioService>();
        services.AddSingleton<IForgeProgressStore, JsonForgeProgressStore>();
        services.AddSingleton<ForgeGraphicsSettings>();
        services.AddSingleton<ForgeWorkshopViewModel>();
        return services;
    }
}
