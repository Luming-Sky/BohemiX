using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Models;

namespace BohemiX.Modules.Alchemy.Engine;

public sealed class AlchemyEngine
{
    private const bool EnforceFailureLimit = false;
    private readonly Dictionary<string, int> inventory = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PreparedIngredient> cauldron = [];
    private readonly List<string> eventLog = [];
    private AlchemyRecipe recipe = AlchemyCatalog.Recipes[0];
    private BaseLiquid? baseLiquid;
    private PreparedIngredient? mortar;
    private int currentStepIndex;
    private int currentStepProgress;
    private double temperature;
    private bool cauldronLowered;
    private bool hourglassRunning;
    private double hourglassElapsed;
    private double hourglassDuration;
    private bool hourglassStayedInBand;
    private int pendingTurns;
    private HeatBand pendingHeatBand;
    private double overheatedSeconds;
    private bool overheatPenaltyApplied;
    private bool distilled;
    private int mistakes;
    private BrewOutcome outcome;
    private int yield;
    private string lastMessage = "选择基液，开始新的炼制。";

    public AlchemyEngine()
    {
        ResetInventory();
    }

    public AlchemySnapshot Snapshot => new(
        recipe,
        currentStepIndex,
        currentStepProgress,
        baseLiquid,
        cauldron.ToArray(),
        mortar,
        temperature,
        GetHeatBand(temperature),
        cauldronLowered,
        hourglassRunning,
        hourglassDuration <= 0 ? 0 : Math.Clamp(hourglassElapsed / hourglassDuration, 0, 1),
        distilled,
        mistakes,
        outcome,
        yield,
        lastMessage,
        new Dictionary<string, int>(inventory),
        eventLog.ToArray());

    public bool RequiresContinuousTick => outcome == BrewOutcome.Brewing
        && (temperature > 0.001 || hourglassRunning || overheatedSeconds > 0.001);

    public void SelectRecipe(AlchemyRecipe selectedRecipe)
    {
        recipe = selectedRecipe;
        Reset();
    }

    public void Reset()
    {
        baseLiquid = null;
        cauldron.Clear();
        mortar = null;
        currentStepIndex = 0;
        currentStepProgress = 0;
        temperature = 0;
        cauldronLowered = false;
        hourglassRunning = false;
        hourglassElapsed = 0;
        hourglassDuration = 0;
        hourglassStayedInBand = false;
        pendingTurns = 0;
        pendingHeatBand = HeatBand.Cold;
        overheatedSeconds = 0;
        overheatPenaltyApplied = false;
        distilled = false;
        mistakes = 0;
        outcome = BrewOutcome.Brewing;
        yield = 0;
        eventLog.Clear();
        lastMessage = "器具已清理。先向坩埚倒入配方指定的基液。";
        ResetInventory();
    }

    public EngineMessage Execute(AlchemyCommand command)
    {
        if (outcome != BrewOutcome.Brewing)
        {
            return Message(false, "本轮炼制已经结束，请清理工作台后重新开始。");
        }

        return command.Kind switch
        {
            AlchemyCommandKind.PourBase => PourBase(command.Base),
            AlchemyCommandKind.LoadMortar => LoadMortar(command.IngredientId, command.Count),
            AlchemyCommandKind.TransferGroundMortar => TransferGroundMortar(),
            AlchemyCommandKind.AddRawIngredient => AddRawIngredient(command.IngredientId, command.Count),
            AlchemyCommandKind.ToggleCauldron => ToggleCauldron(),
            AlchemyCommandKind.PumpBellows => PumpBellows(command.Strength),
            AlchemyCommandKind.StartHourglass => StartHourglassFree(command.Turns),
            AlchemyCommandKind.Distill => DistillFree(),
            AlchemyCommandKind.Bottle => BottleFree(),
            _ => Message(false, "未知的炼金动作。")
        };
    }

    public void Tick(TimeSpan elapsed)
    {
        if (outcome != BrewOutcome.Brewing)
        {
            return;
        }

        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.25);
        var coolingRate = cauldronLowered ? 2.6 : 7.0;
        temperature = Math.Max(0, temperature - (coolingRate * seconds));
        var band = GetHeatBand(temperature);

        if (hourglassRunning)
        {
            hourglassElapsed += seconds;
            hourglassStayedInBand &= band == pendingHeatBand;
            if (hourglassElapsed >= hourglassDuration - 0.0001)
            {
                FinishHourglassFree();
            }
        }

