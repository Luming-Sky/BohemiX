using BohemiX.App.Services;

namespace BohemiX.App.Tests;

public sealed class ApplicationShutdownWatchdogTests
{
    [Fact]
    public void Start_InvokesTerminationAfterTimeout()
    {
        using var terminationRequested = new ManualResetEventSlim();

        ApplicationShutdownWatchdog.Start(
            TimeSpan.FromMilliseconds(20),
            terminationRequested.Set);

        Assert.True(terminationRequested.Wait(TimeSpan.FromSeconds(2)));
    }
}
