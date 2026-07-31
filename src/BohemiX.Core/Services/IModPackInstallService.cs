using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IModPackInstallService
{
    Task<ModPackInstallPlan> PrepareAsync(
        ModPackCatalogEntry entry,
        bool allowAdultContent,
        CancellationToken cancellationToken = default);

    Task<ModPackInstallResult> InstallAsync(
        Guid sessionId,
        IReadOnlyCollection<string> selectedOptionalItemIds,
        IProgress<ModPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModPackInstallResult> ResumeAsync(
        Guid sessionId,
        IProgress<ModPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task PauseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task CancelAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModPackInstallSession>> LoadSessionsAsync(CancellationToken cancellationToken = default);
}

public interface INexusDownloadAuthorizationService
{
    Task<NexusDownloadAuthorizationResult> AuthorizeAsync(
        NexusDownloadAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
