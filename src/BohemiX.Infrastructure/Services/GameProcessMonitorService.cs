using System.Diagnostics;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class GameProcessMonitorService : IGameProcessMonitorService
{
    private readonly ILogger logger;
    private readonly IGameLaunchSessionService? launchSessionService;

    public GameProcessMonitorService(ILogger logger)
    {
        this.logger = logger.ForContext<GameProcessMonitorService>();
    }

    public GameProcessMonitorService(ILogger logger, IGameLaunchSessionService launchSessionService)
        : this(logger)
    {
        this.launchSessionService = launchSessionService;
    }

    public async Task<bool> WaitForExitAsync(int processId, CancellationToken cancellationToken = default)
    {
        var result = await WaitForExitResultAsync(processId, cancellationToken).ConfigureAwait(false);
        return result.WasMonitored;
    }

    public async Task<GameProcessExitResult> WaitForExitResultAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            logger.Information("Monitoring game process {ProcessId} for exit", processId);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            int? exitCode = null;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
            }

            if (launchSessionService is not null)
            {
                try
                {
                    await launchSessionService.MarkExitedByProcessIdAsync(processId, exitCode, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Warning(ex, "Process {ProcessId} exited but its launch session could not be closed", processId);
                }
            }

            logger.Information("Game process {ProcessId} exited with code {ExitCode}", processId, exitCode);
            return new GameProcessExitResult(true, processId, exitCode);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            if (launchSessionService is not null)
            {
                try
                {
                    await launchSessionService.MarkMonitoringLostAsync(processId, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception persistenceError) when (persistenceError is not OperationCanceledException)
                {
                    logger.Warning(persistenceError, "Unable to mark monitoring loss for process {ProcessId}", processId);
                }
            }

            logger.Warning(ex, "Unable to monitor game process {ProcessId}", processId);
            return new GameProcessExitResult(false, processId, null, ex.Message);
        }
    }
}
