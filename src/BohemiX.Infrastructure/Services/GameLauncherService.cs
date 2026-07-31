using System.ComponentModel;
using System.Diagnostics;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class GameLauncherService : IGameLauncherService
{
    private const string Kcd2SteamProtocolUri = "steam://rungameid/1771300";

    private readonly ILogger logger;
    private readonly IGameLaunchSessionService? launchSessionService;
    private readonly IVfsSessionService? vfsSessionService;

    public GameLauncherService(ILogger logger)
    {
        this.logger = logger.ForContext<GameLauncherService>();
    }

    public GameLauncherService(
        ILogger logger,
        IGameLaunchSessionService launchSessionService,
        IVfsSessionService vfsSessionService)
        : this(logger)
    {
        this.launchSessionService = launchSessionService;
        this.vfsSessionService = vfsSessionService;
    }

    public async Task<GameLaunchResult> LaunchAsync(
        DiscoveredGame game,
        GameLaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new GameLaunchOptions();
        GameLaunchSession? session = null;

        try
        {
            session = await TryBeginSessionAsync(game, options, cancellationToken).ConfigureAwait(false);

            if (vfsSessionService is not null
                && vfsSessionService.CurrentState is not VfsSessionState.Idle and not VfsSessionState.Mounted)
            {
                var vfsMessage = vfsSessionService.LastError?.Message
                    ?? "The VFS session is unavailable, so the game launch was blocked.";
                await CompleteLaunchRecordAsync(session, false, null, vfsMessage, cancellationToken).ConfigureAwait(false);
                logger.Warning(
                    "Blocked the base game launch because VFS is in state {VfsState}",
                    vfsSessionService.CurrentState);
                return new GameLaunchResult(false, null, vfsMessage, session?.Id);
            }

            if (vfsSessionService?.CurrentState == VfsSessionState.Mounted)
            {
                var vfsResult = await vfsSessionService.LaunchProcessAsync(
                    new VfsProcessLaunchRequest(
                        game.ExecutablePath,
                        options.Arguments,
                        Path.GetDirectoryName(game.ExecutablePath)),
                    cancellationToken).ConfigureAwait(false);
                await CompleteLaunchRecordAsync(session, vfsResult.IsStarted, vfsResult.ProcessId, vfsResult.Message, cancellationToken)
                    .ConfigureAwait(false);
                return new GameLaunchResult(vfsResult.IsStarted, vfsResult.ProcessId, vfsResult.Message, session?.Id);
            }

            Process? process;
            if (options.UseSteamProtocol)
            {
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = Kcd2SteamProtocolUri,
                    UseShellExecute = true
                });

                logger.Information("Requested game launch through Steam protocol: {Uri}", Kcd2SteamProtocolUri);
            }
            else
            {
                if (!File.Exists(game.ExecutablePath))
                {
                    const string missingMessage = "Game executable was not found.";
                    await CompleteLaunchRecordAsync(session, false, null, missingMessage, cancellationToken).ConfigureAwait(false);
                    logger.Warning("Launch target does not exist: {ExecutablePath}", game.ExecutablePath);
                    return new GameLaunchResult(false, null, missingMessage, session?.Id);
                }

                process = Process.Start(new ProcessStartInfo
                {
                    FileName = game.ExecutablePath,
                    WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath),
                    Arguments = options.Arguments ?? string.Empty,
                    UseShellExecute = true
                });
            }

            var started = process is not null;
            var message = options.UseSteamProtocol ? "Launch requested through Steam." : "Launch requested.";
            await CompleteLaunchRecordAsync(session, started, process?.Id, message, cancellationToken).ConfigureAwait(false);
            logger.Information(
                "Started game process {ProcessId} from {ExecutablePath} with arguments length {ArgumentsLength}",
                process?.Id,
                game.ExecutablePath,
                options.Arguments?.Length ?? 0);
            return new GameLaunchResult(started, process?.Id, message, session?.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            if (session is not null && launchSessionService is not null)
            {
                try
                {
                    await launchSessionService.MarkFailedAsync(session.Id, ex.Message, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception persistenceError) when (persistenceError is not OperationCanceledException)
                {
                    logger.Warning(persistenceError, "Unable to mark launch session {LaunchSessionId} as failed", session.Id);
                }
            }

            logger.Error(ex, "Failed to launch game from {ExecutablePath}", game.ExecutablePath);
            return new GameLaunchResult(false, null, ex.Message, session?.Id);
        }
    }

    private async Task CompleteLaunchRecordAsync(
        GameLaunchSession? session,
        bool started,
        int? processId,
        string message,
        CancellationToken cancellationToken)
    {
        if (session is null || launchSessionService is null)
        {
            return;
        }

        try
        {
            if (started && processId is not null)
            {
                await launchSessionService.MarkRunningAsync(session.Id, processId.Value, cancellationToken).ConfigureAwait(false);
            }
            else if (!started)
            {
                await launchSessionService.MarkFailedAsync(session.Id, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Game launch succeeded but session {LaunchSessionId} could not be updated", session.Id);
        }
    }

    private async Task<GameLaunchSession?> TryBeginSessionAsync(
        DiscoveredGame game,
        GameLaunchOptions options,
        CancellationToken cancellationToken)
    {
        if (launchSessionService is null)
        {
            return null;
        }

        try
        {
            return await launchSessionService.BeginAsync(
                game,
                options,
                vfsSessionService?.SessionInfo?.SessionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Game launch session persistence is unavailable; launch will continue");
            return null;
        }
    }
}
