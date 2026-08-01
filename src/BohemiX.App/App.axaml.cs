using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.App.Views;
using BohemiX.Core.Performance;
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
    private readonly object mainWindowInitializationGate = new();
    private Task? mainWindowInitializationTask;
    private Task? shutdownTask;
    private IDisposable? shutdownWatchdog;
    private bool allowMainWindowClose;
    private int requestedExitCode;
    private int startupFailureInProgress;
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
            var appLogger = serviceProvider.GetRequiredService<ILogger>();
            PerformanceMonitor.Instance.SetSlowOperationCallback(
                (operation, durationMs) => appLogger.Warning("Slow operation: {Operation} took {Duration}ms", operation, durationMs));
            exceptionCoordinator = serviceProvider.GetRequiredService<ApplicationExceptionCoordinator>();
            exceptionCoordinator.SetFatalShutdownHandler(() => RequestFatalShutdownAsync(desktop));
            exceptionCoordinator.Attach();
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            try
            {
                var splashWindow = new StartupSplashWindow();
                splashWindow.MainWindowRevealRequested += (_, _) =>
                {
                    TryRevealMainWindow(desktop, splashWindow);
                };
                splashWindow.SplashCompleted += (_, _) =>
                {
                    TryRevealMainWindow(desktop, splashWindow);
                };
                splashWindow.Show();
                _ = PreloadMainWindowAfterSplashFrameAsync(desktop, splashWindow);
                
                // The splash window is intentionally not the main window so it cannot force itself to the foreground.
                // desktop.MainWindow is assigned when the real window is preloaded or revealed.
            }
            catch (Exception ex)
            {
                Log.Logger.Warning(ex, "Failed to show splash window, showing main window directly");
                TryRevealMainWindow(desktop);
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
                    shutdownWatchdog?.Dispose();
                    shutdownWatchdog = null;
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task PreloadMainWindowAfterSplashFrameAsync(IClassicDesktopStyleApplicationLifetime desktop, Window startupWindow)
    {
        try
        {
            await Task.Delay(80).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(
                () => PreloadMainWindow(desktop, startupWindow),
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to preload main window");
        }
        finally
        {
            _ = RevealMainWindowFallbackAsync(desktop);
        }
    }

    private async Task RevealMainWindowFallbackAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3.5)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(
                () => TryRevealMainWindow(desktop),
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to reveal the main window from the startup fallback");
        }
    }

    private void PreloadMainWindow(IClassicDesktopStyleApplicationLifetime desktop, Window startupWindow)
    {
        using var tracker = PerformanceMonitor.Instance.Track("PreloadMainWindow");
        if (serviceProvider is null || mainWindow is not null)
        {
            return;
        }

        var viewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
        var window = CreateMainWindow(desktop, viewModel);
        PrepareMainWindowForStartupTransition(window, startupWindow);
        window.PrepareStartupPreloadHidden();
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        desktop.MainWindow = window;
        window.Show();
        mainWindowViewModel = viewModel;
        mainWindow = window;
        isMainWindowNativeShown = true;
        WindowsWindowInterop.SetClickThrough(window, true);
        _ = InitializeMainWindowViewModelOnceAsync(viewModel);
    }

    private void TryRevealMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window? startupWindow = null)
    {
        if (Volatile.Read(ref startupFailureInProgress) != 0 || shutdownTask is not null)
        {
            return;
        }

        try
        {
            RevealMainWindow(desktop, startupWindow);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref startupFailureInProgress, 1) == 0)
            {
                _ = HandleStartupFailureAsync(desktop, startupWindow, ex);
            }
        }
    }

    private async Task HandleStartupFailureAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window? startupWindow,
        Exception exception)
    {
        Log.Logger.Error(exception, "Unable to reveal the main application window");
        startupWindow?.Close();
        try
        {
            if (exceptionCoordinator is not null)
            {
                await exceptionCoordinator.ReportFatalAndShutdownAsync(
                    exception,
                    "BohemiX failed to start",
                    "Main window startup");
                return;
            }
        }
        catch (Exception reportError)
        {
            Log.Logger.Error(reportError, "Unable to report the main window startup failure");
        }

        desktop.Shutdown(1);
    }

    private void RevealMainWindow(IClassicDesktopStyleApplicationLifetime desktop, Window? startupWindow = null)
    {
        using var tracker = PerformanceMonitor.Instance.Track("RevealMainWindow");
        if (serviceProvider is null || isMainWindowShown)
        {
            return;
        }

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
        isMainWindowShown = true;
        if (startupWindow is not null)
        {
            _ = CompleteStartupTransitionAsync(mainWindow, mainWindowViewModel);
        }
        else
        {
            _ = InitializeMainWindowViewModelOnceAsync(mainWindowViewModel);
        }
    }

    private MainWindow CreateMainWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel viewModel)
    {
        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Closing += (_, args) =>
        {
            if (allowMainWindowClose)
            {
                return;
            }

            args.Cancel = true;
            StartShutdown(desktop, window, viewModel);
        };
        return window;
    }

    private void StartShutdown(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        MainWindowViewModel viewModel)
    {
        if (shutdownTask is not null)
        {
            return;
        }

        window.IsEnabled = false;
        var task = PrepareAndShutdownAsync(desktop, viewModel);
        shutdownTask = task;
        _ = ObserveShutdownAsync(window, task);
    }

    private async Task PrepareAndShutdownAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.PrepareForShutdownAsync();
        }
        catch (Exception ex) when (Volatile.Read(ref requestedExitCode) != 0)
        {
            // A fatal UI failure cannot safely resume. Continue with a non-zero exit after best-effort cleanup.
            Log.Logger.Error(ex, "Application shutdown preparation failed after a fatal error");
        }

        shutdownWatchdog ??= ApplicationShutdownWatchdog.Start();
        allowMainWindowClose = true;
        desktop.Shutdown(Volatile.Read(ref requestedExitCode));
    }

    private async Task ObserveShutdownAsync(MainWindow window, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Application shutdown preparation failed");
            window.IsEnabled = true;
            if (ReferenceEquals(shutdownTask, task))
            {
                shutdownTask = null;
            }
        }
    }

    private async Task RequestFatalShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        Task? activeShutdown = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref requestedExitCode, 1);
            if (mainWindow is null || mainWindowViewModel is null)
            {
                shutdownWatchdog ??= ApplicationShutdownWatchdog.Start();
                desktop.Shutdown(1);
                return;
            }

            StartShutdown(desktop, mainWindow, mainWindowViewModel);
            activeShutdown = shutdownTask;
        });

        if (activeShutdown is not null)
        {
            await activeShutdown;
        }
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
            await Task.Delay(100);
            await InitializeMainWindowViewModelOnceAsync(viewModel);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed during startup transition");
        }
    }

    private async Task InitializeMainWindowViewModelOnceAsync(MainWindowViewModel viewModel)
    {
        if (shutdownTask is not null)
        {
            return;
        }

        Task initialization;
        lock (mainWindowInitializationGate)
        {
            initialization = mainWindowInitializationTask ??= InitializeMainWindowViewModelCoreAsync(viewModel);
        }

        try
        {
            await initialization;
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to initialize main window view model");
            lock (mainWindowInitializationGate)
            {
                if (ReferenceEquals(mainWindowInitializationTask, initialization))
                {
                    mainWindowInitializationTask = null;
                }
            }
        }
    }

    private async Task InitializeMainWindowViewModelCoreAsync(MainWindowViewModel viewModel)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await viewModel.InitializeAsync();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(viewModel.InitializeAsync);
        }

        if (!viewModel.IsInitializationReady)
        {
            throw new InvalidOperationException("The main window view model did not finish initialization.");
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
