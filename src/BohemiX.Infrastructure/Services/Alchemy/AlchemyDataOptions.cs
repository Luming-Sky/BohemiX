namespace BohemiX.Infrastructure.Services.Alchemy;

/// <summary>
/// Configuration for the on-disk alchemy data store. Recipes live under
/// <c>RootDirectory/Recipes/*.json</c> and herbs under
/// <c>RootDirectory/Herbs/*.json</c>. The default location mirrors the rest
/// of BohemiX's LocalAppData layout.
/// </summary>
public sealed class AlchemyDataOptions
{
    /// <summary>
    /// Root directory containing the <c>Recipes</c> and <c>Herbs</c>
    /// subfolders. Defaults to <c>%LocalAppData%/BohemiX/alchemy</c>.
    /// </summary>
    public string RootDirectory { get; set; } = DefaultRootDirectory;

    /// <summary>
    /// When true, a missing or empty root directory is not an error — the
    /// repository simply returns an empty catalog. This lets the app run
    /// before seed data has been deployed.
    /// </summary>
    public bool AllowMissingRoot { get; set; } = true;

    /// <summary>
    /// Computed default root: <c>%LocalAppData%/BohemiX/alchemy</c>.
    /// Resolved lazily so tests setting <c>LOCALAPPDATA</c> see the right path.
    /// </summary>
    public static string DefaultRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BohemiX",
            "alchemy");
}
