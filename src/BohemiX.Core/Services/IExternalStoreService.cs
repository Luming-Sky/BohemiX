namespace BohemiX.Core.Services;

/// <summary>
/// Opens approved external storefront links from the launcher flow.
/// Implementations should use the operating system shell and avoid embedding browser-specific assumptions.
/// </summary>
public interface IExternalStoreService
{
    /// <summary>
    /// Opens the official Steam store page for Kingdom Come: Deliverance II.
    /// </summary>
    Task OpenKcd2SteamPageAsync(CancellationToken cancellationToken = default);
}

