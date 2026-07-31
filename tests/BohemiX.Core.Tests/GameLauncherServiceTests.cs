using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class GameLauncherServiceTests
{
    [Fact]
    public async Task LaunchAsync_BlocksBaseGameWhenVfsMountFailed()
    {
        var sessions = new FakeLaunchSessionService();
        var vfs = new FaultedVfsSessionService();
        var launcher = new GameLauncherService(Logger.None, sessions, vfs);
        var game = new DiscoveredGame(
            "KCD2",
            @"D:\Games\KCD2",
            @"D:\Games\KCD2\KingdomCome2.exe",
            GameInstallSource.Manual,
            true);

        var result = await launcher.LaunchAsync(game);

        Assert.False(result.IsStarted);
        Assert.Equal("mount failed", result.Message);
        Assert.Equal(0, vfs.LaunchCalls);
        Assert.Equal("mount failed", sessions.FailureMessage);
    }

    private sealed class FaultedVfsSessionService : IVfsSessionService
    {
        public VfsSessionState CurrentState => VfsSessionState.Faulted;
        public VfsSessionInfo? SessionInfo => null;
        public Exception? LastError { get; } = new InvalidOperationException("mount failed");
        public int LaunchCalls { get; private set; }

        public Task MountAsync(VfsMountRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<VfsProcessLaunchResult> LaunchProcessAsync(
            VfsProcessLaunchRequest request,
            CancellationToken cancellationToken = default)
        {
            LaunchCalls++;
            return Task.FromResult(new VfsProcessLaunchResult(true, 42, "unexpected"));
        }

        public Task UnmountAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeLaunchSessionService : IGameLaunchSessionService
    {
        public string? FailureMessage { get; private set; }

        public Task<GameLaunchSession> BeginAsync(
            DiscoveredGame game,
            GameLaunchOptions options,
            Guid? vfsSessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GameLaunchSession(
                Guid.NewGuid(),
                Guid.Empty,
                game.Name,
                game.ExecutablePath,
                options.Arguments,
                options.UseSteamProtocol,
                vfsSessionId,
                null,
                GameLaunchSessionStatus.Requested,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                null));

        public Task MarkRunningAsync(Guid sessionId, int processId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkFailedAsync(Guid sessionId, string message, CancellationToken cancellationToken = default)
        {
            FailureMessage = message;
            return Task.CompletedTask;
        }

        public Task MarkExitedByProcessIdAsync(
            int processId,
            int? exitCode,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkMonitoringLostAsync(
            int processId,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GameLaunchSession>> GetRecentAsync(
            int limit = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameLaunchSession>>([]);
    }
}
