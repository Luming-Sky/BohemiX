using BohemiX.Modules.Alchemy.Models;

namespace BohemiX.Modules.Alchemy.Data;

public static class AlchemyCatalog
{
    public static IReadOnlyList<IngredientDefinition> Ingredients { get; } =
    [
        new("marigold", "Marigold", "Warm orange petals", "M12,2 C9,5 8,8 10,11 C6,10 3,12 2,16 C6,15 9,16 11,19 C12,15 12,10 12,2 M13,11 C17,8 21,10 22,14 C18,14 15,16 13,19", 0xFFE7A53A),
        new("belladonna", "Belladonna", "Deep purple berries and pointed leaves", "M12,22 C11,16 12,10 15,3 M13,12 C8,9 5,10 3,14 C7,15 10,15 13,12 M14,9 C18,6 21,7 22,11 C19,12 16,12 14,9 M8,6 A3,3 0 1,0 8,12 A3,3 0 1,0 8,6", 0xFF75506F),
        new("valerian", "Valerian", "Pale pink clusters of flowers", "M12,22 L12,8 M12,10 L6,5 M12,10 L18,5 M6,5 L3,3 M6,5 L8,2 M18,5 L16,2 M18,5 L21,3", 0xFFD29AA7),
        new("mint", "Mint", "Crisp serrated leaves", "M12,22 L12,3 M12,8 C7,4 4,6 4,11 C8,11 10,10 12,8 M12,13 C17,9 20,11 20,16 C16,16 14,15 12,13", 0xFF5D8F62),
        new("nettle", "Nettle", "Prickly dark green leaves", "M12,22 L12,2 M12,7 L5,4 L7,10 L3,13 L11,15 M12,10 L19,6 L18,12 L22,15 L13,17", 0xFF4E7449),
        new("sage", "Sage", "Thick silver-gray leaves", "M12,22 C12,15 11,8 7,2 M11,13 C6,8 2,10 3,16 C7,16 10,15 11,13 M12,9 C15,4 20,4 21,9 C18,11 15,11 12,9", 0xFF82927A)
    ];

    public static IReadOnlyList<AlchemyRecipe> Recipes { get; } =
    [
        new(
            "night-owl",
            "Night Owl Potion",
            "Stay alert and sharp in the dark.",
            [
                new(RecipeStepKind.PourBase, "Pour wine", Base: BaseLiquid.Wine),
                new(RecipeStepKind.Grind, "Grind belladonna x2", IngredientId: "belladonna", Form: IngredientForm.Ground, Count: 2),
                new(RecipeStepKind.AddIngredient, "Add ground belladonna to the cauldron", IngredientId: "belladonna", Form: IngredientForm.Ground, Count: 2),
                new(RecipeStepKind.Cook, "Boil for 2 turns", Heat: HeatBand.Boiling, Turns: 2),
                new(RecipeStepKind.AddIngredient, "Add raw marigold x1", IngredientId: "marigold", Form: IngredientForm.Raw),
                new(RecipeStepKind.Cook, "Boil for 1 turn", Heat: HeatBand.Boiling, Turns: 1),
                new(RecipeStepKind.Distill, "Distill and condense"),
                new(RecipeStepKind.Bottle, "Bottle the finished potion")
            ]),
        new(
            "marigold-decoction",
            "Marigold Decoction",
            "Slowly restores minor injuries.",
            [
                new(RecipeStepKind.PourBase, "Pour water", Base: BaseLiquid.Water),
                new(RecipeStepKind.AddIngredient, "Add raw marigold x2", IngredientId: "marigold", Count: 2),
                new(RecipeStepKind.Cook, "Simmer for 1 turn", Heat: HeatBand.Simmering, Turns: 1),
                new(RecipeStepKind.Grind, "Grind valerian x1", IngredientId: "valerian", Form: IngredientForm.Ground),
                new(RecipeStepKind.AddIngredient, "Add ground valerian to the cauldron", IngredientId: "valerian", Form: IngredientForm.Ground),
                new(RecipeStepKind.Cook, "Boil for 1 turn", Heat: HeatBand.Boiling, Turns: 1),
                new(RecipeStepKind.Bottle, "Remove from heat and bottle")
            ]),
        new(
            "cockerel",
            "Cockerel Potion",
            "Quickly restores energy.",
            [
                new(RecipeStepKind.PourBase, "Pour spirits", Base: BaseLiquid.Spirits),
                new(RecipeStepKind.Grind, "Grind mint x2", IngredientId: "mint", Form: IngredientForm.Ground, Count: 2),
                new(RecipeStepKind.AddIngredient, "Add ground mint to the cauldron", IngredientId: "mint", Form: IngredientForm.Ground, Count: 2),
                new(RecipeStepKind.Cook, "Boil for 1 turn", Heat: HeatBand.Boiling, Turns: 1),
                new(RecipeStepKind.AddIngredient, "Add raw valerian x1", IngredientId: "valerian"),
                new(RecipeStepKind.Cook, "Boil for 2 turns", Heat: HeatBand.Boiling, Turns: 2),
                new(RecipeStepKind.Distill, "Distill and condense"),
                new(RecipeStepKind.Bottle, "Bottle the finished potion")
            ])
    ];
}
