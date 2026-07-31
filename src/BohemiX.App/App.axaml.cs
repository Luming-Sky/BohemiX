using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BohemiX.App;

public partial class App : Application
{
    private ServiceProvider? serviceProvider;
    private MainWindowViewModel? mainWindowViewModel;
    private MainWindow? mainWindow;
    private bool isMainWindowNativeShown;
    private bool isMainWindowShown;
    private bool hasStartedMainWindowInitialization;
    private ApplicationExceptionCoordinator? exceptionCoordinator;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            serviceProvider = BohemiXApplicationHost.BuildServiceProvider();
            exceptionCoordinator = serviceProvider.GetRequiredService<ApplicationExceptionCoordinator>();
            exceptionCoordinator.Attach();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            try
            {
                var splashWindow = new StartupSplashWindow();
                splashWindow.MainWindowRevealRequested += (_, _) =>
                {
                    RevealMainWindow(desktop, splashWindow);
                };
                splashWindow.SplashCompleted += (_, _) =>
                {
                    RevealMainWindow(desktop, splashWindow);
                };
                splashWindow.Show();
                _ = PreloadMainWindowAfterSplashFrameAsync(desktop, splashWindow);
                
                // 不将启动窗口设为主窗口，避免它强制置顶
                // desktop.MainWindow 将在 ShowMainWindow 中设置
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Failed to show splash window, showing main window directly");
                RevealMainWindow(desktop);
            }

            desktop.Exit += (_, _) =>
            {
                exceptionCoordinator?.Detach();
                try
                {
                    serviceProvider?.Dispose();
                    serviceProvider = null;
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Application service cleanup failed during shutdown");
                }
                finally
                {
                    try
                    {
                        SharedBitmapLeaseCache.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(ex, "Shared bitmap cleanup failed during shutdown");
                    }

                    Log.CloseAndFlush();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task PreloadMainWindowAfterSplashFrameAsync(IClassicDesktopStyleApplicationLifetime desktop, Window startupWindow)
    {
        try
        {
            await Task.Delay(120);
            await Dispatcher.UIThread.InvokeAsync(
                () => PreloadMainWindow(desktop, startupWindow),
                DispatcherPriority.Background);
            _ = RevealMainWindowFallbackAsync(desktop);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to preload main window");
        }
    }

    private async Task RevealMainWindowFallbackAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await Dispatcher.UIThread.InvokeAsync(
                () => RevealMainWindow(desktop),
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to reveal the main window from the startup fallback");
        }
    }

    private void PreloadMainWindow(IClassicDesktopStyleApplicationLifetime desktop, Window startupWindow)
    {
        if (serviceProvider is null || mainWindow is not null)
        {
            return;
        }

        mainWindowViewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
        mainWindow = CreateMainWindow(desktop, mainWindowViewModel);
        PrepareMainWindowForStartupTransition(mainWindow, startupWindow);
        mainWindow.PrepareStartupPreloadHidden();
        mainWindow.ShowInTaskbar = false;
        mainWindow.ShowActivated = false;
        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        WindowsWindowInterop.SetClickThrough(mainWindow, true);
        isMainWindowNativeShown = true;
        _ = InitializeMainWindowViewModelOnceAsync(mainWindowViewModel);
    }

    private void RevealMainWindow(IClassicDesktopStyleApplicationLifetime desktop, Window? startupWindow = null)
    {
        if (serviceProvider is null || isMainWindowShown)
        {
            return;
        }

        isMainWindowShown = true;
        mainWindowViewModel ??= serviceProvider.GetRequiredService<MainWindowViewModel>();
        mainWindow ??= CreateMainWindow(desktop, mainWindowViewModel);

        if (startupWindow is not null)
        {
            PrepareMainWindowForStartupTransition(mainWindow, startupWindow);
        }

        desktop.MainWindow = mainWindow;
        mainWindow.ShowInTaskbar = true;
        mainWindow.ShowActivated = true;
        mainWindow.Opacity = 1;
        mainWindow.PrepareStartupReveal();
        WindowsWindowInterop.SetClickThrough(mainWindow, false);
        if (!isMainWindowNativeShown)
        {
            mainWindow.Show();
            isMainWindowNativeShown = true;
        }

        mainWindow.Activate();
        if (startupWindow is not null)
        {
            _ = CompleteStartupTransitionAsync(mainWindow, mainWindowViewModel);
        }
        else
        {
            _ = InitializeMainWindowViewModelOnceAsync(mainWindowViewModel);
        }
    }

    private static MainWindow CreateMainWindow(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel viewModel)
    {
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Closed += (_, _) =>
        {
            ApplicationShutdownWatchdog.Start();
            desktop.Shutdown();
        };
        return window;
    }

    private static void PrepareMainWindowForStartupTransition(MainWindow window, Window startupWindow)
    {
        window.PrepareStartupReveal();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = startupWindow.Position;
        window.Width = startupWindow.Width;
        window.Height = startupWindow.Height;
    }

    private async Task CompleteStartupTransitionAsync(MainWindow mainWindow, MainWindowViewModel viewModel)
    {
        try
        {
            await mainWindow.FadeInFromStartupAsync();
            await Task.Delay(120);
            await InitializeMainWindowViewModelOnceAsync(viewModel);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed during startup transition");
        }
    }

    private async Task InitializeMainWindowViewModelOnceAsync(MainWindowViewModel viewModel)
    {
        if (hasStartedMainWindowInitialization)
        {
            return;
        }

        hasStartedMainWindowInitialization = true;

        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to initialize main window view model");
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
