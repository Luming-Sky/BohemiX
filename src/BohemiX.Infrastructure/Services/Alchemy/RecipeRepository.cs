using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models.Alchemy;
using BohemiX.Core.Services.Alchemy;
using Serilog;

namespace BohemiX.Infrastructure.Services.Alchemy;

/// <summary>
/// Loads recipes and herbs from JSON files under the configured
/// <see cref="AlchemyDataOptions.RootDirectory"/>. Herbs live in
/// <c>Herbs/*.json</c> (each file an array of herb objects), recipes in
/// <c>Recipes/*.json</c> (each file a single recipe object). Uses
/// camelCase JSON binding (<see cref="JsonSerializerDefaults.Web"/>) with
/// enum-as-string and tolerant missing-root behaviour.
///
/// <para>Referential integrity is validated post-load: every
/// <c>RecipeIngredient.HerbId</c> must resolve to a loaded herb. Dangling
/// references are reported via <see cref="RecipeCatalog.Issues"/> (Warning)
/// and the recipe is still included so the UI can display it with a warning
/// badge.</para>
/// </summary>
public sealed class RecipeRepository : IRecipeRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Enums as strings keeps the JSON author-friendly and the files diffable.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNameCaseInsensitive = true
    };

    private readonly AlchemyDataOptions options;
    private readonly ILogger logger;

    public RecipeRepository(AlchemyDataOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger.ForContext<RecipeRepository>();
    }

    /// <inheritdoc/>
    public async Task<RecipeCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var root = options.RootDirectory;
        if (!Directory.Exists(root))
        {
            if (options.AllowMissingRoot)
            {
                logger.Information("Alchemy data root {Root} does not exist; returning empty catalog.", root);
                return RecipeCatalog.Empty;
            }

            logger.Error("Alchemy data root {Root} does not exist and missing roots are not allowed.", root);
            throw new DirectoryNotFoundException($"Alchemy data root not found: {root}");
        }

        var herbs = await LoadHerbsAsync(root, cancellationToken).ConfigureAwait(false);
        var (recipes, recipeIssues) = await LoadRecipesAsync(root, herbs, cancellationToken).ConfigureAwait(false);

        var issues = recipeIssues.ToList();
        if (issues.Count > 0)
        {
            logger.Warning("Loaded alchemy catalog with {IssueCount} integrity issue(s).", issues.Count);
        }
        else
        {
            logger.Information("Loaded alchemy catalog: {RecipeCount} recipe(s), {HerbCount} herb(s).", recipes.Count, herbs.Count);
        }

        return new RecipeCatalog(recipes, herbs, issues);
    }

    private async Task<IReadOnlyDictionary<string, Herb>> LoadHerbsAsync(string root, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Herb>(StringComparer.OrdinalIgnoreCase);
        var herbsDir = Path.Combine(root, "Herbs");
        if (!Directory.Exists(herbsDir))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(herbsDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var dtos = await JsonSerializer.DeserializeAsync<List<HerbDto>>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (dtos is null)
                {
                    continue;
                }

                foreach (var dto in dtos)
                {
                    if (dto.Id is null)
                    {
                        logger.Warning("Herb in {File} has no id; skipped.", file);
                        continue;
                    }
                    var herb = new Herb(dto.Id, dto.DisplayName ?? dto.Id, dto.Description ?? string.Empty);
                    result[herb.Id] = herb;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Failed to load herbs from {File}.", file);
            }
        }

        return result;
    }

    private async Task<(IReadOnlyDictionary<string, Recipe> Recipes, IEnumerable<CatalogIntegrityIssue> Issues)> LoadRecipesAsync(
        string root,
        IReadOnlyDictionary<string, Herb> herbs,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<CatalogIntegrityIssue>();
        var recipesDir = Path.Combine(root, "Recipes");
        if (!Directory.Exists(recipesDir))
        {
            return (result, issues);
        }

        foreach (var file in Directory.EnumerateFiles(recipesDir, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecipeDto? dto;
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                dto = await JsonSerializer.DeserializeAsync<RecipeDto>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.Warning(ex, "Failed to load recipe from {File}.", file);
                issues.Add(new CatalogIntegrityIssue(file, CatalogIssueSeverity.Error, $"Could not parse recipe file: {ex.Message}"));
                continue;
            }

            if (dto?.Id is null)
            {
                logger.Warning("Recipe in {File} has no id; skipped.", file);
                continue;
            }

            var (recipe, recipeIssues) = MapRecipe(dto, file, herbs);
            foreach (var issue in recipeIssues)
            {
                issues.Add(issue);
            }

            if (recipe is not null)
            {
                result[recipe.Id] = recipe;
            }
        }

        return (result, issues);
    }

    private static (Recipe? Recipe, IEnumerable<CatalogIntegrityIssue> Issues) MapRecipe(
        RecipeDto dto, string source, IReadOnlyDictionary<string, Herb> herbs)
    {
        var issues = new List<CatalogIntegrityIssue>();
        var id = dto.Id ?? string.Empty;
        var displayName = dto.DisplayName ?? id;

        if (!Enum.TryParse<AlchemyBase>(dto.Base, ignoreCase: true, out var baseKind))
        {
            issues.Add(new CatalogIntegrityIssue(source, CatalogIssueSeverity.Error,
                $"Recipe '{dto.Id}' has invalid base '{dto.Base}'."));
            return (null, issues);
        }

        var ingredients = new List<RecipeIngredient>();
        foreach (var ing in dto.Ingredients ?? [])
        {
            if (ing.HerbId is null)
            {
                issues.Add(new CatalogIntegrityIssue(source, CatalogIssueSeverity.Warning,
                    $"Recipe '{dto.Id}' has an ingredient with no herbId; skipped."));
                continue;
            }

            if (!herbs.ContainsKey(ing.HerbId))
            {
                issues.Add(new CatalogIntegrityIssue(source, CatalogIssueSeverity.Warning,
                    $"Recipe '{dto.Id}' references unknown herb '{ing.HerbId}'."));
            }

            if (!Enum.TryParse<HerbState>(ing.RequiredState, ignoreCase: true, out var state))
            {
                state = HerbState.Fresh;
            }
            if (!Enum.TryParse<HerbPreparation>(ing.RequiredPreparation, ignoreCase: true, out var prep))
            {
                prep = HerbPreparation.None;
            }

            ingredients.Add(new RecipeIngredient(ing.HerbId, Math.Max(1, ing.Count), state, prep));
        }

        var steps = (dto.Steps ?? []).Select(MapAction).Where(s => s is not null).Cast<AlchemyAction>().ToList();

        var recipe = new Recipe(
            id,
            displayName,
            baseKind,
            ingredients,
            steps,
            Math.Max(0, dto.ExpectedBoilTurns),
            dto.RequiresDistillation);

        return (recipe, issues);
    }

    private static AlchemyAction? MapAction(AlchemyActionDto dto)
    {
        if (!Enum.TryParse<AlchemyActionKind>(dto.Kind, ignoreCase: true, out var kind))
        {
            return null;
        }

        AlchemyBase? baseKind = Enum.TryParse<AlchemyBase>(dto.Base, ignoreCase: true, out var b) ? b : null;
        StirDirection? direction = Enum.TryParse<StirDirection>(dto.Direction, ignoreCase: true, out var d) ? d : null;
        HerbState? state = Enum.TryParse<HerbState>(dto.HerbState, ignoreCase: true, out var hs) ? hs : null;
        BoilMode? boilMode = Enum.TryParse<BoilMode>(dto.BoilMode, ignoreCase: true, out var bm) ? bm : null;
        AlchemyTransfer? transfer = Enum.TryParse<AlchemyTransfer>(dto.Transfer, ignoreCase: true, out var tr) ? tr : null;

        return new AlchemyAction(
            kind,
            baseKind,
            dto.HerbId,
            state,
            direction,
            dto.Turns,
            dto.StirCount,
            dto.Count,
            boilMode,
            transfer);
    }

    // --- JSON DTOs mirror the on-disk camelCase schema --------------------

    private sealed record HerbDto
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
    }

    private sealed record RecipeDto
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? Base { get; init; }
        public List<RecipeIngredientDto>? Ingredients { get; init; }
        public List<AlchemyActionDto>? Steps { get; init; }
        public int ExpectedBoilTurns { get; init; }
        public bool RequiresDistillation { get; init; }
    }

    private sealed record RecipeIngredientDto
    {
        public string? HerbId { get; init; }
        public int Count { get; init; }
        public string? RequiredState { get; init; }
        public string? RequiredPreparation { get; init; }
    }

    private sealed record AlchemyActionDto
    {
        public string? Kind { get; init; }
        public string? Base { get; init; }
        public string? HerbId { get; init; }
        public string? HerbState { get; init; }
        public string? Direction { get; init; }
        public int? Turns { get; init; }
        public int? StirCount { get; init; }
        public int? Count { get; init; }
        public string? BoilMode { get; init; }
        public string? Transfer { get; init; }
    }
}
