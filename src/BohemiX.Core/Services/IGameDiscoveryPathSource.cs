namespace BohemiX.Core.Services;

/// <summary>
/// Provides local launcher and drive roots used during game discovery.
/// </summary>
public interface IGameDiscoveryPathSource
{
    string EpicManifestRoot { get; }

    IEnumerable<string> GetSteamRoots();

    IEnumerable<string> GetEpicInstallRoots();

    IEnumerable<string> GetCommonGameInstallRoots();

    IEnumerable<string> GetDriveRoots();
}
