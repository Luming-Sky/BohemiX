namespace BohemiX.Core.Models;

public sealed record GameLaunchOptions(
    string? Arguments = null,
    bool UseSteamProtocol = false);
