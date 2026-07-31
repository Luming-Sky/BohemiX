using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.App.Services;

public sealed class ApplicationExceptionCoordinator : IDisposable
{
    private readonly IApplicationErrorReporter errorReporter;
    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;
    private int attached;

    public ApplicationExceptionCoordinator(
        IApplicationErrorReporter errorReporter,
        IApplicationPathService applicationPathService,
        ILogger logger)
    {
        this.errorReporter = errorReporter;
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<ApplicationExceptionCoordinator>();
    }

    public void Attach()
    {
        if (Interlocked.Exchange(ref attached, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;
    }

    public void Detach()
    {
        if (Interlocked.Exchange(ref attached, 0) == 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException -= OnUiThreadUnhandledException;
    }

    private void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _ = ReportAsync(e.Exception, "界面线程发生未处理异常", "Avalonia UI 线程");
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"Unhandled exception object: {e.ExceptionObject}");
        var presentation = ReportAsync(exception, "应用发生严重错误", "AppDomain 未处理异常");
        if (Dispatcher.UIThread.CheckAccess())
        {
            return;
        }

        try
        {
            presentation.GetAwaiter().GetResult();
        }
        catch (Exception presentationError)
        {
            logger.Error(presentationError, "Unable to present a fatal application error");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        _ = ReportAsync(e.Exception, "后台任务发生未处理异常", "TaskScheduler 后台任务");
    }

    private Task ReportAsync(Exception exception, string title, string source)
    {
        logger.Error(exception, "{UnhandledErrorSource}", source);
        var report = ApplicationErrorReport.FromException(
            exception,
            title,
            exception.Message,
            source,
            logPath: applicationPathService.GetPaths().LogsDirectory);
        return errorReporter.ReportAsync(report);
    }

    public void Dispose() => Detach();
}
