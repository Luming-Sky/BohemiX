using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class WorkshopCollectionInstallCoordinatorTests
{
    [Fact]
    public async Task InstallAsyncDeduplicatesChildrenAndPreservesPartialSuccess()
    {
        var collection = CreateCollection([11, 22, 11, 33]);
        var requested = new List<WorkshopInstallRequest>();

        var result = await WorkshopCollectionInstallCoordinator.InstallAsync(
            collection,
            new WorkshopCollectionInstallRequest(collection.CollectionId, "C:\\Games\\KCD2\\Mods"),
            (request, _, _) =>
            {
                requested.Add(request);
                return request.PublishedFileId == 22
                    ? Task.FromResult(new WorkshopInstallResult(false, 22, null, null, [], "failed"))
                    : Task.FromResult(new WorkshopInstallResult(true, request.PublishedFileId, null, null, [], "installed"));
            },
            null,
            CancellationToken.None);

        Assert.Equal([11UL, 22UL, 33UL], requested.Select(request => request.PublishedFileId));
        Assert.All(requested, request => Assert.True(request.IncludeDependencies));
        Assert.Equal([11UL, 33UL], result.InstalledPublishedFileIds);
        Assert.Single(result.Failures);
        Assert.Equal(22UL, result.Failures[0].PublishedFileId);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task InstallAsyncReturnsEmptyResultWithoutInstallingCollectionContainer()
    {
        var collection = CreateCollection([]);
        var installCalls = 0;

        var result = await WorkshopCollectionInstallCoordinator.InstallAsync(
            collection,
            new WorkshopCollectionInstallRequest(collection.CollectionId, "C:\\Games\\KCD2\\Mods"),
            (_, _, _) =>
            {
                installCalls++;
                throw new InvalidOperationException();
            },
            null,
            CancellationToken.None);

        Assert.Equal(0, installCalls);
        Assert.False(result.Success);
        Assert.Empty(result.InstalledPublishedFileIds);
        Assert.Contains("no installable items", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkshopCollectionInfo CreateCollection(IReadOnlyList<ulong> children) => new(
        999,
        "Collection",
        "Summary",
        "Curator",
        null,
        DateTimeOffset.UtcNow,
        children);
}
