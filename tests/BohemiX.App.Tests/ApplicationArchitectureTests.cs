using System.Reflection;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.ViewModels;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.ViewModels;
using BohemiX.Modules.SaveManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace BohemiX.App.Tests;

public sealed class ApplicationArchitectureTests
{
    [Fact]
    public void LowerLayers_DoNotReferenceApplicationOrSiblingModules()
    {
        AssertBohemiXReferences(typeof(IAppStartupService).Assembly);
        AssertBohemiXReferences(typeof(TrackerRuntimeService).Assembly, "BohemiX.Core");
        AssertBohemiXReferences(typeof(AlchemyEngine).Assembly, "BohemiX.Core");
        AssertBohemiXReferences(typeof(ForgeEngine).Assembly, "BohemiX.Core");
        AssertBohemiXReferences(typeof(SaveManagerViewModel).Assembly, "BohemiX.Core");
    }

    [Fact]
    public void ApplicationCompositionRoot_BuildsAValidatedServiceGraph()
    {
        var services = new ServiceCollection();
        services.AddBohemiXApplication(Logger.None);

        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(ITrackerRuntimeService)));
        Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(IGameRuntimeMonitorService)));
        AssertSingletonRegistration<IModTranslationService>(services);
        AssertSingletonRegistration<IMainWindowTextCatalog>(services);
        AssertSingletonRegistration<SaveManagerViewModel>(services);
        AssertSingletonRegistration<AlchemyWorkshopViewModel>(services);
        AssertSingletonRegistration<Lazy<AlchemyWorkshopViewModel>>(services);
        AssertSingletonRegistration<ForgeWorkshopViewModel>(services);
        AssertSingletonRegistration<Lazy<ForgeWorkshopViewModel>>(services);
        AssertSingletonRegistration<MainWindowViewModel>(services);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.Same(
            provider.GetRequiredService<ITrackerRuntimeService>(),
            provider.GetRequiredService<ITrackerRuntimeService>());
        Assert.Same(
            provider.GetRequiredService<IGameRuntimeMonitorService>(),
            provider.GetRequiredService<IGameRuntimeMonitorService>());
        Assert.Same(
            provider.GetRequiredService<SaveManagerViewModel>(),
            provider.GetRequiredService<SaveManagerViewModel>());
        Assert.NotNull(provider.GetRequiredService<ApplicationExceptionCoordinator>());
        Assert.Same(
            provider.GetRequiredService<IMainWindowTextCatalog>(),
            provider.GetRequiredService<IMainWindowTextCatalog>());
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ShutdownPreparation_IsIdempotentForTheApplicationGraph()
    {
        var services = new ServiceCollection();
        services.AddBohemiXApplication(Logger.None);
        using var provider = services.BuildServiceProvider();
        var viewModel = provider.GetRequiredService<MainWindowViewModel>();

        var first = viewModel.PrepareForShutdownAsync();
        var second = viewModel.PrepareForShutdownAsync();

        Assert.Same(first, second);
        await first;
    }

    [Fact]
    public void NexusBusinessServices_DoNotCreateEmbeddedBrowserUntilFirstBrowserCall()
    {
        var services = new ServiceCollection();
        services.AddBohemiXApplication(Logger.None);

        using var provider = services.BuildServiceProvider();
        var browserProxy = provider.GetRequiredService<LazyNexusEmbeddedBrowserServices>();

        _ = provider.GetRequiredService<INexusModService>();
        _ = provider.GetRequiredService<INexusAccountService>();

        Assert.False(browserProxy.IsValueCreated);
        Assert.Same(browserProxy, provider.GetRequiredService<INexusEmbeddedBrowserAuthService>());
        Assert.Same(browserProxy, provider.GetRequiredService<INexusWebDownloadLinkResolver>());
        Assert.Same(browserProxy, provider.GetRequiredService<INexusWebPageResolver>());
    }

    private static void AssertSingletonRegistration<T>(IServiceCollection services)
    {
        var descriptor = Assert.Single(services.Where(value => value.ServiceType == typeof(T)));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private static void AssertBohemiXReferences(Assembly assembly, params string[] expected)
    {
        var actual = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("BohemiX.", StringComparison.Ordinal))
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);
    }
}
