using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Models;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class AlchemyLocalizationTests
{
    [Fact]
    public void CatalogSwitchesRecipesIngredientsAndFeedbackBetweenLanguages()
    {
        var recipe = AlchemyCatalog.Recipes[0];
        var ingredient = AlchemyCatalog.Ingredients[0];

        Assert.Equal("夜鹰药剂", AlchemyTextCatalog.Content(recipe.Name, useEnglish: false));
        Assert.Equal("金盏花", AlchemyTextCatalog.Content(ingredient.Name, useEnglish: false));
        Assert.Equal("Night Owl Potion", AlchemyTextCatalog.Content(recipe.Name, useEnglish: true));
        Assert.Equal("Marigold", AlchemyTextCatalog.Content(ingredient.Name, useEnglish: true));
        Assert.Contains(
            "Choose a base liquid",
            AlchemyTextCatalog.EngineMessage("选择基液，开始新的炼制。", useEnglish: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BottlingHintMatchesTheRecipeProcessingPath()
    {
        var directRecipe = AlchemyCatalog.Recipes.Single(item => item.Id == "marigold-decoction");
        var directStep = directRecipe.Steps.Single(item => item.Kind == RecipeStepKind.Bottle);
        var distilledRecipe = AlchemyCatalog.Recipes.Single(item => item.Id == "night-owl");
        var distilledStep = distilledRecipe.Steps.Single(item => item.Kind == RecipeStepKind.Bottle);

        Assert.Contains("cauldron", AlchemyTextCatalog.StepGestureHint(directStep, true, bottleFromCauldron: true));
        Assert.Contains("condenser", AlchemyTextCatalog.StepGestureHint(distilledStep, true));
    }
}
