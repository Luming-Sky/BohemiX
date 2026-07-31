using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Services;
using BohemiX.Modules.Forge.ViewModels;

namespace BohemiX.ForgeHost;

public sealed partial class App : Application
{
    private ForgeWorkshopViewModel? viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var catalog = new JsonForgeCatalog();
            var engine = new ForgeEngine(catalog, new ForgeQualityEvaluator());
            viewModel = new ForgeWorkshopViewModel(
                engine,
                catalog,
                new ProceduralForgeAudioService(),
                new JsonForgeProgressStore(new ForgeHostApplicationPathService()));
            var window = new ForgeHostWindow(viewModel);
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => viewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
