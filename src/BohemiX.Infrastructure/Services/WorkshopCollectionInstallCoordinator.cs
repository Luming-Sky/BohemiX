using BohemiX.Core.Models;

namespace BohemiX.Infrastructure.Services;

internal static class WorkshopCollectionInstallCoordinator
{
    internal static async Task<WorkshopCollectionInstallResult> InstallAsync(
        WorkshopCollectionInfo collection,
        WorkshopCollectionInstallRequest request,
        Func<WorkshopInstallRequest, IProgress<WorkshopInstallProgress>?, CancellationToken, Task<WorkshopInstallResult>> installItemAsync,
        IProgress<WorkshopCollectionInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var childIds = collection.ChildPublishedFileIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (childIds.Length == 0)
        {
            return new WorkshopCollectionInstallResult(
                request.CollectionId,
                [],
                [],
                $"Steam Workshop collection {request.CollectionId} contains no installable items.");
        }

        var installed = new List<ulong>(childIds.Length);
        var failures = new List<WorkshopCollectionItemFailure>();
        for (var index = 0; index < childIds.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childId = childIds[index];
            progress?.Report(new WorkshopCollectionInstallProgress(
                request.CollectionId,
                childId,
                index,
                childIds.Length,
                $"Installing Workshop item {index + 1}/{childIds.Length}."));

            try
            {
                var result = await installItemAsync(
                    new WorkshopInstallRequest(childId, request.ModsDirectory, request.DeployMode, IncludeDependencies: true),
                    null,
                    cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    installed.Add(childId);
                }
                else
                {
                    failures.Add(new WorkshopCollectionItemFailure(childId, result.Message));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is WorkshopException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or InvalidOperationException)
            {
                failures.Add(new WorkshopCollectionItemFailure(childId, ex.Message));
            }
        }

        progress?.Report(new WorkshopCollectionInstallProgress(
            request.CollectionId,
            null,
            childIds.Length,
            childIds.Length,
            $"Installed {installed.Count}; failed {failures.Count}."));

        return new WorkshopCollectionInstallResult(
            request.CollectionId,
            installed,
            failures,
            $"Installed {installed.Count}/{childIds.Length} Workshop collection items; {failures.Count} failed.");
    }
}
