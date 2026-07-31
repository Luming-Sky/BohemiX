using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Data;

public sealed class JsonForgeCatalog : IForgeCatalog
{
    private const int CurrentSchemaVersion = 3;
    private readonly ForgeCatalogDocument document;

    public JsonForgeCatalog()
        : this(OpenEmbeddedCatalog())
    {
    }

    internal JsonForgeCatalog(Stream source)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        var parsed = JsonSerializer.Deserialize<ForgeCatalogDocument>(source, options)
                     ?? throw new InvalidDataException("Forge catalog JSON is empty.");
        if (parsed.SchemaVersion is < 2 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported forge catalog schema {parsed.SchemaVersion}.");
        }
        document = parsed with
        {
            SchemaVersion = CurrentSchemaVersion,
            Recipes = parsed.Recipes
                .Select(recipe => recipe with
                {
                    Billet = recipe.Billet ?? CreateFallbackBillet(recipe.Shape),
                    Zones = recipe.Zones.Select(zone => zone with { RecommendedFace = HammerFace.Flat }).ToArray()
                })
                .ToArray()
        };
        Validate(document);
    }

    public IReadOnlyList<ForgeRecipeDefinition> Recipes => document.Recipes;
    public IReadOnlyList<ForgeMaterialDefinition> Materials => document.Materials;
    public ForgeHammerDefinition Hammer => document.Hammer;

    public ForgeRecipeDefinition GetRecipe(string id) => Recipes.FirstOrDefault(recipe => recipe.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown forge recipe '{id}'.");

    public ForgeMaterialDefinition GetMaterial(string id) => Materials.FirstOrDefault(material => material.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown forge material '{id}'.");

    private static Stream OpenEmbeddedCatalog()
    {
        var assembly = typeof(JsonForgeCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().SingleOrDefault(resource => resource.EndsWith("forge-catalog.json", StringComparison.OrdinalIgnoreCase));
        return name is null
            ? throw new FileNotFoundException("Embedded forge catalog was not found.")
            : assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException($"Embedded forge catalog '{name}' could not be opened.");
    }

    private static void Validate(ForgeCatalogDocument catalog)
    {
        if (catalog.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported forge catalog schema {catalog.SchemaVersion}.");
        }

        if (catalog.Recipes.Count == 0 || catalog.Materials.Count == 0)
        {
            throw new InvalidDataException("Forge catalog must contain recipes and materials.");
        }

        if (catalog.Recipes.Select(recipe => recipe.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Recipes.Count ||
            catalog.Materials.Select(material => material.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Materials.Count)
        {
            throw new InvalidDataException("Forge catalog IDs must be unique.");
        }

        foreach (var recipe in catalog.Recipes)
        {
            if (recipe.Zones.Count == 0 || recipe.CompletionTolerance is <= 0 or >= 1 ||
                string.IsNullOrWhiteSpace(recipe.NameZh) || string.IsNullOrWhiteSpace(recipe.DescriptionZh) ||
                recipe.Visual is null)
            {
                throw new InvalidDataException($"Recipe '{recipe.Id}' has invalid zones or tolerance.");
            }

            var outline = recipe.Shape.Outline;
            var billet = recipe.Billet;
            if (outline is not { Count: >= 2 } ||
                outline[0].U != 0 || outline[^1].U != 1 ||
                outline.Zip(outline.Skip(1)).Any(pair => pair.First.U >= pair.Second.U) ||
                outline.Any(point => point.U is < 0 or > 1 || point.Lower is < -.5 or > .5 ||
                                     point.Upper is < -.5 or > .5 || point.Lower >= point.Upper) ||
                string.IsNullOrWhiteSpace(recipe.Visual.ThumbnailAsset) ||
                string.IsNullOrWhiteSpace(recipe.Visual.FinishedModelAsset) ||
                string.IsNullOrWhiteSpace(recipe.Visual.FittingsModelAsset) ||
                recipe.Visual.RenderLength <= 0 || recipe.Visual.RenderWidth <= 0 ||
                recipe.Visual.RecommendedDevelopmentStrikes <= 0 ||
                billet is null || billet.Start is < 0 or >= 1 || billet.End is <= 0 or > 1 ||
                billet.Start >= billet.End || billet.MassScale is < .85 or > 1.15 ||
                billet.Outline is not { Count: >= 2 } ||
                billet.Outline.Zip(billet.Outline.Skip(1)).Any(pair => pair.First.U >= pair.Second.U) ||
                billet.Outline.Any(point => point.U < billet.Start || point.U > billet.End ||
                                                    point.Lower is < -.5 or > .5 ||
                                                    point.Upper is < -.5 or > .5 || point.Lower >= point.Upper))
            {
                throw new InvalidDataException($"Recipe '{recipe.Id}' has an invalid shape or visual definition.");
            }

            if (recipe.Zones.Select(zone => zone.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != recipe.Zones.Count ||
                recipe.Zones.Select(zone => zone.Order).Distinct().Count() != recipe.Zones.Count)
            {
                throw new InvalidDataException($"Recipe '{recipe.Id}' zone IDs and order values must be unique.");
            }

            foreach (var zone in recipe.Zones)
            {
                if (string.IsNullOrWhiteSpace(zone.Id) || string.IsNullOrWhiteSpace(zone.Label) ||
                    zone.Start is < 0 or > 1 || zone.End is < 0 or > 1 || zone.Start >= zone.End ||
                    zone.YMin is < 0 or > 1 || zone.YMax is < 0 or > 1 || zone.YMin >= zone.YMax ||
                    zone.IdealHeatMin is < 0 or > 1.15 || zone.IdealHeatMax is < 0 or > 1.15 || zone.IdealHeatMin >= zone.IdealHeatMax ||
                    zone.RecommendedMinStrikes <= 0 || zone.RecommendedMaxStrikes < zone.RecommendedMinStrikes ||
                    zone.RecommendedFlipAfterStrikes is < 0 || zone.RecommendedFlipAfterStrikes > zone.RecommendedMaxStrikes)
                {
                    throw new InvalidDataException($"Recipe '{recipe.Id}' zone '{zone.Id}' has invalid bounds or recommendations.");
                }
            }

            foreach (var material in catalog.Materials)
            {
                if (!recipe.RecommendedQuenchByMaterial.ContainsKey(material.Id))
                {
                    throw new InvalidDataException($"Recipe '{recipe.Id}' has no quench recommendation for '{material.Id}'.");
                }
            }
        }
    }

    internal static ForgeBilletDefinition CreateFallbackBillet(ShapeTemplateDefinition shape)
    {
        var isAxe = shape.Kind.Equals("axe", StringComparison.OrdinalIgnoreCase) ||
                    shape.Kind.Contains("axe", StringComparison.OrdinalIgnoreCase);
        var isShort = shape.Kind.Contains("basilard", StringComparison.OrdinalIgnoreCase) ||
                      shape.Kind.Contains("shortsword", StringComparison.OrdinalIgnoreCase);
        if (isAxe)
        {
            return new ForgeBilletDefinition(.18, .96,
            [
                new(.18, -.16, .16),
                new(.96, -.15, .15)
            ]);
        }

        var start = isShort ? .06 : .04;
        var end = isShort ? .86 : .90;
        var halfWidth = isShort ? .105 : .075;
        return new ForgeBilletDefinition(start, end,
        [
            new(start, -halfWidth, halfWidth),
            new(end, -halfWidth, halfWidth)
        ]);
    }

    private sealed record ForgeCatalogDocument(
        int SchemaVersion,
        IReadOnlyList<ForgeRecipeDefinition> Recipes,
        IReadOnlyList<ForgeMaterialDefinition> Materials,
        ForgeHammerDefinition Hammer);
}
