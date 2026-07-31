using System.Text.RegularExpressions;
using BohemiX.Core.Models;

namespace BohemiX.Infrastructure.Services;

public static partial class ModPackCatalogValidator
{
    private static readonly HashSet<string> NexusHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "www.nexusmods.com",
        "next.nexusmods.com"
    };

    private static readonly HashSet<string> ThumbnailHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "images.nexusmods.com",
        "media.nexusmods.com",
        "staticdelivery.nexusmods.com",
        "images.steamusercontent.com",
        "cdn.steamusercontent.com",
        "steamuserimages-a.akamaihd.net"
    };

    public static IReadOnlyList<string> Validate(ModPackCatalogDocument? document)
    {
        var errors = new List<string>();
        if (document is null)
        {
            return ["Catalog document is missing."];
        }

        if (document.Version <= 0)
        {
            errors.Add("Catalog version must be positive.");
        }

        if (document.Entries is null)
        {
            errors.Add("Catalog entries are missing.");
            return errors;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            ValidateEntry(entry, ids, errors);
        }

        return errors;
    }

    private static void ValidateEntry(ModPackCatalogEntry? entry, ISet<string> ids, ICollection<string> errors)
    {
        if (entry is null)
        {
            errors.Add("Catalog contains a null entry.");
            return;
        }

        var prefix = string.IsNullOrWhiteSpace(entry.Id) ? "<missing-id>" : entry.Id;
        if (!CatalogIdRegex().IsMatch(entry.Id ?? string.Empty))
        {
            errors.Add($"{prefix}: ID must use 3-64 lowercase letters, digits, or hyphens.");
        }
        else if (!ids.Add(entry.Id!))
        {
            errors.Add($"{prefix}: duplicate ID.");
        }

        if (string.IsNullOrWhiteSpace(entry.Title)
            || string.IsNullOrWhiteSpace(entry.Summary)
            || string.IsNullOrWhiteSpace(entry.Curator)
            || string.IsNullOrWhiteSpace(entry.GameVersionNote))
        {
            errors.Add($"{prefix}: title, summary, curator, and game-version note are required.");
        }

        if (entry.Tags is null || entry.Tags.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add($"{prefix}: tags must be present and nonblank.");
        }

        if (entry.ItemCount < 0)
        {
            errors.Add($"{prefix}: item count cannot be negative.");
        }

        if (!entry.IsPublic || !entry.IsActive || entry.RightsBasis != ModPackRightsBasis.OfficialPlatform)
        {
            errors.Add($"{prefix}: only active, public, official-platform collections are allowed.");
        }

        if (!TryValidateOfficialPage(entry, out var pageError))
        {
            errors.Add($"{prefix}: {pageError}");
        }

        if (!string.IsNullOrWhiteSpace(entry.ThumbnailUrl)
            && (!TryCreateHttpsUri(entry.ThumbnailUrl, out var thumbnailUri)
                || !ThumbnailHosts.Contains(thumbnailUri.Host)
                || HasDownloadLikePath(thumbnailUri)))
        {
            errors.Add($"{prefix}: thumbnail URL is not on an approved official image host.");
        }
    }

    private static bool TryValidateOfficialPage(ModPackCatalogEntry entry, out string error)
    {
        if (!TryCreateHttpsUri(entry.OfficialPageUrl, out var uri))
        {
            error = "official page must be an absolute HTTPS URL.";
            return false;
        }

        if (HasDownloadLikePath(uri))
        {
            error = "direct file or download URLs are not allowed.";
            return false;
        }

        if (entry.Platform == ModPackPlatform.NexusCollection)
        {
            var match = NexusCollectionPathRegex().Match(uri.AbsolutePath);
            if (!NexusHosts.Contains(uri.Host)
                || !match.Success
                || !string.Equals(match.Groups["slug"].Value, entry.PlatformIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                error = "Nexus entry must point to the declared KCD2 collection on nexusmods.com.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        if (entry.Platform == ModPackPlatform.SteamWorkshopCollection)
        {
            var id = GetQueryParameter(uri, "id");
            if (!string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/sharedfiles/filedetails", StringComparison.OrdinalIgnoreCase)
                || !ulong.TryParse(entry.PlatformIdentifier, out var declaredId)
                || declaredId == 0
                || !ulong.TryParse(id, out var urlId)
                || declaredId != urlId)
            {
                error = "Steam entry must point to the declared Steam Workshop collection.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        error = "unsupported collection platform.";
        return false;
    }

    private static bool TryCreateHttpsUri(string? value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.Port == 443;
    }

    private static bool HasDownloadLikePath(Uri uri)
    {
        var path = uri.AbsolutePath;
        return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/download", StringComparison.OrdinalIgnoreCase)
            || GetQueryParameter(uri, "download") is not null;
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }
        }

        return null;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,62}[a-z0-9])$")]
    private static partial Regex CatalogIdRegex();

    [GeneratedRegex("^/(?:games/)?kingdomcomedeliverance2/collections/(?<slug>[A-Za-z0-9_-]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex NexusCollectionPathRegex();
}
