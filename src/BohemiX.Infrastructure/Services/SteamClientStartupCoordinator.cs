using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

internal sealed class SteamClientStartupCoordinator
{
    private static readonly TimeSpan DefaultProcessStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Func<bool> isSteamClientRunning;
    private readonly Func<SteamLoginAccount?> readActiveAccount;
    private readonly Func<SteamClientLaunchResult> startSteamClient;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeSpan processStartupTimeout;
    private readonly TimeSpan loginTimeout;
    private readonly TimeSpan pollInterval;

    public SteamClientStartupCoordinator()
        : this(
            SteamLoginAccountReader.IsSteamClientRunning,
            ReadActiveAccount,
            SteamLoginAccountReader.TryStartSteamClient,
            static (duration, cancellationToken) => Task.Delay(duration, cancellationToken),
            DefaultProcessStartupTimeout,
            DefaultLoginTimeout,
            DefaultPollInterval)
    {
    }

    internal SteamClientStartupCoordinator(
        Func<bool> isSteamClientRunning,
        Func<SteamLoginAccount?> readActiveAccount,
        Func<SteamClientLaunchResult> startSteamClient,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan processStartupTimeout,
        TimeSpan loginTimeout,
        TimeSpan pollInterval)
    {
        this.isSteamClientRunning = isSteamClientRunning;
        this.readActiveAccount = readActiveAccount;
        this.startSteamClient = startSteamClient;
        this.delay = delay;
        this.processStartupTimeout = processStartupTimeout;
        this.loginTimeout = loginTimeout;
        this.pollInterval = pollInterval > TimeSpan.Zero
            ? pollInterval
            : throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public async Task<SteamLoginAccount> EnsureActiveAccountAsync(
        IProgress<SteamAccountDetectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isSteamClientRunning())
        {
            progress?.Report(new SteamAccountDetectionProgress(SteamAccountDetectionStage.StartingClient));
            var launchResult = startSteamClient();
            if (!launchResult.Started)
            {
                throw new WorkshopException(
                    launchResult.ErrorMessage ?? "Steam could not be started.",
                    WorkshopFailureKind.SteamUnavailable);
            }

            var started = await WaitUntilAsync(
                isSteamClientRunning,
                processStartupTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                throw new WorkshopException(
                    "Steam was started, but its client process did not become available within 15 seconds.",
                    WorkshopFailureKind.SteamUnavailable);
            }
        }

        progress?.Report(new SteamAccountDetectionProgress(SteamAccountDetectionStage.WaitingForLogin));
        SteamLoginAccount? activeAccount = null;
        var loggedIn = await WaitUntilAsync(
            () =>
            {
                activeAccount = readActiveAccount();
                return activeAccount is not null;
            },
            loginTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!loggedIn || activeAccount is null)
        {
            throw new WorkshopException(
                "Steam is running, but no active account was detected within 120 seconds.",
                WorkshopFailureKind.NotLoggedIn);
        }

        progress?.Report(new SteamAccountDetectionProgress(SteamAccountDetectionStage.ValidatingAccount));
        return activeAccount;
    }

    private async Task<bool> WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds / pollInterval.TotalMilliseconds));
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return true;
            }

            await delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return predicate();
    }

    private static SteamLoginAccount? ReadActiveAccount() =>
        SteamLoginAccountReader.TryReadActiveAccount(out var account) ? account : null;
}
