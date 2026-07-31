namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// The finished product of a successful brew: identifies which recipe it came
/// from and the quality grade the player's procedure earned. A <c>null</c>
/// potion on a <see cref="BrewResult"/> means the brew was structurally
/// invalid (e.g. never received a base, or was never bottled) and produced
/// nothing — distinct from a merely low-quality potion.
/// </summary>
/// <param name="RecipeId">The <see cref="Recipe.Id"/> this potion was brewed from.</param>
/// <param name="DisplayName">Potion name, copied from the recipe for display.</param>
/// <param name="Quality">Final grade determined by the quality evaluator.</param>
/// <param name="BrewedAtUtc">When the brew was finalized.</param>
public sealed record Potion(
    string RecipeId,
    string DisplayName,
    PotionQuality Quality,
    DateTimeOffset BrewedAtUtc);
