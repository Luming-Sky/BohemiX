using System;
using BohemiX.Core.Models;

namespace BohemiX.App.ViewModels;

public sealed class NexusModRequirementViewModel
{
    public NexusModRequirementViewModel(
        NexusModRequirement requirement,
        string nexusSourceText,
        string manualInstallText)
    {
        ModId = requirement.ModId;
        Name = string.IsNullOrWhiteSpace(requirement.ModName)
            ? requirement.ModId is { } modId ? $"Mod {modId}" : "Unnamed requirement"
            : requirement.ModName.Trim();
        Notes = requirement.Notes?.Trim();
        IsAutoDownloadAvailable = !requirement.IsExternal && requirement.ModId is not null;
        SourceText = IsAutoDownloadAvailable ? nexusSourceText : manualInstallText;
        DetailsUrl = ResolveDetailsUrl(requirement);
    }

    public int? ModId { get; }

    public string Name { get; }

    public string? Notes { get; }

    public bool IsAutoDownloadAvailable { get; }

    public string SourceText { get; }

    public string? DetailsUrl { get; }

    public bool HasDetailsUrl => !string.IsNullOrWhiteSpace(DetailsUrl);

    private static string? ResolveDetailsUrl(NexusModRequirement requirement)
    {
        if (Uri.TryCreate(requirement.Url, UriKind.Absolute, out var url)
            && (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return url.AbsoluteUri;
        }

        return requirement.ModId is { } modId && modId > 0
            ? $"https://www.nexusmods.com/kingdomcomedeliverance2/mods/{modId}"
            : null;
    }
}
