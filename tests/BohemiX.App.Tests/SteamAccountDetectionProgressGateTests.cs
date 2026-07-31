using BohemiX.App.ViewModels;
using BohemiX.Core.Services;

namespace BohemiX.App.Tests;

public sealed class SteamAccountDetectionProgressGateTests
{
    [Fact]
    public void Complete_DropsProgressCallbackAlreadyQueuedForUiThread()
    {
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var received = new List<SteamAccountDetectionStage>();
            var progress = new SteamAccountDetectionProgressGate(value => received.Add(value.Stage));

            progress.Report(new SteamAccountDetectionProgress(SteamAccountDetectionStage.ValidatingAccount));
            progress.Complete();
            context.Drain();

            Assert.Empty(received);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    [Fact]
    public void Report_DeliversProgressWhileGateIsOpen()
    {
        var context = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var received = new List<SteamAccountDetectionStage>();
            var progress = new SteamAccountDetectionProgressGate(value => received.Add(value.Stage));

            progress.Report(new SteamAccountDetectionProgress(SteamAccountDetectionStage.WaitingForLogin));
            context.Drain();

            Assert.Equal([SteamAccountDetectionStage.WaitingForLogin], received);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            callbacks.Enqueue((callback, state));

        public void Drain()
        {
            while (callbacks.TryDequeue(out var queued))
            {
                queued.Callback(queued.State);
            }
        }
    }
}
