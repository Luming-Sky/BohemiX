using BohemiX.Infrastructure.Services.Saves;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class ProcessCoordinatorTests
{
    [Fact]
    public async Task IsSafeToSwitch_UsesRecentWhsWritesWithoutWatchingJunctionDirectoryEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-ProcessCoordinatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var whs = Path.Combine(root, "exit.whs");
            await File.WriteAllTextAsync(whs, "save");
            File.SetLastWriteTimeUtc(whs, DateTime.UtcNow.AddMinutes(-1));
            await File.WriteAllTextAsync(Path.Combine(root, "exit-thumbnail.png"), "thumbnail");

            using var coordinator = new ProcessCoordinator(root, new LoggerConfiguration().CreateLogger(), TimeSpan.FromSeconds(3));
            Assert.True(await coordinator.IsSafeToSwitch());

            File.SetLastWriteTimeUtc(whs, DateTime.UtcNow);
            Assert.False(await coordinator.IsSafeToSwitch());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
