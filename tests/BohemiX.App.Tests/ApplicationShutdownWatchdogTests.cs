using BohemiX.App.Services;

namespace BohemiX.App.Tests;

public sealed class ApplicationShutdownWatchdogTests
{
    [Fact]
    public void Start_InvokesTerminationAfterTimeout()
    {
        using var terminationRequested = new ManualResetEventSlim();

        using var watchdog = ApplicationShutdownWatchdog.Start(
            TimeSpan.FromMilliseconds(20),
            terminationRequested.Set);

        Assert.True(terminationRequested.Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Dispose_CancelsPendingTermination()
    {
        using var terminationRequested = new ManualResetEventSlim();
        var watchdog = ApplicationShutdownWatchdog.Start(
            TimeSpan.FromMilliseconds(150),
            terminationRequested.Set);

        watchdog.Dispose();

        Assert.False(terminationRequested.Wait(TimeSpan.FromMilliseconds(300)));
    }
}
