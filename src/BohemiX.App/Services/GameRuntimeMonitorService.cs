using System;
using System.Threading;
using System.Threading.Tasks;
using BohemiX.App.Models;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.App.Services;

public sealed class GameRuntimeMonitorService : IGameRuntimeMonitorService, IDisposable
{
    private readonly IGameProcessMonitorService processMonitorService;
    private readonly IGameExitThumbnailCaptureService thumbnailCaptureService;
    private readonly IVfsSessionService vfsSessionService;
    private readonly IApplicationErrorReporter errorReporter;
    private readonly IApplicationPathService applicationPathService;
    private readonly GameRuntimeMonitorOptions options;
    private readonly ILogger logger;
    private readonly object lifecycleGate = new();
    private CancellationTokenSource? monitorCancellation;
    private Task? monitorTask;
    private int disposed;

    public GameRuntimeMonitorService(
        IGameProcessMonitorService processMonitorService,
        IGameExitThumbnailCaptureService thumbnailCaptureService,
        IVfsSessionService vfsSessionService,
        IApplicationErrorReporter errorReporter,
        IApplicationPathService applicationPathService,
        GameRuntimeMonitorOptions options,
        ILogger logger)
    {
        this.processMonitorService = processMonitorService;
        this.thumbnailCaptureService = thumbnailCaptureService;
        this.vfsSessionService = vfsSessionService;
        this.errorReporter = errorReporter;
        this.applicationPathService = applicationPathService;
        this.options = options;
        this.logger = logger.ForContext<GameRuntimeMonitorService>();
    }

    public event Action<GameRuntimeExitUpdate>? Exited;

    public void Start(GameRuntimeMonitorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ProcessId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        lock (lifecycleGate)
        {
            monitorCancellation?.Cancel();
            thumbnailCaptureService.CancelAll();

            var cancellation = new CancellationTokenSource();
            monitorCancellation = cancellation;
            thumbnailCaptureService.BeginCapture(request.ProcessId);
            monitorTask = Task.Run(() => MonitorAsync(request, cancellation));
        }
    }

    public async Task StopAsync()
    {
        Task? task;
        lock (lifecycleGate)
        {
            monitorCancellation?.Cancel();
            task = monitorTask;
        }

        thumbnailCaptureService.CancelAll();
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MonitorAsync(
        GameRuntimeMonitorRequest request,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            var exitResult = await processMonitorService
                .WaitForExitResultAsync(request.ProcessId, token)
                .ConfigureAwait(false);
            if (!exitResult.WasMonitored || token.IsCancellationRequested)
            {
                await thumbnailCaptureService
                    .CompleteCaptureAsync(request.ProcessId, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(exitResult.ErrorMessage))
                {
                    await ReportSafelyAsync(CreateMonitorFailureReport(request, exitResult.ErrorMessage))
                        .ConfigureAwait(false);
                }

                return;
            }

            var exitThumbnail = await thumbnailCaptureService
                .CompleteCaptureAsync(request.ProcessId, token)
                .ConfigureAwait(false);
            if (exitResult.IsAbnormalExit && exitResult.ExitCode is not null)
            {
                await ReportSafelyAsync(ApplicationErrorReport.GameProcessExit(
                        request.ProcessId,
                        exitResult.ExitCode.Value,
                        request.ExecutablePath,
                        applicationPathService.GetPaths().LogsDirectory,
                        request.SessionId))
                    .ConfigureAwait(false);
            }

            await Task.Delay(options.PostExitDelay, token).ConfigureAwait(false);
            await vfsSessionService.UnmountAsync(token).ConfigureAwait(false);
            Publish(new GameRuntimeExitUpdate(exitResult, exitThumbnail, vfsSessionService.CurrentState));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await ReportSafelyAsync(ApplicationErrorReport.FromException(
                    ex,
                    "游戏退出处理失败",
                    ex.Message,
                    "Kingdom Come: Deliverance II 运行结束",
                    ApplicationErrorCategory.GameProcess,
                    $"游戏进程编号 {request.ProcessId}\n游戏启动文件：{request.ExecutablePath}",
                    applicationPathService.GetPaths().LogsDirectory,
                    request.SessionId,
                    request.ProcessId))
                .ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycleGate)
            {
                if (ReferenceEquals(monitorCancellation, cancellation))
                {
                    monitorCancellation = null;
                    monitorTask = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private ApplicationErrorReport CreateMonitorFailureReport(
        GameRuntimeMonitorRequest request,
        string errorMessage) =>
        new(
            ApplicationErrorCategory.GameProcess,
            "无法继续查看游戏运行状态",
            errorMessage,
            $"游戏进程编号 {request.ProcessId}\n游戏启动文件：{request.ExecutablePath}",
            "Kingdom Come: Deliverance II 运行状态",
            $"Process ID: {request.ProcessId}\nLaunch session: {request.SessionId?.ToString("D") ?? "not available"}\n{errorMessage}",
            DateTimeOffset.UtcNow,
            applicationPathService.GetPaths().LogsDirectory,
            request.SessionId,
            request.ProcessId);

    private async Task ReportSafelyAsync(ApplicationErrorReport report)
    {
        try
        {
            await errorReporter.ReportAsync(report, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unable to report game runtime failure");
        }
    }

    private void Publish(GameRuntimeExitUpdate update)
    {
        var handlers = Exited;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<GameRuntimeExitUpdate> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(update);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Game runtime exit subscriber failed");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
    }
}
