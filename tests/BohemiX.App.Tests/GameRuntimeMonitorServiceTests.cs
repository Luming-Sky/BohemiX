using System.Collections.Concurrent;
using BohemiX.App.Models;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using Serilog.Core;

namespace BohemiX.App.Tests;

public sealed class GameRuntimeMonitorServiceTests
{
    [Fact]
    public async Task Exit_UnmountsVfsAndPublishesThumbnail()
    {
        var processMonitor = new FakeGameProcessMonitorService();
        var thumbnails = new FakeThumbnailCaptureService();
        var vfs = new FakeVfsSessionService();
        var reporter = new FakeErrorReporter();
        using var service = CreateService(processMonitor, thumbnails, vfs, reporter);
        var received = new TaskCompletionSource<GameRuntimeExitUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Exited += update => received.TrySetResult(update);

        service.Start(new GameRuntimeMonitorRequest(101, Guid.NewGuid(), "C:\\Games\\KCD2.exe"));
        await processMonitor.WaitForRequestAsync(101);
        processMonitor.Complete(new GameProcessExitResult(true, 101, 0));
        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, vfs.UnmountCount);
        Assert.Equal(VfsSessionState.Idle, update.VfsState);
        Assert.Equal(new byte[] { 101 }, update.ExitThumbnail);
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task AbnormalExit_ReportsFailureAndStillPublishes()
    {
        var processMonitor = new FakeGameProcessMonitorService();
        var reporter = new FakeErrorReporter();
        using var service = CreateService(
            processMonitor,
            new FakeThumbnailCaptureService(),
            new FakeVfsSessionService(),
            reporter);
        var received = new TaskCompletionSource<GameRuntimeExitUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Exited += update => received.TrySetResult(update);

        service.Start(new GameRuntimeMonitorRequest(202, Guid.NewGuid(), "C:\\Games\\KCD2.exe"));
        await processMonitor.WaitForRequestAsync(202);
        processMonitor.Complete(new GameProcessExitResult(true, 202, -1));
        var update = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(update.ExitResult.IsAbnormalExit);
        var report = Assert.Single(reporter.Reports);
        Assert.Equal(ApplicationErrorCategory.GameProcess, report.Category);
        Assert.Equal(202, report.ProcessId);
    }

    [Fact]
    public async Task Start_CancelsPreviousMonitor()
    {
        var processMonitor = new FakeGameProcessMonitorService();
        using var service = CreateService(
            processMonitor,
            new FakeThumbnailCaptureService(),
            new FakeVfsSessionService(),
            new FakeErrorReporter());
        var updates = new ConcurrentQueue<GameRuntimeExitUpdate>();
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Exited += update =>
        {
            updates.Enqueue(update);
            if (update.ExitResult.ProcessId == 302)
            {
                secondReceived.TrySetResult();
            }
        };

        service.Start(new GameRuntimeMonitorRequest(301, null, "first.exe"));
        await processMonitor.WaitForRequestAsync(301);
        service.Start(new GameRuntimeMonitorRequest(302, null, "second.exe"));
        await processMonitor.WaitForRequestAsync(302);
        processMonitor.Complete(new GameProcessExitResult(true, 302, 0));
        await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, processMonitor.CanceledCount);
        Assert.Equal(302, Assert.Single(updates).ExitResult.ProcessId);
    }

    [Fact]
    public async Task StopAsync_CancelsMonitorWithoutPublishingExit()
    {
        var processMonitor = new FakeGameProcessMonitorService();
        var thumbnails = new FakeThumbnailCaptureService();
        using var service = CreateService(
            processMonitor,
            thumbnails,
            new FakeVfsSessionService(),
            new FakeErrorReporter());
        var published = false;
        service.Exited += _ => published = true;

        service.Start(new GameRuntimeMonitorRequest(401, null, "game.exe"));
        await processMonitor.WaitForRequestAsync(401);
        await service.StopAsync();

        Assert.False(published);
        Assert.Equal(1, processMonitor.CanceledCount);
        Assert.True(thumbnails.CancelAllCount > 0);
    }

    private static GameRuntimeMonitorService CreateService(
        FakeGameProcessMonitorService processMonitor,
        FakeThumbnailCaptureService thumbnails,
        FakeVfsSessionService vfs,
        FakeErrorReporter reporter) =>
        new(
            processMonitor,
            thumbnails,
            vfs,
            reporter,
            new FakeApplicationPathService(),
            new GameRuntimeMonitorOptions(TimeSpan.Zero),
            Logger.None);

    private sealed class FakeGameProcessMonitorService : IGameProcessMonitorService
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<GameProcessExitResult>> requests = new();
        private int canceledCount;

        public int CanceledCount => Volatile.Read(ref canceledCount);

        public async Task<bool> WaitForExitAsync(int processId, CancellationToken cancellationToken = default) =>
            (await WaitForExitResultAsync(processId, cancellationToken)).WasMonitored;

        public async Task<GameProcessExitResult> WaitForExitResultAsync(
            int processId,
            CancellationToken cancellationToken = default)
        {
            var request = requests.GetOrAdd(
                processId,
                _ => new TaskCompletionSource<GameProcessExitResult>(TaskCreationOptions.RunContinuationsAsynchronously));
            try
            {
                return await request.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref canceledCount);
                throw;
            }
        }

        public void Complete(GameProcessExitResult result) =>
            requests[result.ProcessId].TrySetResult(result);

        public async Task WaitForRequestAsync(int processId)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!requests.ContainsKey(processId))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    private sealed class FakeThumbnailCaptureService : IGameExitThumbnailCaptureService
    {
        private int cancelAllCount;

        public int CancelAllCount => Volatile.Read(ref cancelAllCount);

        public void BeginCapture(int processId)
        {
        }

        public Task<byte[]?> CompleteCaptureAsync(
            int processId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>([(byte)processId]);

        public void CancelAll() => Interlocked.Increment(ref cancelAllCount);

        public void Dispose()
        {
        }
    }

    private sealed class FakeVfsSessionService : IVfsSessionService
    {
        private int unmountCount;

        public VfsSessionState CurrentState { get; private set; } = VfsSessionState.Mounted;

        public VfsSessionInfo? SessionInfo => null;

        public Exception? LastError => null;

        public int UnmountCount => Volatile.Read(ref unmountCount);

        public Task MountAsync(VfsMountRequest request, CancellationToken cancellationToken = default)
        {
            CurrentState = VfsSessionState.Mounted;
            return Task.CompletedTask;
        }

        public Task<VfsProcessLaunchResult> LaunchProcessAsync(
            VfsProcessLaunchRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new VfsProcessLaunchResult(true, 1, "started"));

        public Task UnmountAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref unmountCount);
            CurrentState = VfsSessionState.Idle;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeErrorReporter : IApplicationErrorReporter
    {
        public ConcurrentQueue<ApplicationErrorReport> Reports { get; } = new();

        public Task ReportAsync(
            ApplicationErrorReport report,
            CancellationToken cancellationToken = default)
        {
            Reports.Enqueue(report);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApplicationPathService : IApplicationPathService
    {
        public ApplicationPaths GetPaths() => new(
            "data",
            "database",
            "logs",
            "mods",
            "native",
            "tracker",
            "events",
            "achievements");

        public GlobalApplicationPaths GetGlobalPaths() => throw new NotSupportedException();
    }
}
