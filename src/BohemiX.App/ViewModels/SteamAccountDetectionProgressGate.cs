using System;
using System.Threading;
using BohemiX.Core.Services;

namespace BohemiX.App.ViewModels;

internal sealed class SteamAccountDetectionProgressGate : IProgress<SteamAccountDetectionProgress>
{
    private readonly IProgress<SteamAccountDetectionProgress> dispatcher;
    private int completed;

    public SteamAccountDetectionProgressGate(Action<SteamAccountDetectionProgress> handler)
    {
        dispatcher = new Progress<SteamAccountDetectionProgress>(value =>
        {
            if (Volatile.Read(ref completed) == 0)
            {
                handler(value);
            }
        });
    }

    public void Report(SteamAccountDetectionProgress value)
    {
        if (Volatile.Read(ref completed) == 0)
        {
            dispatcher.Report(value);
        }
    }

    public void Complete() => Interlocked.Exchange(ref completed, 1);
}
