namespace BohemiX.Core.Models;

public sealed record TrackerModHealth(
    bool IsLocalPackagePrepared,
    bool IsInstalledToGame,
    bool IsListedInModOrder,
    bool BridgeFileExists,
    DateTimeOffset? BridgeLastWriteUtc,
    string Message);
