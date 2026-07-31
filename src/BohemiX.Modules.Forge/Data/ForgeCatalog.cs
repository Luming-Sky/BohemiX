using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Data;

public interface IForgeCatalog
{
    IReadOnlyList<ForgeRecipeDefinition> Recipes { get; }
    IReadOnlyList<ForgeMaterialDefinition> Materials { get; }
    ForgeHammerDefinition Hammer { get; }
    ForgeRecipeDefinition GetRecipe(string id);
    ForgeMaterialDefinition GetMaterial(string id);
}

// Kept as the lightweight catalog entry point used by tools and tests. Runtime DI uses
// JsonForgeCatalog directly, and both now share the same embedded source of truth.
public sealed class ForgeCatalog : IForgeCatalog
{
    private readonly JsonForgeCatalog inner = new();

    public IReadOnlyList<ForgeRecipeDefinition> Recipes => inner.Recipes;
    public IReadOnlyList<ForgeMaterialDefinition> Materials => inner.Materials;
    public ForgeHammerDefinition Hammer => inner.Hammer;
    public ForgeRecipeDefinition GetRecipe(string id) => inner.GetRecipe(id);
    public ForgeMaterialDefinition GetMaterial(string id) => inner.GetMaterial(id);
}