        if (band == HeatBand.Overheated)
        {
            overheatedSeconds += seconds;
            if (!overheatPenaltyApplied && overheatedSeconds >= 1.5)
            {
                overheatPenaltyApplied = true;
                AddMistake("坩埚在过热区停留过久，药性被灼伤。");
            }

        }
        else
        {
            overheatedSeconds = Math.Max(0, overheatedSeconds - (seconds * 2));
            if (overheatedSeconds <= 0)
            {
                overheatPenaltyApplied = false;
            }
        }
    }

    private EngineMessage PourBase(BaseLiquid? liquid)
    {
        if (liquid is null)
        {
            return Message(false, "没有选择基液。 ");
        }

        if (baseLiquid is not null)
        {
            return Message(false, "坩埚中已经有基液，清理后才能换用另一种溶剂。");
        }

        baseLiquid = liquid;
        var expected = CurrentStep;
        if (expected is { Kind: RecipeStepKind.PourBase } && expected.Base == liquid)
        {
            Advance($"已倒入{FormatBase(liquid.Value)}。液面平静，第一步完成。");
            return new EngineMessage(true, lastMessage);
        }

        return RecordDeviation($"已倒入{FormatBase(liquid.Value)}。这与配方提示不同，但炼制仍可继续。");
    }

    private EngineMessage LoadMortar(string? ingredientId, int count)
    {
        count = Math.Clamp(count, 1, 3);
        if (mortar is not null && !StringEquals(mortar.IngredientId, ingredientId))
        {
            return Message(false, "研钵中仍有材料，先完成当前预处理。");
        }

        if (!TryConsume(ingredientId, count, out var error))
        {
            return Message(false, error);
        }

        mortar = mortar is null
            ? new PreparedIngredient(ingredientId!, IngredientForm.Raw, count)
            : mortar with { Count = mortar.Count + count };
        return Message(true, "草药已放入研钵。握住杵持续研磨，再将粉末直接投入坩埚。");
    }

    private EngineMessage TransferGroundMortar()
    {
        if (mortar is null)
        {
            return Message(false, "研钵里没有可研磨的草药。");
        }

        if (baseLiquid is null)
        {
            return Message(false, "先倒入一种基液，坩埚才能接收研磨后的草药粉。");
        }

        var transferred = mortar with { Form = IngredientForm.Ground };
        var expected = CurrentStep;
        if (expected is { Kind: RecipeStepKind.Grind }
            && StringEquals(expected.IngredientId, transferred.IngredientId)
            && transferred.Count <= expected.Count - currentStepProgress)
        {
            currentStepProgress += transferred.Count;
            mortar = null;
            AddToCauldron(transferred);

            if (currentStepProgress < expected.Count)
            {
                return Message(true, $"已研磨并投入 {currentStepProgress}/{expected.Count} 份药粉；可继续逐份加入同种草药。");
            }

            var completedCount = currentStepProgress;
            Advance("研磨完成，细粉已可投入坩埚。");
            var following = CurrentStep;
            if (following is { Kind: RecipeStepKind.AddIngredient, Form: IngredientForm.Ground }
                && StringEquals(following.IngredientId, transferred.IngredientId)
                && following.Count == completedCount)
            {
                Advance("药粉已随每次研磨直接落入药液。");
            }

            return new EngineMessage(true, lastMessage);
        }

        if (expected is { Kind: RecipeStepKind.Grind }
            && StringEquals(expected.IngredientId, transferred.IngredientId)
            && expected.Count == transferred.Count)
        {
            Advance("研磨完成，细粉已经可以投入坩埚。");
        }
        else
        {
            RecordDeviation("研磨完成，但草药或数量与当前配方提示不匹配。");
        }

        mortar = null;
        AddToCauldron(transferred);
        expected = CurrentStep;
        if (expected is { Kind: RecipeStepKind.AddIngredient, Form: IngredientForm.Ground }
            && StringEquals(expected.IngredientId, transferred.IngredientId)
            && expected.Count == transferred.Count)
        {
            Advance("草药粉直接落入药液，没有结块。");
            return new EngineMessage(true, lastMessage);
        }

        return RecordDeviation("研磨材料已直接投入坩埚；它与当前配方提示不匹配，但可以继续操作。");
    }

    private EngineMessage AddRawIngredient(string? ingredientId, int count)
    {
        if (baseLiquid is null)
        {
            return Message(false, "先倒入一种基液，坩埚才能接收草药。");
        }

        if (!TryConsume(ingredientId, count, out var error))
        {
            return Message(false, error);
        }

        var progressiveExpected = CurrentStep;
        if (progressiveExpected is { Kind: RecipeStepKind.AddIngredient, Form: IngredientForm.Raw }
            && StringEquals(progressiveExpected.IngredientId, ingredientId)
            && count <= progressiveExpected.Count - currentStepProgress)
        {
            AddToCauldron(new PreparedIngredient(ingredientId!, IngredientForm.Raw, count));
            currentStepProgress += count;
            if (currentStepProgress < progressiveExpected.Count)
            {
                return Message(true, $"已投入 {currentStepProgress}/{progressiveExpected.Count} 份药草；可继续逐份加入。");
            }

            Advance("原始药草已投入坩埚，叶片在液面上缓慢展开。");
            return new EngineMessage(true, lastMessage);
        }

        AddToCauldron(new PreparedIngredient(ingredientId!, IngredientForm.Raw, count));
        var expected = CurrentStep;
        if (expected is { Kind: RecipeStepKind.AddIngredient, Form: IngredientForm.Raw }
            && StringEquals(expected.IngredientId, ingredientId)
            && expected.Count == count)
        {
            Advance("原始草药已投入坩埚，叶片在液面上缓慢展开。");
            return new EngineMessage(true, lastMessage);
        }

        return RecordDeviation("原始草药已投入坩埚；它与当前配方提示不匹配，但可以继续操作。");
    }

    private EngineMessage ToggleCauldron()
    {
        if (baseLiquid is null)
        {
            return Message(false, "空坩埚没有必要靠近炉火。");
        }

        cauldronLowered = !cauldronLowered;
        if (cauldronLowered)
        {
            temperature = Math.Max(temperature, 18);
        }

        return Message(true, cauldronLowered ? "坩埚已经降到炉火上。" : "坩埚已经升离炉火，药液开始降温。");
    }

    private EngineMessage PumpBellows(double strength)
    {
        if (!cauldronLowered || baseLiquid is null)
        {
            return Message(false, "先把装有基液的坩埚降到炉火上。");
        }

        var normalizedStrength = Math.Clamp(strength, 0.15, 1);
        temperature = Math.Min(100, temperature + (3 + (normalizedStrength * 12)));
        return Message(true, GetHeatBand(temperature) switch
        {
            HeatBand.Cold => "火焰刚刚舔到锅底。",
            HeatBand.Simmering => "药液进入慢熬区，细泡沿锅壁升起。",
            HeatBand.Boiling => "液面已经稳定沸腾，可以翻转沙漏。",
            HeatBand.Overheated => "火舌过高，立刻停止鼓风。",
            _ => "炉火发生变化。"
        });
    }

    private EngineMessage StartHourglassFree(int turns)
    {
        var expected = CurrentStep;
        if (!cauldronLowered || baseLiquid is null)
        {
            return Message(false, "把装有基液的坩埚降到炉火上后，才能开始计时。");
        }

        pendingTurns = Math.Clamp(turns, 1, 3);
        pendingHeatBand = expected is { Kind: RecipeStepKind.Cook }
            ? expected.Heat
            : GetHeatBand(temperature);
        hourglassDuration = pendingTurns * 4.0;
        hourglassElapsed = 0;
        hourglassRunning = true;
        hourglassStayedInBand = GetHeatBand(temperature) == pendingHeatBand;
        return Message(
            true,
            expected is { Kind: RecipeStepKind.Cook }
                ? $"沙漏开始计时：{pendingTurns} 回合。配方建议保持在{FormatHeat(pendingHeatBand)}区。"
                : $"沙漏开始计时：{pendingTurns} 回合。当前不是配方指定的熬煮步骤，结果将影响品质。");
    }

    private EngineMessage DistillFree()
    {
        if (baseLiquid is null)
        {
            return Message(false, "坩埚中没有药液，无法开始蒸馏。");
        }

        var expected = CurrentStep;
        distilled = true;
        cauldronLowered = false;
        if (expected is { Kind: RecipeStepKind.Distill })
        {
            Advance("药液已进入蒸馏器。");
            return new EngineMessage(true, lastMessage);
        }

        return RecordDeviation("已完成蒸馏；当前配方提示并不要求此时蒸馏。");
    }

    private EngineMessage BottleFree()
    {
        if (baseLiquid is null)
        {
            return Message(false, "先在坩埚中准备药液，才能装瓶。");
        }

        var followsRecipe = CurrentStep is { Kind: RecipeStepKind.Bottle };
        if (followsRecipe)
        {
            Advance("药剂完成装瓶。");
        }
        else
        {
            AddMistake("在配方完成前装瓶，成品会被稀释。");
        }

        outcome = mistakes == 0 && followsRecipe ? BrewOutcome.Perfect : BrewOutcome.Diluted;
        yield = outcome == BrewOutcome.Perfect ? 3 : 1;
        lastMessage = outcome == BrewOutcome.Perfect
            ? "完美药剂：额外得到 3 瓶成品。"
            : $"{QualityGradeForMistakes(mistakes)}品质：炼制已结束，得到 1 瓶成品。";
        AddLog(lastMessage);
        return new EngineMessage(true, lastMessage);
    }

    private void FinishHourglassFree()
    {
        hourglassRunning = false;
        var expected = CurrentStep;
        if (expected is not { Kind: RecipeStepKind.Cook })
        {
            RecordDeviation("熬煮已结束；当前并不是配方建议的熬煮步骤。此操作会影响最终品质。");
            return;
        }

        var turnCountMatches = pendingTurns == expected.Turns;
        if (!turnCountMatches)
        {
            AddMistake($"本次熬煮为 {pendingTurns} 回合，配方建议为 {expected.Turns} 回合。");
        }

        if (!hourglassStayedInBand)
        {
            AddMistake($"计时期间温度没有稳定保持在建议的{FormatHeat(expected.Heat)}区。");
        }

        var deviationMessage = turnCountMatches && hourglassStayedInBand
            ? $"{pendingTurns} 回合熬煮完成，温度稳定保持在{FormatHeat(expected.Heat)}区。"
            : "熬煮完成。操作偏差已计入品质，但可以继续下一步。";
        Advance(deviationMessage);
    }

    private EngineMessage StartHourglass(int turns)
    {
        var expected = CurrentStep;
        if (expected is not { Kind: RecipeStepKind.Cook })
        {
            return RegisterMistake("当前步骤并不需要熬煮。");
        }

        if (!cauldronLowered || baseLiquid is null)
        {
            return RegisterMistake("坩埚未处于炉火上，沙漏无法计量有效熬煮。");
        }

        expected = CurrentStep;
        if (expected is not { Kind: RecipeStepKind.Cook })
        {
            return RegisterMistake("当前步骤并不需要熬煮。");
        }

        pendingTurns = Math.Clamp(turns, 1, 3);
        pendingHeatBand = expected.Heat;
        hourglassDuration = pendingTurns * 4.0;
        hourglassElapsed = 0;
        hourglassRunning = true;
        hourglassStayedInBand = GetHeatBand(temperature) == pendingHeatBand;
        return Message(true, $"沙漏开始计量 {pendingTurns} 回合。保持温度在{FormatHeat(pendingHeatBand)}区。");
    }

    private EngineMessage Distill()
    {
        var expected = CurrentStep;
        if (expected is not { Kind: RecipeStepKind.Distill })
        {
            return RegisterMistake("当前药剂不应在此刻进入蒸馏器。");
        }

        distilled = true;
        cauldronLowered = false;
        Advance("药液进入蒸馏器，第一滴冷凝液落入接收瓶。");
        return new EngineMessage(true, lastMessage);
    }

    private EngineMessage Bottle()
    {
        var expected = CurrentStep;
        if (expected is not { Kind: RecipeStepKind.Bottle })
        {
            return RegisterMistake("配方尚未完成，过早装瓶会截断药性。");
        }

        Advance("药剂完成装瓶。");
        outcome = mistakes == 0 ? BrewOutcome.Perfect : BrewOutcome.Diluted;
        yield = mistakes == 0 ? 3 : 1;
        lastMessage = mistakes == 0
            ? "完美药剂：色泽清澈，额外得到 3 瓶成品。"
            : $"{QualityGradeForMistakes(mistakes)}品质：仍可使用，但只得到 1 瓶成品。";
        AddLog(lastMessage);
        return new EngineMessage(true, lastMessage);
    }

    private void FinishHourglass()
    {
        hourglassRunning = false;
        var expected = CurrentStep;
        if (expected is not { Kind: RecipeStepKind.Cook })
        {
            AddMistake("当前步骤并不需要熬煮。");
            return;
        }

        var turnCountMatches = expected.Turns == pendingTurns;
        if (!turnCountMatches)
        {
            AddMistake($"本次熬煮为 {pendingTurns} 回合，配方建议为 {expected.Turns} 回合。");
        }

        if (!hourglassStayedInBand)
        {
            AddMistake($"计时期间温度没有稳定保持在建议的{FormatHeat(expected.Heat)}区。");
        }

        Advance(turnCountMatches && hourglassStayedInBand
            ? $"{pendingTurns} 回合熬煮完成，温度始终保持在{FormatHeat(expected.Heat)}区。"
            : "熬煮完成。操作偏差已计入品质，但可以继续下一步。");
    }

    private RecipeStep? CurrentStep => currentStepIndex < recipe.Steps.Count ? recipe.Steps[currentStepIndex] : null;

    private void Advance(string message)
    {
        currentStepIndex = Math.Min(currentStepIndex + 1, recipe.Steps.Count);
        currentStepProgress = 0;
        lastMessage = message;
        AddLog(message);
    }

    private EngineMessage RegisterMistake(string message)
    {
        AddMistake(message);
        return new EngineMessage(false, lastMessage);
    }

    private EngineMessage RecordDeviation(string message)
    {
        AddMistake(message);
        return new EngineMessage(true, lastMessage);
    }

    private void AddMistake(string message)
    {
        mistakes++;
        lastMessage = $"失误 {mistakes}：{message}";
        AddLog(lastMessage);
        if (EnforceFailureLimit && mistakes >= 3)
        {
            Fail("累计三次失误，药液已经变成不可用的灰烬与废液。");
        }
    }

    private void Fail(string message)
    {
        outcome = BrewOutcome.Failed;
        yield = 0;
        hourglassRunning = false;
        lastMessage = message;
        AddLog(message);
    }

    private EngineMessage Message(bool accepted, string message)
    {
        lastMessage = message;
        AddLog(message);
        return new EngineMessage(accepted, message);
    }

    private void AddLog(string message)
    {
        eventLog.Add(message);
        if (eventLog.Count > 40)
        {
            eventLog.RemoveAt(0);
        }
    }

    private void AddToCauldron(PreparedIngredient ingredient)
    {
        var index = cauldron.FindIndex(item =>
            item.Form == ingredient.Form
            && StringEquals(item.IngredientId, ingredient.IngredientId));
        if (index < 0)
        {
            cauldron.Add(ingredient);
            return;
        }

        cauldron[index] = cauldron[index] with { Count = cauldron[index].Count + ingredient.Count };
    }

    private bool TryConsume(string? ingredientId, int count, out string error)
    {
        count = Math.Clamp(count, 1, 3);
        if (ingredientId is null || !inventory.TryGetValue(ingredientId, out var available))
        {
            error = "物品栏中没有这种草药。";
            return false;
        }

        if (available < count)
        {
            error = "这种草药的库存不足。";
            return false;
        }

        inventory[ingredientId] = available - count;
        error = string.Empty;
        return true;
    }

    private void ResetInventory()
    {
        inventory.Clear();
        foreach (var ingredient in AlchemyCatalog.Ingredients)
        {
            inventory[ingredient.Id] = ingredient.StartingQuantity;
        }
    }

    private static HeatBand GetHeatBand(double value) => value switch
    {
        < 28 => HeatBand.Cold,
        < 56 => HeatBand.Simmering,
        < 84 => HeatBand.Boiling,
        _ => HeatBand.Overheated
    };

    private static string FormatBase(BaseLiquid liquid) => liquid switch
    {
        BaseLiquid.Water => "清水",
        BaseLiquid.Wine => "葡萄酒",
        BaseLiquid.Spirits => "烈酒",
        BaseLiquid.Oil => "油",
        _ => liquid.ToString()
    };

    private static string FormatHeat(HeatBand band) => band switch
    {
        HeatBand.Cold => "冷却",
        HeatBand.Simmering => "慢熬",
        HeatBand.Boiling => "沸腾",
        HeatBand.Overheated => "过热",
        _ => band.ToString()
    };

    private static string QualityGradeForMistakes(int mistakeCount)
    {
        var score = Math.Clamp(84 - (Math.Max(1, mistakeCount) - 1) * 7, 0, 84);
        return score switch
        {
            >= 80 => "优秀",
            >= 60 => "普通",
            _ => "糟糕"
        };
    }

    private static bool StringEquals(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
