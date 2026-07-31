using System;
using System.Threading;

namespace BohemiX.App.Services;

internal static class ApplicationShutdownWatchdog
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static int started;

    public static void Start() => Start(DefaultTimeout, () => Environment.Exit(0));

    internal static void Start(TimeSpan timeout, Action terminateProcess)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(terminateProcess);

        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            Thread.Sleep(timeout);
            terminateProcess();
        })
        {
            IsBackground = true,
            Name = "BohemiX shutdown watchdog"
        };
        thread.Start();
    }
}
