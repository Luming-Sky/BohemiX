namespace BohemiX.Core.Models;

public sealed record DiscoveredGame(
    string Name,
    string InstallPath,
    string ExecutablePath,
    GameInstallSource Source,
    bool IsVerified);
