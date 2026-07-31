namespace BohemiX.Modules.Alchemy.Models;

public enum BaseLiquid
{
    Water,
    Wine,
    Spirits,
    Oil
}

public enum IngredientForm
{
    Raw,
    Ground
}

public enum HeatBand
{
    Cold,
    Simmering,
    Boiling,
    Overheated
}

public enum RecipeStepKind
{
    PourBase,
    Grind,
    AddIngredient,
    Cook,
    Distill,
    Bottle
}

public enum BrewOutcome
{
    Brewing,
    Perfect,
    Diluted,
    Failed
}

public enum AlchemyCommandKind
{
    PourBase,
    LoadMortar,
    TransferGroundMortar,
    AddRawIngredient,
    ToggleCauldron,
    PumpBellows,
    StartHourglass,
    Distill,
    Bottle
}

public sealed record IngredientDefinition(
    string Id,
    string Name,
    string Description,
    string Geometry,
    uint Color,
    int StartingQuantity = 8);

public sealed record RecipeStep(
    RecipeStepKind Kind,
    string Label,
    BaseLiquid? Base = null,
    string? IngredientId = null,
    IngredientForm Form = IngredientForm.Raw,
    int Count = 1,
    HeatBand Heat = HeatBand.Cold,
    int Turns = 0);

public sealed record AlchemyRecipe(
    string Id,
    string Name,
    string Effect,
    IReadOnlyList<RecipeStep> Steps);

public sealed record AlchemyCommand(
    AlchemyCommandKind Kind,
    BaseLiquid? Base = null,
    string? IngredientId = null,
    int Count = 1,
    int Turns = 1,
    double Strength = 1);

public sealed record PreparedIngredient(string IngredientId, IngredientForm Form, int Count);

public sealed record EngineMessage(bool Accepted, string Text);

public sealed record AlchemySnapshot(
    AlchemyRecipe Recipe,
    int CurrentStepIndex,
    int CurrentStepProgress,
    BaseLiquid? Base,
    IReadOnlyList<PreparedIngredient> Cauldron,
    PreparedIngredient? Mortar,
    double Temperature,
    HeatBand HeatBand,
    bool CauldronLowered,
    bool HourglassRunning,
    double HourglassProgress,
    bool Distilled,
    int Mistakes,
    BrewOutcome Outcome,
    int Yield,
    string LastMessage,
    IReadOnlyDictionary<string, int> Inventory,
    IReadOnlyList<string> EventLog);
