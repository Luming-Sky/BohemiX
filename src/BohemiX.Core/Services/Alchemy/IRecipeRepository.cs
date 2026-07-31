using BohemiX.Core.Models.Alchemy;

namespace BohemiX.Core.Services.Alchemy;

/// <summary>
/// Loads recipe and herb definitions from the configured alchemy data store
/// (JSON files on disk by default). Implementations are responsible for
/// referential integrity: every <c>RecipeIngredient.HerbId</c> must resolve to
/// a loaded <see cref="Herb"/>, and recipes with dangling references are
/// reported via the returned snapshot rather than thrown.
/// </summary>
public interface IRecipeRepository
{
    /// <summary>
    /// Loads all recipes and herbs, validating that every herb reference in
    /// every recipe resolves. Herb freshness/preparation requirements are not
    /// validated here — they are recipe intent, not referential integrity.
    /// </summary>
    /// <param name="cancellationToken">Propagates cancellation to disk reads.</param>
    /// <returns>A snapshot of loaded recipes, herbs, and any integrity problems found.</returns>
    Task<RecipeCatalog> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The loaded catalog: recipes keyed by id, herbs keyed by id, and a list of
/// any referential-integrity problems discovered during load (e.g. a recipe
/// names a herb that has no definition file).
/// </summary>
public sealed record RecipeCatalog(
    IReadOnlyDictionary<string, Recipe> Recipes,
    IReadOnlyDictionary<string, Herb> Herbs,
    IReadOnlyList<CatalogIntegrityIssue> Issues)
{
    /// <summary>Convenience accessor for an empty catalog (no data directory).</summary>
    public static RecipeCatalog Empty { get; } = new(
        new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, Herb>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<CatalogIntegrityIssue>());
}

/// <summary>
/// A problem found while loading the catalog. Severity ranges from
/// <c>Warning</c> (a recipe loaded but references a missing herb) to
/// <c>Error</c> (a file could not be parsed at all).
/// </summary>
public sealed record CatalogIntegrityIssue(
    string Source,
    CatalogIssueSeverity Severity,
    string Message);

/// <summary>Severity of a <see cref="CatalogIntegrityIssue"/>.</summary>
public enum CatalogIssueSeverity
{
    Warning,
    Error
}
