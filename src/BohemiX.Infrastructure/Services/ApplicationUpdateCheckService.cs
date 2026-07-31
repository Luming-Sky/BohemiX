using System.Net;
using System.Text.Json;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ApplicationUpdateCheckService(HttpClient httpClient) : IApplicationUpdateCheckService
{
    private const string RepositoryUrl = "https://github.com/Luming-Sky/BohemiX";
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Luming-Sky/BohemiX/releases/latest";
    private const string TagsApiUrl = "https://api.github.com/repos/Luming-Sky/BohemiX/tags?per_page=20";

    public async Task<ApplicationUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var parsedCurrentVersion = ParseVersion(currentVersion);

        using var releaseResponse = await httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
        if (releaseResponse.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(
                await releaseResponse.Content.ReadAsStreamAsync(cancellationToken));
            var tagName = GetRequiredString(document.RootElement, "tag_name");
            var releaseUrl = GetOptionalUri(document.RootElement, "html_url")
                ?? new Uri($"{RepositoryUrl}/releases/tag/{Uri.EscapeDataString(tagName)}");
            return CreateResult(parsedCurrentVersion, tagName, releaseUrl);
        }

        if (releaseResponse.StatusCode != HttpStatusCode.NotFound)
        {
            releaseResponse.EnsureSuccessStatusCode();
        }

        using var tagsResponse = await httpClient.GetAsync(TagsApiUrl, cancellationToken);
        tagsResponse.EnsureSuccessStatusCode();
        using var tagsDocument = JsonDocument.Parse(
            await tagsResponse.Content.ReadAsStreamAsync(cancellationToken));

        foreach (var tag in tagsDocument.RootElement.EnumerateArray())
        {
            if (!tag.TryGetProperty("name", out var nameProperty)
                || nameProperty.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameProperty.GetString()))
            {
                continue;
            }

            var tagName = nameProperty.GetString()!;
            if (!TryParseVersion(tagName, out _))
            {
                continue;
            }

            return CreateResult(
                parsedCurrentVersion,
                tagName,
                new Uri($"{RepositoryUrl}/releases/tag/{Uri.EscapeDataString(tagName)}"));
        }

        return new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.NoPublishedVersion,
            FormatVersion(parsedCurrentVersion),
            null,
            new Uri(RepositoryUrl));
    }

    private static ApplicationUpdateCheckResult CreateResult(
        Version currentVersion,
        string latestVersionText,
        Uri releaseUrl)
    {
        var latestVersion = ParseVersion(latestVersionText);
        return new ApplicationUpdateCheckResult(
            latestVersion.CompareTo(currentVersion) > 0
                ? ApplicationUpdateAvailability.UpdateAvailable
                : ApplicationUpdateAvailability.UpToDate,
            FormatVersion(currentVersion),
            FormatVersion(latestVersion),
            releaseUrl);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"GitHub response did not contain '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static Uri? GetOptionalUri(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri)
            ? uri
            : null;

    private static Version ParseVersion(string versionText)
    {
        if (!TryParseVersion(versionText, out var version))
        {
            throw new InvalidOperationException($"GitHub returned an unsupported version tag: {versionText}.");
        }

        return version;
    }

    private static bool TryParseVersion(string versionText, out Version version)
    {
        version = new Version();
        var normalized = versionText.Trim();
        if (normalized.Length > 0 && (normalized[0] is 'v' or 'V'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Split('.', StringSplitOptions.None).Length is < 2 or > 4
            || !Version.TryParse(normalized, out var parsed))
        {
            return false;
        }

        version = parsed.Build < 0
            ? new Version(parsed.Major, parsed.Minor, 0)
            : parsed;
        return true;
    }

    private static string FormatVersion(Version version) =>
        version.Revision < 0
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : $"v{version}";
}
