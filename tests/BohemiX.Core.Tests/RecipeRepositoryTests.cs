using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models.Alchemy;
using BohemiX.Core.Services.Alchemy;
using BohemiX.Infrastructure.Services.Alchemy;

namespace BohemiX.Core.Tests;

public sealed class RecipeRepositoryTests : IDisposable
{
    private readonly string tempRoot;

    public RecipeRepositoryTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "bohemix-alchemy-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(tempRoot, recursive: true); }
        catch (IOException) { /* best effort on Windows file locks */ }
    }

    [Fact]
    public async Task LoadAsync_MissingRoot_ReturnsEmptyCatalogWhenAllowed()
    {
        var options = new AlchemyDataOptions { RootDirectory = Path.Combine(tempRoot, "does-not-exist"), AllowMissingRoot = true };
        var repo = new RecipeRepository(options, SerilogLogger.NoOp);

        var catalog = await repo.LoadAsync();

        Assert.Empty(catalog.Recipes);
        Assert.Empty(catalog.Herbs);
        Assert.Empty(catalog.Issues);
    }

    [Fact]
    public async Task LoadAsync_MissingRoot_ThrowsWhenNotAllowed()
    {
        var options = new AlchemyDataOptions { RootDirectory = Path.Combine(tempRoot, "does-not-exist"), AllowMissingRoot = false };
        var repo = new RecipeRepository(options, SerilogLogger.NoOp);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => repo.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_LoadsHerbsAndRecipesWithResolvedReferences()
    {
        WriteHerbs();
        WriteRecipe("chamomile-brew", "Chamomile Brew", "Water",
            herbIds: ("chamomile", "sage"));
        var repo = CreateRepo();

        var catalog = await repo.LoadAsync();

        Assert.Equal(2, catalog.Herbs.Count);
        Assert.True(catalog.Herbs.ContainsKey("chamomile"));
        Assert.Single(catalog.Recipes);
        Assert.Equal("chamomile-brew", catalog.Recipes["chamomile-brew"].Id);
        Assert.Empty(catalog.Issues);
    }

    [Fact]
    public async Task LoadAsync_DanglingHerbReference_ReportsWarningButStillLoadsRecipe()
    {
        WriteHerbs(); // only chamomile + sage
        WriteRecipe("dangling", "Dangling", "Water",
            ("chamomile", ""), ("ghost-herb", ""));
        var repo = CreateRepo();

        var catalog = await repo.LoadAsync();

        // Diagnostics to understand ingredient resolution.
        Assert.True(catalog.Recipes.ContainsKey("dangling"));
        var loaded = catalog.Recipes["dangling"];
        Assert.True(loaded.Ingredients.Count == 2, $"expected 2 ingredients, got {loaded.Ingredients.Count}");
        var warning = Assert.Single(catalog.Issues);
        Assert.Equal(CatalogIssueSeverity.Warning, warning.Severity);
        Assert.Contains("ghost-herb", warning.Message);
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ReportsErrorAndSkipsRecipe()
    {
        WriteHerbs();
        var recipesDir = Path.Combine(tempRoot, "Recipes");
        Directory.CreateDirectory(recipesDir);
        await File.WriteAllTextAsync(Path.Combine(recipesDir, "broken.json"), "{ not valid json");
        WriteRecipe("valid", "Valid", "Water", herbIds: ("chamomile", "sage"));
        var repo = CreateRepo();

        var catalog = await repo.LoadAsync();

        Assert.True(catalog.Recipes.ContainsKey("valid"));
        Assert.False(catalog.Recipes.ContainsKey("broken"));
        var error = Assert.Single(catalog.Issues);
        Assert.Equal(CatalogIssueSeverity.Error, error.Severity);
    }

    private RecipeRepository CreateRepo() =>
        new(new AlchemyDataOptions { RootDirectory = tempRoot }, SerilogLogger.NoOp);

    private void WriteHerbs()
    {
        var dir = Path.Combine(tempRoot, "Herbs");
        Directory.CreateDirectory(dir);
        var herbs = new[]
        {
            new { id = "chamomile", displayName = "Chamomile", description = "soothing" },
            new { id = "sage", displayName = "Sage", description = "aromatic" }
        };
        File.WriteAllText(Path.Combine(dir, "herbs.json"),
            JsonSerializer.Serialize(herbs, JsonOptions));
    }

    private void WriteRecipe(string id, string displayName, string @base,
        params (string HerbId, string _)[] herbIds)
    {
        var dir = Path.Combine(tempRoot, "Recipes");
        Directory.CreateDirectory(dir);
        var recipe = new
        {
            id,
            displayName,
            @base,
            ingredients = herbIds.Select(h => new
            {
                herbId = h.HerbId,
                count = 1,
                requiredState = "fresh",
                requiredPreparation = "none"
            }),
            steps = new object[]
            {
                new { kind = "pour", @base },
                new { kind = "bottle" }
            },
            expectedBoilTurns = 0,
            requiresDistillation = false
        };
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            JsonSerializer.Serialize(recipe, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
