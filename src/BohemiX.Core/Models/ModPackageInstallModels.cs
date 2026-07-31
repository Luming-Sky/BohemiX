namespace BohemiX.Core.Models;

public sealed record ModPackageInstallRequest(
    string PackagePath,
    string ModsDirectory,
    string ModId,
    string DisplayName,
    string Version,
    bool IsEnabled = true,
    ModPackageSourceMetadata? Source = null);

public enum ModPackageExistingInstallPolicy
{
    Reject,
    Replace
}

public sealed record PreparedModPackageInstallRequest(
    string PayloadDirectory,
    string SourcePackagePath,
    string ModsDirectory,
    string ModId,
    string DisplayName,
    string Version,
    bool IsEnabled = true,
    ModPackageSourceMetadata? Source = null,
    ModPackageExistingInstallPolicy ExistingInstallPolicy = ModPackageExistingInstallPolicy.Reject,
    string? ExistingRootPath = null);

public sealed record ModPackageInstallResult(
    bool Success,
    string? InstalledRootPath,
    string Message,
    Guid? TransactionId = null,
    bool RolledBack = false);
