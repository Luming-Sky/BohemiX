using BohemiX.Core.Models.Alchemy;
using BohemiX.Core.Services.Alchemy;
using BohemiX.Infrastructure.Services.Alchemy;

namespace BohemiX.AlchemyConsole;

/// <summary>
/// A minimal interactive console that drives the KCD2 alchemy engine so you can
/// verify the bench state machine and quality evaluator by hand. It offers the
/// two spec sample recipes, walks you through brewing one action at a time,
/// shows live bench state after each step, and prints the scored result.
/// </summary>
internal static class Program
{
    // Silent Serilog logger — the console doesn't need file sinks.
    private static readonly Serilog.ILogger SilentLogger =
        new Serilog.LoggerConfiguration().CreateLogger();

    // The two sample recipes are inlined so the console runs with zero file
    // dependencies. Their step sequences match the real KCD2 mechanics.
    private static readonly Recipe[] InlineRecipes = SampleRecipes.All.ToArray();

    private static readonly AlchemyBenchService BenchService =
        new(new PotionQualityEvaluator(), SilentLogger);

    private static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintBanner();

        while (true)
        {
            var recipes = ResolveRecipes();
            var recipe = PickRecipe(recipes);
            if (recipe is null)
            {
                return;
            }

            BrewInteractive(recipe);

            Console.WriteLine();
            Console.Write("Brew another? (y/n): ");
            if (!Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                return;
            }
            Console.WriteLine();
        }
    }

    // --- Recipe resolution: prefer on-disk seed, fall back to inlined samples -

    private static IReadOnlyList<Recipe> ResolveRecipes()
    {
        try
        {
            var options = new AlchemyDataOptions { AllowMissingRoot = true };
            var repo = new RecipeRepository(options, SilentLogger);
            var catalog = repo.LoadAsync().GetAwaiter().GetResult();
            if (catalog.Recipes.Count > 0)
            {
                Console.WriteLine($"(Loaded {catalog.Recipes.Count} recipe(s) from {options.RootDirectory})");
                return catalog.Recipes.Values.OrderBy(r => r.DisplayName).ToList();
            }
        }
        catch
        {
            // Fall through to inlined recipes — the console must always be runnable.
        }

        return InlineRecipes;
    }

    // --- Step 1: pick a recipe ----------------------------------------------

    private static Recipe? PickRecipe(IReadOnlyList<Recipe> recipes)
    {
        Console.WriteLine("Available recipes:");
        for (var i = 0; i < recipes.Count; i++)
        {
            var r = recipes[i];
            var tail = r.RequiresDistillation ? " [distillation]" : "";
            Console.WriteLine($"  {i + 1}. {r.DisplayName} — base {r.Base}, {r.Ingredients.Count} herb(s){tail}");
        }
        Console.WriteLine($"  0. Quit");
        Console.Write("Pick a recipe by number: ");

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null || line.Trim() == "0")
            {
                return null;
            }
            if (int.TryParse(line.Trim(), out var idx) && idx >= 1 && idx <= recipes.Count)
            {
                var chosen = recipes[idx - 1];
                Console.WriteLine();
                Console.WriteLine($"Brewing: {chosen.DisplayName}");
                Console.WriteLine($"  Base solvent : {chosen.Base}");
                Console.WriteLine($"  Boil turns   : {chosen.ExpectedBoilTurns}");
                Console.WriteLine($"  Distillation : {(chosen.RequiresDistillation ? "yes" : "no")}");
                Console.WriteLine($"  Ingredients  : {string.Join(", ", chosen.Ingredients.Select(DescribeIngredient))}");
                Console.WriteLine();
                return chosen;
            }
            Console.Write("Invalid choice. Enter a number 1-" + recipes.Count + " (or 0 to quit): ");
        }
    }

    private static string DescribeIngredient(RecipeIngredient ing) =>
        $"{ing.HerbId} x{ing.Count} ({ing.RequiredState}, {ing.RequiredPreparation})";

    // --- Step 2: brew interactively -----------------------------------------

    private static void BrewInteractive(Recipe recipe)
    {
        var bench = new AlchemyBench();
        var performed = new List<AlchemyAction>();
        var errors = new List<(int Step, AlchemyActionKind Kind, string Reason)>();
        var stepIndex = 0;

        Console.WriteLine("=== Alchemy Bench ===");
        Console.WriteLine("Enter actions one at a time. Commands:");
        PrintCommands();
        Console.WriteLine();

        while (true)
        {
            PrintState(bench);
            Console.Write($"> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                return;
            }

            var trimmed = input.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) || trimmed == "0")
            {
                return;
            }
            if (trimmed.Equals("done", StringComparison.OrdinalIgnoreCase) || trimmed == "end")
            {
                break;
            }
            if (trimmed.Equals("help", StringComparison.OrdinalIgnoreCase) || trimmed == "?")
            {
                PrintCommands();
                continue;
            }
            if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                // Cheat: auto-play the canonical procedure for fast verification.
                var canonical = SampleRecipes.PerfectProcedureFor(recipe);
                if (canonical is null)
                {
                    Console.WriteLine("  No canonical procedure known for this recipe.");
                    continue;
                }
                Console.WriteLine($"  Auto-playing {canonical.Count} canonical steps...");
                foreach (var a in canonical)
                {
                    var r = bench.Apply(a);
                    performed.Add(a);
                    if (!r.Applied)
                    {
                        errors.Add((stepIndex, a.Kind, r.Error ?? "rejected"));
                    }
                    stepIndex++;
                }
                Console.WriteLine("  Done. Type 'done' to score, or keep adding actions.");
                Console.WriteLine();
                continue;
            }

            var action = ParseAction(trimmed);
            if (action is null)
            {
                Console.WriteLine("  Unknown command. Type 'help' for the list, or 'done' to finish.");
                continue;
            }

            var result = bench.Apply(action);
            performed.Add(action);
            if (!result.Applied)
            {
                errors.Add((stepIndex, action.Kind, result.Error ?? "rejected"));
                Console.WriteLine($"  ✗ Rejected: {result.Error}");
            }
            else
            {
                Console.WriteLine("  ✓ Applied");
            }
            stepIndex++;
            Console.WriteLine();
        }

        // Finalize & score via the same service the app will use.
        var brew = BenchService.Brew(recipe, performed);
        Console.WriteLine();
        Console.WriteLine("================== RESULT ==================");
        if (brew.Potion is { } potion)
        {
            Console.WriteLine($"  Potion  : {potion.DisplayName}");
            Console.WriteLine($"  Quality : {potion.Quality}  (score {brew.Quality.TotalScore:F3})");
            Console.WriteLine($"    order     {brew.Quality.Order.Score:F2}  ×{brew.Quality.Order.Weight}  = {brew.Quality.Order.Contribution:F3}");
            Console.WriteLine($"    boilturns {brew.Quality.BoilTurns.Score:F2}  ×{brew.Quality.BoilTurns.Weight}  = {brew.Quality.BoilTurns.Contribution:F3}");
            Console.WriteLine($"    freshness{brew.Quality.Freshness.Score:F2}  ×{brew.Quality.Freshness.Weight}  = {brew.Quality.Freshness.Contribution:F3}");
        }
        else
        {
            Console.WriteLine("  ✗ No potion produced — the brew never reached a valid final state");
            Console.WriteLine("    (need: a base poured, at least one herb added, and a Bottle action).");
        }

        if (errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Tolerated errors ({errors.Count}):");
            foreach (var e in errors)
            {
                Console.WriteLine($"    step {e.Step}: {e.Kind} — {e.Reason}");
            }
        }
        Console.WriteLine("============================================");
    }

    // --- Action parsing -----------------------------------------------------

    /// <summary>
    /// Parses a single command line into an <see cref="AlchemyAction"/>. The
    /// grammar is verb-first, e.g. <c>pour water</c>, <c>add nettle fresh</c>,
    /// <c>grind belladonna 2</c>, <c>stir cw</c>, <c>boil 1</c>.
    /// </summary>
    private static AlchemyAction? ParseAction(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var verb = parts[0].ToLowerInvariant();
        return verb switch
        {
            "lower" or "lowerc" or "down" => AlchemyAction.LowerCauldron(),
            "raise" or "raisec" or "up" => AlchemyAction.RaiseCauldron(),
            "bellows" or "fire" or "blow" => AlchemyAction.PullBellows(),
            "pour" => ParsePour(parts),
            "add" => ParseAdd(parts, preparation: HerbPreparation.None),
            "grind" => ParseAdd(parts, preparation: HerbPreparation.Ground, defaultVerb: AlchemyActionKind.Grind),
            "crush" => ParseAdd(parts, preparation: HerbPreparation.Crushed, defaultVerb: AlchemyActionKind.Crush),
            "stir" => ParseStir(parts),
            "boil" or "hourglass" or "turn" => ParseBoil(parts),
            "bottle" or "phial" => AlchemyAction.Bottle(),
            _ => null
        };
    }

    private static AlchemyAction? ParsePour(string[] parts)
    {
        // "pour" with nothing → bare pour (plate->cauldron or cauldron->alembic).
        if (parts.Length < 2)
        {
            return AlchemyAction.Pour();
        }

        var arg = parts[1].ToLowerInvariant();
        // "pour water/wine/oil/spirits" → base pour.
        if (TryParseBase(arg, out var baseKind))
        {
            return AlchemyAction.PourBase(baseKind);
        }
        // Otherwise treat as a herb pour: "pour nettle fresh".
        var state = parts.Length > 2 && TryParseHerbState(parts[2], out var s) ? s : HerbState.Fresh;
        return AlchemyAction.AddHerb(parts[1], state);
    }

    private static AlchemyAction? ParseAdd(string[] parts, HerbPreparation preparation, AlchemyActionKind? defaultVerb = null)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("  Usage: add|grind|crush <herbId> [fresh|dried] [count]");
            return null;
        }

        var herbId = parts[1];
        var state = parts.Length > 2 && TryParseHerbState(parts[2], out var s) ? s : HerbState.Fresh;
        var countIndex = TryParseHerbState(parts[Math.Min(2, parts.Length - 1)], out _) ? 3 : 2;
        var count = 1;
        if (countIndex < parts.Length && int.TryParse(parts[countIndex], out var c) && c > 0)
        {
            count = c;
        }

        return defaultVerb switch
        {
            AlchemyActionKind.Grind => AlchemyAction.Grind(herbId, state, count),
            AlchemyActionKind.Crush => AlchemyAction.CrushHerb(herbId, state, count),
            _ => AlchemyAction.AddHerb(herbId, state)
        };
    }

    private static AlchemyAction? ParseStir(string[] parts)
    {
        var direction = StirDirection.Clockwise;
        if (parts.Length > 1)
        {
            direction = parts[1].ToLowerInvariant() switch
            {
                "ccw" or "counter" or "counterclockwise" or "anticlockwise" => StirDirection.CounterClockwise,
                _ => StirDirection.Clockwise
            };
        }
        var count = parts.Length > 2 && int.TryParse(parts[2], out var c) && c > 0 ? c : 1;
        return AlchemyAction.Stir(direction, count);
    }

    private static AlchemyAction? ParseBoil(string[] parts)
    {
        var turns = parts.Length > 1 && int.TryParse(parts[1], out var t) && t > 0 ? t : 1;
        return AlchemyAction.BoilTurns(turns);
    }

    private static bool TryParseBase(string token, out AlchemyBase value)
    {
        switch (token.ToLowerInvariant())
        {
            case "water": value = AlchemyBase.Water; return true;
            case "wine": value = AlchemyBase.Wine; return true;
            case "oil": value = AlchemyBase.Oil; return true;
            case "spirits": case "spirit": case "liquor": value = AlchemyBase.Spirits; return true;
        }
        value = default;
        return false;
    }

    private static bool TryParseHerbState(string token, out HerbState value)
    {
        switch (token.ToLowerInvariant())
        {
            case "fresh": value = HerbState.Fresh; return true;
            case "dried": case "dry": value = HerbState.Dried; return true;
        }
        value = default;
        return false;
    }

    // --- Display ------------------------------------------------------------

    private static void PrintBanner()
    {
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║   BohemiX · KCD2 Alchemy Bench — Console Test   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.WriteLine("Drive the alchemy engine by hand to verify brewing.");
        Console.WriteLine();
    }

    private static void PrintCommands()
    {
        Console.WriteLine("  lower            — lower cauldron onto fire");
        Console.WriteLine("  raise            — raise cauldron off fire");
        Console.WriteLine("  bellows          — pull bellows (needs lowered cauldron)");
        Console.WriteLine("  pour <base>      — pour base solvent (water/wine/oil/spirits)");
        Console.WriteLine("  pour <herb> [s]  — add a herb (s = fresh|dried)");
        Console.WriteLine("  pour             — bare pour (plate→cauldron, or cauldron→alembic)");
        Console.WriteLine("  add <herb> [s]   — same as pour-herb, raw add");
        Console.WriteLine("  grind <herb> [s] [n] — grind n herbs to powder on the plate");
        Console.WriteLine("  crush <herb> [s] [n] — crush n juicy herbs onto the plate");
        Console.WriteLine("  stir [cw|ccw] [n]    — stir cauldron");
        Console.WriteLine("  boil [n]         — turn hourglass n times (needs boiling)");
        Console.WriteLine("  bottle           — bottle the brew into a phial");
        Console.WriteLine("  auto             — auto-play the canonical procedure (cheat, for fast verify)");
        Console.WriteLine("  done / end       — finish & score");
        Console.WriteLine("  help / ?         — show this list   |   quit / 0 — exit");
    }

    private static void PrintState(AlchemyBench bench)
    {
        var s = bench.State;
        Console.WriteLine($"  [cauldron {s.CauldronPosition}, liquid {s.LiquidPhase}, boil-turns {s.BoilTurnsElapsed}]");
        Console.WriteLine($"  [base: {s.CauldronBase?.ToString() ?? "—"}]  [herbs in cauldron: {s.CauldronHerbs.Count}]  [plate: {s.Plate.Count}]");
        if (s.DistillationTransferred)
        {
            Console.WriteLine("  [transferred to alembic]");
        }
        if (s.Bottled)
        {
            Console.WriteLine("  [bottled]");
        }
    }
}
