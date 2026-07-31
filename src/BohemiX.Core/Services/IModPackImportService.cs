using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IModPackImportService
{
    Task<ModPackImportPrepareResult> PrepareAsync(
        string packagePath,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task<ModPackImportResult> ImportAsync(
        Guid sessionId,
        IReadOnlyCollection<ModPackImportSelection> selections,
        bool applyAuthorLoadOrder,
        IProgress<ModPackImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
