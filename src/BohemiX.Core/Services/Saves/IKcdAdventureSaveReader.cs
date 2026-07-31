using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Reads spoiler-free aggregate progress from a KCD2 save playline without mutating it.
/// </summary>
public interface IKcdAdventureSaveReader
{
    Task<KcdAdventureSaveData> ReadAsync(
        string saveDirectory,
        string? gameInstallDirectory,
        CancellationToken cancellationToken = default);
}
