using BohemiX.Modules.SaveManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BohemiX.Modules.SaveManager;

public static class ModuleStartup
{
    public static IServiceCollection AddSaveManagerModule(this IServiceCollection services)
    {
        services.AddSingleton<SaveManagerViewModel>();

        return services;
    }
}
