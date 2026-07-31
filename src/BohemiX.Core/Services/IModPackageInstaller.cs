using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IModPackageInstaller
{
    Task<ModPackageInstallResult> InstallAsync(
        ModPackageInstallRequest request,
        CancellationToken cancellationToken = default);

    Task<ModPackageInstallResult> InstallPreparedDirectoryAsync(
        PreparedModPackageInstallRequest request,
        CancellationToken cancellationToken = default);
}
