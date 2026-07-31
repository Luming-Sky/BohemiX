using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface ILocalGameInstallationDetector
{
    Task<IReadOnlyList<DiscoveredGame>> DetectLocalInstallationsAsync(
        CancellationToken cancellationToken = default);
}
