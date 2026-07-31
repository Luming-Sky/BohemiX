using System;
using System.Threading;

namespace BohemiX.App.ViewModels;

internal sealed class ModDownloadSearchOperationGate : IDisposable
{
    private CancellationTokenSource? cancellation;
    private long generation;

    public ModDownloadSearchOperation Begin()
    {
        Invalidate();
        cancellation = new CancellationTokenSource();
        return new ModDownloadSearchOperation(generation, cancellation.Token);
    }

    public void Invalidate()
    {
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }

    public bool IsCurrent(ModDownloadSearchOperation operation) =>
        operation.Generation == generation && !operation.CancellationToken.IsCancellationRequested;

    public void Dispose() => Invalidate();
}

internal readonly record struct ModDownloadSearchOperation(
    long Generation,
    CancellationToken CancellationToken);
