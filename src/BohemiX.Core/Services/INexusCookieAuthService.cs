using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface INexusCookieAuthService
{
    Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
        Uri requestUri,
        CancellationToken cancellationToken = default);

    void ClearSession();
}

public interface INexusEmbeddedBrowserAuthService
{
    Task<bool> SignInAsync(CancellationToken cancellationToken = default);

    Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
        Uri requestUri,
        CancellationToken cancellationToken = default);

    void ClearSession();
}

public interface INexusWebDownloadLinkResolver
{
    Task<string?> TryGenerateDownloadUrlAsync(
        string gameDomainName,
        int modId,
        int fileId,
        int gameId,
        Uri referrer,
        CancellationToken cancellationToken = default);
}

public interface INexusWebPageResolver
{
    Task<string?> TryLoadPageHtmlAsync(
        Uri uri,
        CancellationToken cancellationToken = default);
}
