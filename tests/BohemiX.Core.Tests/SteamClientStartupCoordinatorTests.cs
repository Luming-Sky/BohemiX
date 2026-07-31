using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class SteamClientStartupCoordinatorTests
{
    [Fact]
    public async Task EnsureActiveAccountAsync_StartsSteamAndReportsEveryStage()
    {
        var runningChecks = 0;
        var launchCount = 0;
        var stages = new List<SteamAccountDetectionStage>();
        var expected = new SteamLoginAccount(76561198000000001, "Henry");
        var coordinator = CreateCoordinator(
            isRunning: () => ++runningChecks >= 2,
            readAccount: () => expected,
            start: () =>
            {
                launchCount++;
                return SteamClientLaunchResult.Success;
            });

        var result = await coordinator.EnsureActiveAccountAsync(
            new RecordingProgress(stages),
            CancellationToken.None);

        Assert.Same(expected, result);
        Assert.Equal(1, launchCount);
        Assert.Equal(
            [
                SteamAccountDetectionStage.StartingClient,
                SteamAccountDetectionStage.WaitingForLogin,
                SteamAccountDetectionStage.ValidatingAccount
            ],
            stages);
    }

    [Fact]
    public async Task EnsureActiveAccountAsync_DoesNotRestartRunningSteam()
    {
        var coordinator = CreateCoordinator(
            isRunning: () => true,
            readAccount: () => new SteamLoginAccount(76561198000000002, "Theresa"),
            start: () => throw new InvalidOperationException("Steam must not be restarted."));

        var result = await coordinator.EnsureActiveAccountAsync(null, CancellationToken.None);

        Assert.Equal(76561198000000002UL, result.SteamId);
    }

    [Fact]
    public async Task EnsureActiveAccountAsync_ReportsLaunchFailureAsSteamUnavailable()
    {
        var coordinator = CreateCoordinator(
            isRunning: () => false,
            readAccount: () => null,
            start: () => SteamClientLaunchResult.Failure("steam.exe is missing"));

        var exception = await Assert.ThrowsAsync<WorkshopException>(() =>
            coordinator.EnsureActiveAccountAsync(null, CancellationToken.None));

        Assert.Equal(WorkshopFailureKind.SteamUnavailable, exception.FailureKind);
        Assert.Contains("steam.exe is missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureActiveAccountAsync_ReportsProcessStartupTimeoutAsSteamUnavailable()
    {
        var coordinator = CreateCoordinator(
            isRunning: () => false,
            readAccount: () => null,
            start: () => SteamClientLaunchResult.Success);

        var exception = await Assert.ThrowsAsync<WorkshopException>(() =>
            coordinator.EnsureActiveAccountAsync(null, CancellationToken.None));

        Assert.Equal(WorkshopFailureKind.SteamUnavailable, exception.FailureKind);
        Assert.Contains("15 seconds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureActiveAccountAsync_ReportsLoginTimeoutAsNotLoggedIn()
    {
        var coordinator = CreateCoordinator(
            isRunning: () => true,
            readAccount: () => null,
            start: () => SteamClientLaunchResult.Success);

        var exception = await Assert.ThrowsAsync<WorkshopException>(() =>
            coordinator.EnsureActiveAccountAsync(null, CancellationToken.None));

        Assert.Equal(WorkshopFailureKind.NotLoggedIn, exception.FailureKind);
        Assert.Contains("120 seconds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureActiveAccountAsync_StopsWaitingWhenCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            isRunning: () => true,
            readAccount: () => null,
            start: () => SteamClientLaunchResult.Success,
            delay: (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.EnsureActiveAccountAsync(null, cancellation.Token));
    }

    private static SteamClientStartupCoordinator CreateCoordinator(
        Func<bool> isRunning,
        Func<SteamLoginAccount?> readAccount,
        Func<SteamClientLaunchResult> start,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            isRunning,
            readAccount,
            start,
            delay ?? ((_, _) => Task.CompletedTask),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(1));

    private sealed class RecordingProgress(ICollection<SteamAccountDetectionStage> stages)
        : IProgress<SteamAccountDetectionProgress>
    {
        public void Report(SteamAccountDetectionProgress value) => stages.Add(value.Stage);
    }
}
