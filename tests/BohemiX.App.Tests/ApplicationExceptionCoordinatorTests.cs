using Avalonia.Headless.XUnit;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using Serilog.Core;

namespace BohemiX.App.Tests;

public sealed class ApplicationExceptionCoordinatorTests
{
    [AvaloniaFact]
    public async Task FatalFailure_IsReportedOnlyOnce()
    {
        var reporter = new RecordingErrorReporter();
        using var coordinator = new ApplicationExceptionCoordinator(
            reporter,
            new StubApplicationPathService(),
            Logger.None);
        var shutdownRequests = 0;
        coordinator.SetFatalShutdownHandler(() =>
        {
            shutdownRequests++;
            return Task.CompletedTask;
        });

        await coordinator.ReportFatalAndShutdownAsync(new InvalidOperationException("first"), "Fatal", "Test");
        await coordinator.ReportFatalAndShutdownAsync(new InvalidOperationException("second"), "Fatal", "Test");

        Assert.Single(reporter.Reports);
        Assert.Equal("first", reporter.Reports[0].Message);
        Assert.Equal(1, shutdownRequests);
    }

    [AvaloniaFact]
    public async Task FatalFailure_StillRequestsShutdownWhenReportingFails()
    {
        var reporter = new RecordingErrorReporter(throwOnReport: true);
        using var coordinator = new ApplicationExceptionCoordinator(
            reporter,
            new StubApplicationPathService(),
            Logger.None);
        var shutdownRequested = false;
        coordinator.SetFatalShutdownHandler(() =>
        {
            shutdownRequested = true;
            return Task.CompletedTask;
        });

        await coordinator.ReportFatalAndShutdownAsync(new InvalidOperationException("fatal"), "Fatal", "Test");

        Assert.True(shutdownRequested);
    }

    private sealed class RecordingErrorReporter(bool throwOnReport = false) : IApplicationErrorReporter
    {
        public List<ApplicationErrorReport> Reports { get; } = [];

        public Task ReportAsync(ApplicationErrorReport report, CancellationToken cancellationToken = default)
        {
            Reports.Add(report);
            if (throwOnReport)
            {
                throw new InvalidOperationException("reporting failed");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubApplicationPathService : IApplicationPathService
    {
        private static readonly string Root = Path.GetTempPath();

        public ApplicationPaths GetPaths() => new(
            Root,
            Path.Combine(Root, "bohemix-test.db"),
            Root,
            Root,
            Root,
            Root,
            Path.Combine(Root, "tracker-events.jsonl"),
            Path.Combine(Root, "tracker-rules.json"));

        public GlobalApplicationPaths GetGlobalPaths() => new(
            Root,
            Path.Combine(Root, "accounts.db"),
            Root,
            Root,
            Root,
            Root,
            Path.Combine(Root, "legacy.db"),
            Root,
            Root,
            Root);
    }
}
