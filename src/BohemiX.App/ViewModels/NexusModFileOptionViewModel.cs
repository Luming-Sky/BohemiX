using System;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class NexusModFileOptionViewModel : ViewModelBase
{
    public NexusModFileOptionViewModel(NexusModFile file, bool useChinese)
    {
        File = file;
        Name = string.IsNullOrWhiteSpace(file.Name) ? file.FileName : file.Name;
        FileName = string.IsNullOrWhiteSpace(file.FileName) ? "-" : file.FileName;
        VersionText = string.IsNullOrWhiteSpace(file.Version) ? "-" : $"v{file.Version}";
        CategoryText = string.IsNullOrWhiteSpace(file.Category) ? "File" : file.Category;
        SizeText = file.SizeInBytes is { } size ? FormatSize(size) : "Unknown size";
        UploadedText = file.UploadedTimestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(file.UploadedTimestamp).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "-";
        IsLegacyVersion = IsLegacyCategory(file.Category);
        LegacyVersionText = IsLegacyVersion
            ? useChinese ? "旧版本" : "Legacy version"
            : string.Empty;
        CompatibilityWarning = IsLegacyVersion
            ? useChinese
                ? "旧版本文件：不要与当前主文件同时安装。若要回退，请只选择此版本及其对应的更新。"
                : "Legacy file: do not install with the current main file. For a rollback, select this version and its matching updates only."
            : string.Empty;
        Description = CreateDescription(file, useChinese);
    }

    public NexusModFile File { get; }

    public string Name { get; }

    public string FileName { get; }

    public string VersionText { get; }

    public string CategoryText { get; }

    public string SizeText { get; }

    public string UploadedText { get; }

    public bool IsPrimary => File.IsPrimary;

    public bool IsLegacyVersion { get; }

    public string LegacyVersionText { get; }

    public string CompatibilityWarning { get; }

    public string Description { get; }

    public Action? SelectionChanged { get; set; }

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = Math.Max(0, bytes);
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{size:0} {units[unit]}"
            : $"{size:0.0} {units[unit]}";
    }

    private static string CreateDescription(NexusModFile file, bool useChinese)
    {
        var sourceDescription = NormalizeDescription(file.Description);
        if (!string.IsNullOrWhiteSpace(sourceDescription))
        {
            return sourceDescription;
        }

        var category = file.Category.ToUpperInvariant();
        if (file.IsPrimary || category.Contains("MAIN", StringComparison.Ordinal))
        {
            return useChinese
                ? "主安装包，适合首次安装和常规使用，通常包含 Mod 的完整基础内容。"
                : "Main installation package for a standard first install. It usually contains the complete base content.";
        }

        if (category.Contains("UPDATE", StringComparison.Ordinal))
        {
            return useChinese
                ? "更新包，用于在已有基础版本上安装修复或新内容。请先确认它要求的主文件版本。"
                : "Update package for fixes or newer content on top of an existing base version. Check its required main file version first.";
        }

        if (category.Contains("OPTIONAL", StringComparison.Ordinal))
        {
            return useChinese
                ? "可选包，通常提供替代功能、材质或兼容性方案。多数情况下需要先安装主文件。"
                : "Optional package, usually providing an alternative feature, texture, or compatibility variant. It commonly requires the main file.";
        }

        if (category.Contains("OLD", StringComparison.Ordinal))
        {
            return useChinese
                ? "旧版本，仅在最新版与当前游戏或其他 Mod 不兼容时，用于回退和兼容性排查。"
                : "Older version for rollback or compatibility troubleshooting when the latest release does not work with your setup.";
        }

        return useChinese
            ? "补充文件。请结合文件名、版本号和 Mod 页面说明确认它是否适合当前安装。"
            : "Supplementary file. Check its filename, version, and the Mod page notes before installing.";
    }

    private static bool IsLegacyCategory(string? category) =>
        !string.IsNullOrWhiteSpace(category)
        && (category.Contains("OLD", StringComparison.OrdinalIgnoreCase)
            || category.Contains("ARCHIVE", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(description, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"<[^>]+>", " ");
        normalized = Regex.Replace(normalized, @"\[/?[a-z][^\]]*\]", " ", RegexOptions.IgnoreCase);
        normalized = WebUtility.HtmlDecode(normalized);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized.Length <= 520 ? normalized : normalized[..517] + "...";
    }
}
