using System;
using System.Threading;
using System.Threading.Tasks;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BohemiX.App.Services;

internal sealed class LazyNexusEmbeddedBrowserServices :
    INexusEmbeddedBrowserAuthService,
    INexusWebDownloadLinkResolver,
    INexusWebPageResolver,
    INexusDownloadAuthorizationService
{
    private readonly Lazy<NexusEmbeddedBrowserAuthService> service;

    public LazyNexusEmbeddedBrowserServices(IServiceProvider services)
    {
        service = new Lazy<NexusEmbeddedBrowserAuthService>(
            services.GetRequiredService<NexusEmbeddedBrowserAuthService>,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal bool IsValueCreated => service.IsValueCreated;

    public Task<bool> SignInAsync(CancellationToken cancellationToken = default) =>
        service.Value.SignInAsync(cancellationToken);

    public Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        service.Value.ProbeAsync(cancellationToken);

    public Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
        Uri requestUri,
        CancellationToken cancellationToken = default) =>
        service.Value.TryCreateCookieHeaderLeaseAsync(requestUri, cancellationToken);

    public void ClearSession() => service.Value.ClearSession();

    public Task<string?> TryGenerateDownloadUrlAsync(
        string gameDomainName,
        int modId,
        int fileId,
        int gameId,
        Uri referrer,
        CancellationToken cancellationToken = default) =>
        service.Value.TryGenerateDownloadUrlAsync(
            gameDomainName,
            modId,
            fileId,
            gameId,
            referrer,
            cancellationToken);

    public Task<string?> TryLoadPageHtmlAsync(
        Uri uri,
        CancellationToken cancellationToken = default) =>
        service.Value.TryLoadPageHtmlAsync(uri, cancellationToken);

    public Task<NexusDownloadAuthorizationResult> AuthorizeAsync(
        NexusDownloadAuthorizationRequest request,
        CancellationToken cancellationToken = default) =>
        service.Value.AuthorizeAsync(request, cancellationToken);
}
