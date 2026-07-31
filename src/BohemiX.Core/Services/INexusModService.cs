using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface INexusModService
{
    Task<NexusGameInfo> GetGameAsync(string gameDomainName, CancellationToken cancellationToken = default);

    Task<NexusModSearchResult> SearchModsAsync(
        NexusModSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<NexusModDetails> GetModDetailsAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NexusModFile>> GetModFilesAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken = default);

    Task<NexusModFile> GetPreferredFileAsync(
        string gameDomainName,
        int modId,
        CancellationToken cancellationToken = default);

    Task<NexusDownloadLink> GetDownloadLinkAsync(
        string gameDomainName,
        int modId,
        int fileId,
        CancellationToken cancellationToken = default);

    Task<NexusDownloadLink> GetDownloadLinkAsync(
        string gameDomainName,
        int modId,
        int fileId,
        NexusDownloadAuthorization authorization,
        CancellationToken cancellationToken = default) =>
        GetDownloadLinkAsync(gameDomainName, modId, fileId, cancellationToken);
}
