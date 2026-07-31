using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IModDownloader
{
    IReadOnlyCollection<ModDownloadQueueItem> Queue { get; }

    bool PauseQueueItem(string queueKey);

    bool ResumeQueueItem(string queueKey);

    bool RetryQueueItem(string queueKey);

    bool CancelQueueItem(string queueKey);

    int PauseQueue();

    int ResumeQueue();

    int CancelQueue();

    int ClearInactiveQueueItems();

    int ClearCompletedQueueItems();

    int ClearCanceledQueueItems();

    Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueWithDependenciesAsync(
        string gameDomainName,
        int modId,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFileWithDependenciesAsync(
        string gameDomainName,
        int modId,
        NexusModFile selectedFile,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFilesWithDependenciesAsync(
        string gameDomainName,
        int modId,
        IReadOnlyCollection<NexusModFile> selectedFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFilesAsync(
        string gameDomainName,
        int modId,
        IReadOnlyCollection<NexusModFile> selectedFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        EnqueueFilesWithDependenciesAsync(
            gameDomainName,
            modId,
            selectedFiles,
            destinationDirectory,
            cancellationToken);

    Task<IReadOnlyList<ModDownloadResult>> StartQueuedDownloadsAsync(
        IProgress<ModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModDownloadResult> DownloadAsync(
        NexusModDownloadRequest request,
        IProgress<ModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
