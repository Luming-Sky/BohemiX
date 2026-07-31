namespace BohemiX.Core.Models;

public sealed record TrackerModInstallResult(
    bool IsInstalled,
    string Message,
    string? InstalledDirectory);
