using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.Services;
using BohemiX.Modules.Alchemy.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BohemiX.Modules.Alchemy;

public static class ModuleStartup
{
    public static IServiceCollection AddAlchemyModule(this IServiceCollection services)
    {
        services.AddSingleton<AlchemyEngine>();
        services.AddSingleton<IAlchemyAudioService, SampledAlchemyAudioService>();
        services.AddSingleton<AlchemyWorkshopViewModel>();
        return services;
    }
}
