namespace BohemiX.Core.Models;

public sealed record TrackerModPackageResult(
    string ModId,
    string PackageDirectory,
    string BridgeEventsPath,
    string LuaScriptPath,
    string ManifestPath);
