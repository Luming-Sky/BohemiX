using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Provides the resolved mod-management state that UI and VFS workflows can share.
/// </summary>
public interface IModManagementSnapshotService
{
    Task<ModManagementSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default);
}
