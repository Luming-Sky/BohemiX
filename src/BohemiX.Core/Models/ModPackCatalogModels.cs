namespace BohemiX.Core.Models;

public enum ModPackPlatform
{
    NexusCollection,
    SteamWorkshopCollection
}

public enum ModPackCategory
{
    VanillaPlus,
    Overhaul,
    Visuals,
    ImmersionHardcore,
    EssentialsTools
}

public enum ModPackRightsBasis
{
    OfficialPlatform
}

public enum ModPackCatalogSource
{
    Embedded,
    LastKnownGood,
    Remote
}

public sealed record ModPackCatalogEntry(
    string Id,
    string Title,
    string Summary,
    ModPackPlatform Platform,
    string PlatformIdentifier,
    string OfficialPageUrl,
    string Curator,
    ModPackCategory Category,
    IReadOnlyList<string> Tags,
    int ItemCount,
    string GameVersionNote,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ReviewedAt,
    ModPackRightsBasis RightsBasis = ModPackRightsBasis.OfficialPlatform,
    bool IsPublic = true,
    bool IsActive = true,
    bool ContainsAdultContent = false,
    string? ThumbnailUrl = null,
    bool ThumbnailIsRepresentative = false);

public sealed record ModPackCatalogDocument(
    int Version,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ModPackCatalogEntry> Entries);

public sealed record ModPackCatalogResult(
    IReadOnlyList<ModPackCatalogEntry> Entries,
    ModPackCatalogSource Source,
    string? Warning = null);

public sealed record ModPackCatalogOptions(
    string? RemoteCatalogUri = null,
    string? RemoteSignatureUri = null,
    string? PublicKeyPem = null,
    int MaximumResponseBytes = 2 * 1024 * 1024);
