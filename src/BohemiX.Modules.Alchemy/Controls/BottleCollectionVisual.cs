using Avalonia;
using BohemiX.Modules.Alchemy.Models;

namespace BohemiX.Modules.Alchemy.Controls;

internal enum BottleCollectionSource
{
    Cauldron,
    Condenser
}

internal readonly record struct BottleCollectionTarget(
    BottleCollectionSource Source,
    Rect Hitbox,
    Point RestingCenter);

internal static class BottleCollectionVisual
{
    internal const double FillDurationSeconds = 0.68;

    public static bool RequiresDistillation(AlchemyRecipe recipe) =>
        recipe.Steps.Any(step => step.Kind == RecipeStepKind.Distill);

    public static bool CanInstallCondenserBottle(
        AlchemyRecipe recipe,
        RecipeStepKind? currentStep,
        bool distilled) =>
        RequiresDistillation(recipe)
        && currentStep == RecipeStepKind.Distill
        && !distilled;

    public static bool CanOperatePump(
        AlchemyRecipe recipe,
        RecipeStepKind? currentStep,
        bool bottleInstalled) =>
        RequiresDistillation(recipe)
        && currentStep == RecipeStepKind.Distill
        && bottleInstalled;

    public static BottleCollectionTarget Resolve(AlchemyRecipe recipe, double cauldronOffset)
    {
        if (RequiresDistillation(recipe))
        {
            return new BottleCollectionTarget(
                BottleCollectionSource.Condenser,
                AlchemyHudLayout.DistillerReceiver,
                AlchemyHudLayout.DistillerReceiver.Center);
        }

        var mouth = AlchemyHudLayout.CauldronMouth;
        var hitbox = new Rect(mouth.X, mouth.Y + cauldronOffset, mouth.Width, mouth.Height);
        return new BottleCollectionTarget(
            BottleCollectionSource.Cauldron,
            hitbox,
            new Point(820, 410 + cauldronOffset));
    }

    public static Point ClampRestingCenter(BottleCollectionTarget target, Point droppedAt)
    {
        if (target.Source == BottleCollectionSource.Condenser)
        {
            return target.RestingCenter;
        }

        return new Point(
            Math.Clamp(droppedAt.X, target.Hitbox.Left + 66, target.Hitbox.Right - 66),
            target.RestingCenter.Y);
    }
}
