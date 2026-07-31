namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// A complete alchemy recipe: an ordered, strictly-typed action sequence. The
/// bench executes it step by step; deviations (wrong order, wrong boil-turn
/// count, wrong herb freshness) are captured by the quality evaluator rather
/// than hard-failing the brew.
/// </summary>
/// <param name="Id">Stable lowercase identifier (e.g. <c>saviour-schnapps</c>).</param>
/// <param name="DisplayName">Human-readable potion name.</param>
/// <param name="Base">The single base solvent this recipe starts with.</param>
/// <param name="Ingredients">Herb requirements, each with count / state / preparation.</param>
/// <param name="Steps">The canonical, ordered procedure. Quality order-score compares the player's performed actions against this.</param>
/// <param name="ExpectedBoilTurns">Number of hourglass turns the recipe calls for; used by the boil-turns quality factor.</param>
/// <param name="RequiresDistillation">
/// If <c>true</c>, the recipe ends by pouring the cauldron into the alembic,
/// extinguishing the candle at the correct mark, and bottling from the alembic.
/// If <c>false</c>, it ends by bottling directly from the cauldron.
/// </param>
public sealed record Recipe(
    string Id,
    string DisplayName,
    AlchemyBase Base,
    IReadOnlyList<RecipeIngredient> Ingredients,
    IReadOnlyList<AlchemyAction> Steps,
    int ExpectedBoilTurns,
    bool RequiresDistillation);
