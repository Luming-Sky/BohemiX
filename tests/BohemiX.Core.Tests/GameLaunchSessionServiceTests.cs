using BohemiX.Core.Models;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class GameLaunchSessionServiceTests
{
    [Fact]
    public async Task SessionLifecycle_PersistsRunningAndExitState()
    {
        var root = Path.Combine(Path.GetTempPath(), "bohemix-launch-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPathService(root);
            var service = new GameLaunchSessionService(new SqliteConnectionFactory(paths), paths, Logger.None);
            var game = new DiscoveredGame(
                "KCD2",
                Path.Combine(root, "game"),
                Path.Combine(root, "game", "KingdomCome2.exe"),
                GameInstallSource.Manual,
                true);
            var vfsSessionId = Guid.NewGuid();

            var session = await service.BeginAsync(game, new GameLaunchOptions("-devmode"), vfsSessionId);
            await service.MarkRunningAsync(session.Id, 2468);
            await service.MarkExitedByProcessIdAsync(2468, 0);
            var stored = Assert.Single(await service.GetRecentAsync());

            Assert.Equal(session.Id, stored.Id);
            Assert.Equal(GameLaunchSessionStatus.Exited, stored.Status);
            Assert.Equal(2468, stored.ProcessId);
            Assert.Equal(0, stored.ExitCode);
            Assert.Equal(vfsSessionId, stored.VfsSessionId);
            Assert.NotNull(stored.StartedAtUtc);
            Assert.NotNull(stored.ExitedAtUtc);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FailedSession_IsClosedWithReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "bohemix-launch-session-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPathService(root);
            var service = new GameLaunchSessionService(new SqliteConnectionFactory(paths), paths, Logger.None);
            var game = new DiscoveredGame("KCD2", root, Path.Combine(root, "missing.exe"), GameInstallSource.Manual, false);

            var session = await service.BeginAsync(game, new GameLaunchOptions(), null);
            await service.MarkFailedAsync(session.Id, "missing executable");
            var stored = Assert.Single(await service.GetRecentAsync());

            Assert.Equal(GameLaunchSessionStatus.Failed, stored.Status);
            Assert.Equal("missing executable", stored.FailureMessage);
            Assert.NotNull(stored.ExitedAtUtc);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
