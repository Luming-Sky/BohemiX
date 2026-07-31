using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Prepares the local BohemiX Tracker mod package without modifying KCD2 core files or saves.
/// Implementations should write only inside BohemiX-managed mod/package directories unless explicitly asked to export.
/// </summary>
public interface ITrackerModPackageService
{
    /// <summary>
    /// Creates or refreshes the local Tracker mod package and returns its generated file paths.
    /// </summary>
    Task<TrackerModPackageResult> PrepareAsync(CancellationToken cancellationToken = default);
}
