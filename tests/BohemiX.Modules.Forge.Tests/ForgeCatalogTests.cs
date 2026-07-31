using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Services;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeCatalogTests
{
    [Fact]
    public void CatalogContainsThreeDistinctRecipesAndMaterials()
    {
        var catalog = new ForgeCatalog();

        Assert.Equal(3, catalog.Recipes.Count);
        Assert.Equal(3, catalog.Materials.Count);
        Assert.Equal(new[] { "duelling-longsword", "basilard", "bearded-axe" }, catalog.Recipes.Select(item => item.Id));
        Assert.All(catalog.Recipes, recipe => Assert.NotEmpty(recipe.Zones));
        Assert.All(catalog.Recipes, recipe => Assert.Equal(3, recipe.RecommendedQuenchByMaterial.Count));
        Assert.Equal(new[] { "duelling-longsword", "basilard", "bearded-axe" }, catalog.Recipes.Select(item => item.Shape.Kind));
    }

    [Fact]
    public void HighCarbonSteelUsesOilForEveryLaunchRecipe()
    {
        var catalog = new ForgeCatalog();

        Assert.All(catalog.Recipes, recipe =>
            Assert.Equal(QuenchMedium.Oil, recipe.RecommendedQuenchByMaterial["high-carbon-steel"]));
    }

    [Fact]
    public void BeardedAxeUsesASolidHaftSeatWithoutAnEyeVoid()
    {
        var recipe = new ForgeCatalog().GetRecipe("bearded-axe");

        Assert.Equal(0, recipe.Shape.EyeWidth);
        Assert.Equal(0, recipe.Shape.EyeHeight);
        Assert.Contains(recipe.Zones, zone => zone.Id == "haft-seat" && zone.LabelZh == "斧柄座");
        Assert.DoesNotContain(recipe.Zones, zone => zone.Id == "eye");
    }

    [Fact]
    public void EmbeddedJsonCatalogIsValidAndComplete()
    {
        var catalog = new JsonForgeCatalog();

        Assert.Equal(3, catalog.Recipes.Count);
        Assert.Equal(3, catalog.Materials.Count);
        Assert.Equal("smith-hammer", catalog.Hammer.Id);
        Assert.All(catalog.Recipes.SelectMany(recipe => recipe.Zones), zone =>
        {
            Assert.InRange(zone.YMin, 0, 1);
            Assert.InRange(zone.YMax, 0, 1);
            Assert.True(zone.YMin < zone.YMax);
            Assert.NotNull(zone.RecommendedFace);
        });
    }

    [Fact]
    public void Kcd2RecipesExposeLocalizedVisualAndOutlineDefinitions()
    {
        var catalog = new ForgeCatalog();

        Assert.All(catalog.Recipes, recipe =>
        {
            Assert.False(string.IsNullOrWhiteSpace(recipe.NameZh));
            Assert.False(string.IsNullOrWhiteSpace(recipe.DescriptionZh));
            Assert.NotNull(recipe.Visual);
            Assert.EndsWith(".glb", recipe.Visual!.FinishedModelAsset);
            Assert.EndsWith(".glb", recipe.Visual.FittingsModelAsset);
            Assert.EndsWith(".png", recipe.Visual.ThumbnailAsset);
            Assert.NotNull(recipe.Shape.Outline);
            Assert.NotNull(recipe.Billet);
            Assert.True(recipe.Billet!.Start < recipe.Billet.End);
            Assert.True(recipe.Billet.Outline.Count >= 2);
            Assert.True(recipe.Shape.Outline!.Count >= 6);
            Assert.Equal(0, recipe.Shape.Outline[0].U);
            Assert.Equal(1, recipe.Shape.Outline[^1].U);
            Assert.All(recipe.Zones, zone => Assert.False(string.IsNullOrWhiteSpace(zone.LabelZh)));
        });
    }

    [Fact]
    public void LegacyRecipeProgressMigratesToKcd2RecipeIds()
    {
        var profile = new ForgeProfile(
            1,
            [new ForgeHistoryEntry(DateTimeOffset.UnixEpoch, "axe", "soft-steel", ForgeQuality.Fine, 70)],
            new Dictionary<string, int> { ["longsword:balanced-steel"] = 82, ["basilard:balanced-steel"] = 86 });

        var migrated = JsonForgeProgressStore.MigrateRecipeIds(profile);

        Assert.Equal("bearded-axe", migrated.History[0].RecipeId);
        Assert.Equal(82, migrated.BestScores["duelling-longsword:balanced-steel"]);
        Assert.Equal(86, migrated.BestScores["basilard:balanced-steel"]);
    }
}
