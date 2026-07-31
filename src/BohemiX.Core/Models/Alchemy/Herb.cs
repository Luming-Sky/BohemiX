namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// A single herb definition sourced from the bench's Herb Shelf. Herbs carry a
/// freshness state when picked; recipes declare which state they require, and
/// a mismatch reduces the freshness quality factor.
/// </summary>
/// <param name="Id">Stable lowercase identifier (e.g. <c>nettle</c>); referenced by recipes.</param>
/// <param name="DisplayName">Human-readable name, may be localized by the UI layer.</param>
/// <param name="Description">Short flavour / usage note shown in recipe book UI.</param>
public sealed record Herb(
    string Id,
    string DisplayName,
    string Description);

/// <summary>
/// A recipe's requirement for one herb: how many units, in which freshness
/// state, and with which preparation. <see cref="RequiredPreparation"/>
/// drives whether the player must Grind or Crush before adding the herb.
/// </summary>
/// <param name="HerbId">Must match a <see cref="Herb.Id"/> known to the repository.</param>
/// <param name="Count">Number of units required (e.g. belladonna x2).</param>
/// <param name="RequiredState">Whether the recipe wants fresh or dried herb.</param>
/// <param name="RequiredPreparation">None (add raw), Ground (via mortar+plate), or Crushed.</param>
public sealed record RecipeIngredient(
    string HerbId,
    int Count,
    HerbState RequiredState,
    HerbPreparation RequiredPreparation);
