using System;
using System.Threading;

namespace BohemiX.App.Services;

internal static class ApplicationShutdownWatchdog
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static IDisposable Start() => Start(DefaultTimeout, () => Environment.Exit(1));

    internal static IDisposable Start(TimeSpan timeout, Action terminateProcess)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(terminateProcess);

        var cancellation = new CancellationTokenSource();

        var thread = new Thread(() =>
        {
            try
            {
                if (!cancellation.Token.WaitHandle.WaitOne(timeout))
                {
                    terminateProcess();
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "BohemiX shutdown watchdog"
        };
        thread.Start();
        return new WatchdogRegistration(cancellation);
    }

    private sealed class WatchdogRegistration(CancellationTokenSource cancellation) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
