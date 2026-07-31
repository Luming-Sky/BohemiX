using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface INexusCollectionService
{
    Task<NexusCollectionRevisionInfo> GetLatestPublishedRevisionAsync(
        string slug,
        bool viewAdultContent,
        CancellationToken cancellationToken = default);

    Task<NexusCollectionPackage> DownloadAndReadPackageAsync(
        NexusCollectionRevisionInfo revision,
        Guid sessionId,
        CancellationToken cancellationToken = default);
}
