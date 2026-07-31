using Avalonia;
using BohemiX.Modules.Alchemy.Controls;
using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Models;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class BottleCollectionVisualTests
{
    [Fact]
    public void NonDistilledRecipeCollectsDirectlyFromCauldron()
    {
        var recipe = AlchemyCatalog.Recipes.Single(item => item.Id == "marigold-decoction");

        var target = BottleCollectionVisual.Resolve(recipe, 34);

        Assert.Equal(BottleCollectionSource.Cauldron, target.Source);
        Assert.Equal(AlchemyHudLayout.CauldronMouth.Y + 34, target.Hitbox.Y);
        Assert.True(target.Hitbox.Contains(target.RestingCenter));
    }

    [Fact]
    public void DistilledRecipeStillCollectsFromCondenser()
    {
        var recipe = AlchemyCatalog.Recipes.Single(item => item.Id == "night-owl");

        var target = BottleCollectionVisual.Resolve(recipe, 34);

        Assert.Equal(BottleCollectionSource.Condenser, target.Source);
        Assert.Equal(AlchemyHudLayout.DistillerReceiver, target.Hitbox);
    }

    [Fact]
    public void DirectCollectionBottleStaysInsideCauldronMouth()
    {
        var recipe = AlchemyCatalog.Recipes.Single(item => item.Id == "marigold-decoction");
        var target = BottleCollectionVisual.Resolve(recipe, 0);

        var left = BottleCollectionVisual.ClampRestingCenter(target, new Point(-100, 0));
        var right = BottleCollectionVisual.ClampRestingCenter(target, new Point(3000, 0));

        Assert.True(target.Hitbox.Contains(left));
        Assert.True(target.Hitbox.Contains(right));
        Assert.True(left.X < right.X);
    }

    [Fact]
    public void DistillationRequiresBottleBeforePumpCanOperate()
    {
        var recipe = AlchemyCatalog.Recipes.Single(item => item.Id == "night-owl");

        Assert.True(BottleCollectionVisual.CanInstallCondenserBottle(
            recipe,
            RecipeStepKind.Distill,
            distilled: false));
        Assert.False(BottleCollectionVisual.CanOperatePump(
            recipe,
            RecipeStepKind.Distill,
            bottleInstalled: false));
        Assert.True(BottleCollectionVisual.CanOperatePump(
            recipe,
            RecipeStepKind.Distill,
            bottleInstalled: true));
    }

    [Fact]
    public void EmptyBottleSupplyDoesNotStartOnDistiller()
    {
        Assert.True(AlchemyHudLayout.ProductBottle.Right < AlchemyHudLayout.DistillerLever.Left);
        Assert.False(AlchemyHudLayout.ProductBottle.Intersects(AlchemyHudLayout.DistillerReceiver));
    }
}
