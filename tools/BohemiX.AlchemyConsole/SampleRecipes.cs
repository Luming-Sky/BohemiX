using BohemiX.Core.Models.Alchemy;

namespace BohemiX.AlchemyConsole;

/// <summary>
/// Sample recipes matching the real KCD2 alchemy mechanics, inlined so the
/// console runs with no on-disk seed dependency. Kept in sync with the App's
/// AlchemySampleRecipes. See that file for mechanic documentation.
/// </summary>
internal static class SampleRecipes
{
    /// <summary>Saviour Schnapps — boil 2 turns with bellows, bottle directly (no distillation).</summary>
    public static Recipe SaviourSchnapps { get; } = new(
        "saviour-schnapps",
        "Saviour Schnapps",
        AlchemyBase.Wine,
        [
            new RecipeIngredient("nettle", 1, HerbState.Fresh, HerbPreparation.None),
            new RecipeIngredient("belladonna", 2, HerbState.Fresh, HerbPreparation.Ground)
        ],
        PerfectSaviourSchnapps,
        ExpectedBoilTurns: 2,
        RequiresDistillation: false);

    /// <summary>Chamomile Brew — boil 1 turn with bellows, bottle directly.</summary>
    public static Recipe ChamomileBrew { get; } = new(
        "chamomile-brew",
        "Chamomile Brew",
        AlchemyBase.Water,
        [
            new RecipeIngredient("chamomile", 1, HerbState.Fresh, HerbPreparation.None),
            new RecipeIngredient("sage", 1, HerbState.Fresh, HerbPreparation.Ground)
        ],
        PerfectChamomileBrew,
        ExpectedBoilTurns: 1,
        RequiresDistillation: false);

    /// <summary>Cockerel Potion — mixed boil: 1 bellows turn + 2 plain turns.</summary>
    public static Recipe CockerelPotion { get; } = new(
        "cockerel-potion",
        "Cockerel Potion (mixed boil)",
        AlchemyBase.Water,
        [
            new RecipeIngredient("dandelion", 1, HerbState.Fresh, HerbPreparation.None),
            new RecipeIngredient("wormwood", 1, HerbState.Fresh, HerbPreparation.Ground)
        ],
        PerfectCockerelPotion,
        ExpectedBoilTurns: 3,
        RequiresDistillation: false);

    public static IReadOnlyList<Recipe> All => [SaviourSchnapps, ChamomileBrew, CockerelPotion];

    public static IReadOnlyList<AlchemyAction> PerfectSaviourSchnapps =>
    [
        AlchemyAction.PourBase(AlchemyBase.Wine),
        AlchemyAction.Grind("belladonna", HerbState.Fresh, 2),
        AlchemyAction.Pour(),
        AlchemyAction.AddHerb("nettle", HerbState.Fresh),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.PullBellows(),
        AlchemyAction.BoilTurns(2, BoilMode.Bellows),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Bottle()
    ];

    public static IReadOnlyList<AlchemyAction> PerfectChamomileBrew =>
    [
        AlchemyAction.PourBase(AlchemyBase.Water),
        AlchemyAction.AddHerb("chamomile", HerbState.Fresh),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.PullBellows(),
        AlchemyAction.BoilTurns(1, BoilMode.Bellows),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Grind("sage", HerbState.Fresh, 1),
        AlchemyAction.Pour(),
        AlchemyAction.Bottle()
    ];

    public static IReadOnlyList<AlchemyAction> PerfectCockerelPotion =>
    [
        AlchemyAction.PourBase(AlchemyBase.Water),
        AlchemyAction.Grind("wormwood", HerbState.Fresh, 1),
        AlchemyAction.Pour(),
        AlchemyAction.AddHerb("dandelion", HerbState.Fresh),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.PullBellows(),
        AlchemyAction.BoilTurns(1, BoilMode.Bellows),
        AlchemyAction.BoilTurns(2, BoilMode.Plain),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Bottle()
    ];

    public static IReadOnlyList<AlchemyAction>? PerfectProcedureFor(Recipe recipe) =>
        recipe.Id switch
        {
            "saviour-schnapps" => PerfectSaviourSchnapps,
            "chamomile-brew" => PerfectChamomileBrew,
            "cockerel-potion" => PerfectCockerelPotion,
            _ => null
        };
}
