using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Monitors KCD2 save files and streams debounced change events to the parsing pipeline.
/// Implementations should use background processing and never block the UI thread while saves are written.
/// </summary>
public interface ISaveMonitorService
{
    /// <summary>
    /// Watches a save directory for `.whs` changes until the caller cancels enumeration.
    /// </summary>
    IAsyncEnumerable<SaveFileChange> WatchAsync(string saveDirectory, CancellationToken cancellationToken = default);
}

