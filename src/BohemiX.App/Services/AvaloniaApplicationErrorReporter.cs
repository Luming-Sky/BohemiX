using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using BohemiX.App.Views;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.App.Services;

public sealed class AvaloniaApplicationErrorReporter : IApplicationErrorReporter
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    private readonly ILogger logger;
    private readonly SemaphoreSlim dialogGate = new(1, 1);
    private readonly object duplicateGate = new();
    private string? lastFingerprint;
    private DateTimeOffset lastReportedAtUtc;

    public AvaloniaApplicationErrorReporter(ILogger logger)
    {
        this.logger = logger.ForContext<AvaloniaApplicationErrorReporter>();
    }

    public async Task ReportAsync(ApplicationErrorReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        logger.Error(
            "Application error {ErrorCategory} at {ErrorLocation}: {ErrorMessage}. Session={SessionId}, Process={ProcessId}, ExitCode={ExitCode}. Details: {TechnicalDetails}",
            report.Category,
            report.Location,
            report.Message,
            report.SessionId,
            report.ProcessId,
            report.ExitCode,
            report.TechnicalDetails);

        if (IsRecentDuplicate(report))
        {
            return;
        }

        await dialogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                await ShowDialogCoreAsync(report).ConfigureAwait(true);
                return;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await ShowDialogCoreAsync(report);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unable to display the application error dialog");
        }
        finally
        {
            dialogGate.Release();
        }
    }

    private static async Task ShowDialogCoreAsync(ApplicationErrorReport report)
    {
        var dialog = new ErrorDialogWindow(report);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var owner = desktop.Windows.FirstOrDefault(window =>
                window is not ErrorDialogWindow
                && window.IsVisible
                && window.WindowState != WindowState.Minimized);
            if (owner is not null)
            {
                await dialog.ShowDialog(owner);
                return;
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Closed += (_, _) => completion.TrySetResult();
        dialog.Topmost = true;
        dialog.Show();
        dialog.Activate();
        await completion.Task;
    }

    private bool IsRecentDuplicate(ApplicationErrorReport report)
    {
        var fingerprint = $"{report.Category}|{report.Message}|{report.Location}|{report.ProcessId}|{report.ExitCode}";
        var now = DateTimeOffset.UtcNow;
        lock (duplicateGate)
        {
            if (string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal)
                && now - lastReportedAtUtc <= DuplicateWindow)
            {
                return true;
            }

            lastFingerprint = fingerprint;
            lastReportedAtUtc = now;
            return false;
        }
    }
}
