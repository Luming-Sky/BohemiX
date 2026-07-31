namespace BohemiX.Core.Models;

public sealed record ModManifest(
    string Id,
    string DisplayName,
    string Version,
    string RootPath,
    int LoadOrder,
    bool IsEnabled,
    IReadOnlyList<ModFileEntry> Files,
    ModPackageSourceMetadata? Source = null);
