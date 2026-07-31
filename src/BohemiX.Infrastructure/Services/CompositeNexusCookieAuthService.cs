using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class CompositeNexusCookieAuthService : INexusCookieAuthService
{
    private readonly INexusEmbeddedBrowserAuthService? embeddedBrowserAuthService;
    private readonly NexusCookieAuthService browserCookieAuthService;
    private readonly ILogger logger;

    public CompositeNexusCookieAuthService(
        IEnumerable<INexusEmbeddedBrowserAuthService> embeddedBrowserAuthServices,
        NexusCookieAuthService browserCookieAuthService,
        ILogger logger)
    {
        embeddedBrowserAuthService = embeddedBrowserAuthServices.FirstOrDefault();
        this.browserCookieAuthService = browserCookieAuthService;
        this.logger = logger.ForContext<CompositeNexusCookieAuthService>();
    }

    public async Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (embeddedBrowserAuthService is not null)
        {
            var embeddedProbe = await embeddedBrowserAuthService.ProbeAsync(cancellationToken);
            if (embeddedProbe.Success)
            {
                return embeddedProbe;
            }

            var browserProbe = await browserCookieAuthService.ProbeAsync(cancellationToken);
            if (!browserProbe.Success)
            {
                return MergeProbeFailures(embeddedProbe, browserProbe);
            }

            return browserProbe;
        }

        return await browserCookieAuthService.ProbeAsync(cancellationToken);
    }

    public async Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
        Uri requestUri,
        CancellationToken cancellationToken = default)
    {
        NexusCookieAuthService.AssertNexusModsUri(requestUri);
        if (embeddedBrowserAuthService is not null)
        {
            var embeddedLease = await embeddedBrowserAuthService.TryCreateCookieHeaderLeaseAsync(requestUri, cancellationToken);
            if (embeddedLease is not null)
            {
                logger.Information("Using embedded Edge WebView2 Nexus Cookie auth.");
                return embeddedLease;
            }
        }

        return await browserCookieAuthService.TryCreateCookieHeaderLeaseAsync(requestUri, cancellationToken);
    }

    public void ClearSession()
    {
        embeddedBrowserAuthService?.ClearSession();
        browserCookieAuthService.ClearSession();
    }

    private static NexusCookieAuthProbeResult MergeProbeFailures(
        NexusCookieAuthProbeResult embeddedProbe,
        NexusCookieAuthProbeResult browserProbe)
    {
        var attempts = embeddedProbe.Attempts.Concat(browserProbe.Attempts).ToArray();
        var errorCode = embeddedProbe.ErrorCode ?? browserProbe.ErrorCode ?? "NEXUS_COOKIE_NOT_FOUND_OR_EXPIRED";
        var message = $"{embeddedProbe.Message} {browserProbe.Message}";
        return new NexusCookieAuthProbeResult(false, null, errorCode, message, attempts);
    }
}
