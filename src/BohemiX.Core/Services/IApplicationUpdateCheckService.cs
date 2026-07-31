namespace BohemiX.Core.Services;

public enum ApplicationUpdateAvailability
{
    UpToDate,
    UpdateAvailable,
    NoPublishedVersion
}

public sealed record ApplicationUpdateCheckResult(
    ApplicationUpdateAvailability Availability,
    string CurrentVersion,
    string? LatestVersion,
    Uri? ReleaseUrl);

public interface IApplicationUpdateCheckService
{
    Task<ApplicationUpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);
}
