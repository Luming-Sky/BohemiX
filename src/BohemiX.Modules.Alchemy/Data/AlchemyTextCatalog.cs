using System.Text.RegularExpressions;
using BohemiX.Modules.Alchemy.Models;

namespace BohemiX.Modules.Alchemy.Data;

internal enum AlchemyTextKey
{
    CurrentRecipe,
    AlchemyWorkshop,
    Step,
    Loading,
    Mistakes,
    BrewStatus,
    Herbs,
    Inventory,
    Take,
    Base,
    ChooseSolvent,
    AddHerbs,
    GroundHerbsReady,
    Grinding,
    Pull,
    Ready,
    Timing,
    TurnsSelected,
    Press,
    Quantity,
    Turns,
    Temperature,
    Cold,
    Simmer,
    Simmering,
    Boiling,
    Hot,
    Overheated,
    Result,
    Yield,
    PotionRecipe,
    Effect,
    BrewingOrder,
    BrewingOrderInstruction,
    Complete,
    Current,
    Pending,
    Previous,
    Next,
    Recipe,
    BrewComplete,
    PotionQuality,
    Status,
    None,
    Total,
    Uncork,
    Pouring,
    Reset,
    CurrentStep,
    Hourglass,
    Distilling,
    Brewing,
    Bottles,
    RecipeComplete,
    HourglassReady,
    HourglassRunning,
    EmptyCauldron,
    DragInHerbs,
    CondensateReady,
    StillReady,
    Ground,
    MistakesNone,
    SelectQuantityHint
}

internal static partial class AlchemyTextCatalog
{
    private static readonly IReadOnlyDictionary<AlchemyTextKey, string> Chinese =
        new Dictionary<AlchemyTextKey, string>
        {
            [AlchemyTextKey.CurrentRecipe] = "当前配方",
            [AlchemyTextKey.AlchemyWorkshop] = "炼药工坊",
            [AlchemyTextKey.Step] = "步骤",
            [AlchemyTextKey.Loading] = "载入中...",
            [AlchemyTextKey.Mistakes] = "失误",
            [AlchemyTextKey.BrewStatus] = "炼制状态",
            [AlchemyTextKey.Herbs] = "草药",
            [AlchemyTextKey.Inventory] = "库存",
            [AlchemyTextKey.Take] = "取用",
            [AlchemyTextKey.Base] = "基液",
            [AlchemyTextKey.ChooseSolvent] = "选择溶剂",
            [AlchemyTextKey.AddHerbs] = "放入草药",
            [AlchemyTextKey.GroundHerbsReady] = "药粉已备好",
            [AlchemyTextKey.Grinding] = "研磨",
            [AlchemyTextKey.Pull] = "拉动",
            [AlchemyTextKey.Ready] = "就绪",
            [AlchemyTextKey.Timing] = "计时",
            [AlchemyTextKey.TurnsSelected] = "回合已选择",
            [AlchemyTextKey.Press] = "下压",
            [AlchemyTextKey.Quantity] = "数量",
            [AlchemyTextKey.Turns] = "回合",
            [AlchemyTextKey.Temperature] = "温度",
            [AlchemyTextKey.Cold] = "冷却",
            [AlchemyTextKey.Simmer] = "慢熬",
            [AlchemyTextKey.Simmering] = "慢熬",
            [AlchemyTextKey.Boiling] = "沸腾",
            [AlchemyTextKey.Hot] = "过热",
            [AlchemyTextKey.Overheated] = "过热",
            [AlchemyTextKey.Result] = "结果",
            [AlchemyTextKey.Yield] = "产量",
            [AlchemyTextKey.PotionRecipe] = "药剂配方",
            [AlchemyTextKey.Effect] = "效果",
            [AlchemyTextKey.BrewingOrder] = "炼制顺序",
            [AlchemyTextKey.BrewingOrderInstruction] = "依次完成以下步骤",
            [AlchemyTextKey.Complete] = "已完成",
            [AlchemyTextKey.Current] = "当前",
            [AlchemyTextKey.Pending] = "待处理",
            [AlchemyTextKey.Previous] = "上一个",
            [AlchemyTextKey.Next] = "下一个",
            [AlchemyTextKey.Recipe] = "配方",
            [AlchemyTextKey.BrewComplete] = "炼制完成",
            [AlchemyTextKey.PotionQuality] = "药剂品质",
            [AlchemyTextKey.Status] = "状态",
            [AlchemyTextKey.None] = "无",
            [AlchemyTextKey.Total] = "次",
            [AlchemyTextKey.Uncork] = "拔出瓶塞",
            [AlchemyTextKey.Pouring] = "倾倒",
            [AlchemyTextKey.Reset] = "清理",
            [AlchemyTextKey.CurrentStep] = "当前步骤",
            [AlchemyTextKey.Hourglass] = "沙漏",
            [AlchemyTextKey.Distilling] = "蒸馏",
            [AlchemyTextKey.Brewing] = "炼制中",
            [AlchemyTextKey.Bottles] = "瓶",
            [AlchemyTextKey.RecipeComplete] = "配方已完成",
            [AlchemyTextKey.HourglassReady] = "沙漏已就绪",
            [AlchemyTextKey.HourglassRunning] = "沙漏计时中",
            [AlchemyTextKey.EmptyCauldron] = "坩埚为空",
            [AlchemyTextKey.DragInHerbs] = "拖入草药",
            [AlchemyTextKey.CondensateReady] = "冷凝液已备好",
            [AlchemyTextKey.StillReady] = "蒸馏器已就绪",
            [AlchemyTextKey.Ground] = "研磨后的",
            [AlchemyTextKey.MistakesNone] = "失误：无",
            [AlchemyTextKey.SelectQuantityHint] = "先选择草药数量或沙漏回合，再拿起物品"
        };

    private static readonly IReadOnlyDictionary<AlchemyTextKey, string> English =
        Enum.GetValues<AlchemyTextKey>().ToDictionary(key => key, key => key switch
        {
            AlchemyTextKey.CurrentRecipe => "Current recipe",
            AlchemyTextKey.AlchemyWorkshop => "Alchemy workshop",
            AlchemyTextKey.Step => "Step",
            AlchemyTextKey.Loading => "Loading...",
            AlchemyTextKey.Mistakes => "Mistakes",
            AlchemyTextKey.BrewStatus => "Brew status",
            AlchemyTextKey.Herbs => "Herbs",
            AlchemyTextKey.Inventory => "Inventory",
            AlchemyTextKey.Take => "Take",
            AlchemyTextKey.Base => "Base",
            AlchemyTextKey.ChooseSolvent => "Choose solvent",
            AlchemyTextKey.AddHerbs => "Add herbs",
            AlchemyTextKey.GroundHerbsReady => "Ground herbs ready",
            AlchemyTextKey.Grinding => "Grinding",
            AlchemyTextKey.Pull => "Pull",
            AlchemyTextKey.Ready => "Ready",
            AlchemyTextKey.Timing => "Timing",
            AlchemyTextKey.TurnsSelected => "turns selected",
            AlchemyTextKey.Press => "Press",
            AlchemyTextKey.Quantity => "Quantity",
            AlchemyTextKey.Turns => "Turns",
            AlchemyTextKey.Temperature => "Temperature",
            AlchemyTextKey.Cold => "Cold",
            AlchemyTextKey.Simmer => "Simmer",
            AlchemyTextKey.Simmering => "Simmering",
            AlchemyTextKey.Boiling => "Boiling",
            AlchemyTextKey.Hot => "Hot",
            AlchemyTextKey.Overheated => "Overheated",
            AlchemyTextKey.Result => "Result",
            AlchemyTextKey.Yield => "Yield",
            AlchemyTextKey.PotionRecipe => "Potion recipe",
            AlchemyTextKey.Effect => "Effect",
            AlchemyTextKey.BrewingOrder => "Brewing order",
            AlchemyTextKey.BrewingOrderInstruction => "Complete these steps in order",
            AlchemyTextKey.Complete => "Complete",
            AlchemyTextKey.Current => "Current",
            AlchemyTextKey.Pending => "Pending",
            AlchemyTextKey.Previous => "Previous",
            AlchemyTextKey.Next => "Next",
            AlchemyTextKey.Recipe => "Recipe",
            AlchemyTextKey.BrewComplete => "Brew complete",
            AlchemyTextKey.PotionQuality => "Potion quality",
            AlchemyTextKey.Status => "Status",
            AlchemyTextKey.None => "None",
            AlchemyTextKey.Total => "total",
            AlchemyTextKey.Uncork => "Uncork",
            AlchemyTextKey.Pouring => "Pouring",
            AlchemyTextKey.Reset => "Reset",
            AlchemyTextKey.CurrentStep => "Current step",
            AlchemyTextKey.Hourglass => "Hourglass",
            AlchemyTextKey.Distilling => "Distilling",
            AlchemyTextKey.Brewing => "Brewing",
            AlchemyTextKey.Bottles => "bottles",
            AlchemyTextKey.RecipeComplete => "Recipe complete",
            AlchemyTextKey.HourglassReady => "Hourglass ready",
            AlchemyTextKey.HourglassRunning => "Hourglass running",
            AlchemyTextKey.EmptyCauldron => "Empty cauldron",
            AlchemyTextKey.DragInHerbs => "Drag in herbs",
            AlchemyTextKey.CondensateReady => "Condensate ready",
            AlchemyTextKey.StillReady => "Still ready",
            AlchemyTextKey.Ground => "Ground",
            AlchemyTextKey.MistakesNone => "Mistakes: none",
            AlchemyTextKey.SelectQuantityHint => "Select a herb quantity or hourglass turns before picking it up",
            _ => key.ToString()
        });

    private static readonly IReadOnlyDictionary<string, string> ChineseContent =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Marigold"] = "金盏花",
            ["Warm orange petals"] = "温暖的橙色花瓣",
            ["Belladonna"] = "颠茄",
            ["Deep purple berries and pointed leaves"] = "深紫色浆果与尖叶",
            ["Valerian"] = "缬草",
            ["Pale pink clusters of flowers"] = "淡粉色花簇",
            ["Mint"] = "薄荷",
            ["Crisp serrated leaves"] = "清脆的锯齿叶",
            ["Nettle"] = "荨麻",
            ["Prickly dark green leaves"] = "带刺的深绿色叶片",
            ["Sage"] = "鼠尾草",
            ["Thick silver-gray leaves"] = "厚实的银灰色叶片",
            ["Night Owl Potion"] = "夜鹰药剂",
            ["Stay alert and sharp in the dark."] = "在黑暗中保持清醒与敏锐。",
            ["Marigold Decoction"] = "金盏花煎剂",
            ["Slowly restores minor injuries."] = "缓慢恢复轻微伤势。",
            ["Cockerel Potion"] = "雄鸡药剂",
            ["Quickly restores energy."] = "快速恢复精力。",
            ["Pour wine"] = "倒入葡萄酒",
            ["Grind belladonna x2"] = "研磨 2 份颠茄",
            ["Add ground belladonna to the cauldron"] = "将颠茄药粉投入坩埚",
            ["Boil for 2 turns"] = "沸煮 2 回合",
            ["Add raw marigold x1"] = "加入 1 份金盏花",
            ["Boil for 1 turn"] = "沸煮 1 回合",
            ["Distill and condense"] = "蒸馏并冷凝",
            ["Bottle the finished potion"] = "将成品药剂装瓶",
            ["Pour water"] = "倒入清水",
            ["Add raw marigold x2"] = "加入 2 份金盏花",
            ["Simmer for 1 turn"] = "慢熬 1 回合",
            ["Grind valerian x1"] = "研磨 1 份缬草",
            ["Add ground valerian to the cauldron"] = "将缬草药粉投入坩埚",
            ["Remove from heat and bottle"] = "离火并装瓶",
            ["Pour spirits"] = "倒入烈酒",
            ["Grind mint x2"] = "研磨 2 份薄荷",
            ["Add ground mint to the cauldron"] = "将薄荷药粉投入坩埚",
            ["Add raw valerian x1"] = "加入 1 份缬草"
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishEngineMessages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["器具已清理。先向坩埚倒入配方指定的基液。"] = "The tools are clean. Pour the recipe's base liquid into the cauldron.",
            ["选择基液，开始新的炼制。"] = "Choose a base liquid to begin a new brew.",
            ["本轮炼制已经结束，请清理工作台后重新开始。"] = "This brew is finished. Clear the workbench to begin again.",
            ["没有选择基液。 "] = "No base liquid is selected.",
            ["坩埚中已经有基液，清理后才能换用另一种溶剂。"] = "The cauldron already contains a base. Clear it before changing solvents.",
            ["草药已放入研钵。握住杵持续研磨，再将粉末直接投入坩埚。"] = "The herbs are in the mortar. Grind continuously, then add the powder to the cauldron.",
            ["研钵中仍有材料，先完成当前预处理。"] = "The mortar still contains ingredients. Finish preparing them first.",
            ["研钵里没有可研磨的草药。"] = "There are no herbs to grind in the mortar.",
            ["先倒入一种基液，坩埚才能接收研磨后的草药粉。"] = "Pour a base liquid before adding ground herbs.",
            ["研磨完成，细粉已可投入坩埚。"] = "Grinding is complete; the fine powder is ready for the cauldron.",
            ["研磨完成，细粉已经可以投入坩埚。"] = "Grinding is complete; the fine powder is ready for the cauldron.",
            ["药粉已随每次研磨直接落入药液。"] = "The powder fell directly into the liquid as it was ground.",
            ["草药粉直接落入药液，没有结块。"] = "The herb powder fell into the liquid without clumping.",
            ["研磨完成，但草药或数量与当前配方提示不匹配。"] = "Grinding is complete, but the herb or amount does not match the current recipe step.",
            ["研磨材料已直接投入坩埚；它与当前配方提示不匹配，但可以继续操作。"] = "The ground ingredient was added, but it does not match the current recipe step.",
            ["先倒入一种基液，坩埚才能接收草药。"] = "Pour a base liquid before adding herbs.",
            ["原始药草已投入坩埚，叶片在液面上缓慢展开。"] = "The raw herb is in the cauldron, its leaves slowly opening on the surface.",
            ["原始草药已投入坩埚，叶片在液面上缓慢展开。"] = "The raw herb is in the cauldron, its leaves slowly opening on the surface.",
            ["原始草药已投入坩埚；它与当前配方提示不匹配，但可以继续操作。"] = "The raw herb was added, but it does not match the current recipe step.",
            ["空坩埚没有必要靠近炉火。"] = "An empty cauldron does not need the fire.",
            ["坩埚已经降到炉火上。"] = "The cauldron has been lowered over the fire.",
            ["坩埚已经升离炉火，药液开始降温。"] = "The cauldron has been raised away from the fire and is cooling.",
            ["先把装有基液的坩埚降到炉火上。"] = "Lower the filled cauldron over the fire first.",
            ["火焰刚刚舔到锅底。"] = "The flames are just licking the bottom of the cauldron.",
            ["药液进入慢熬区，细泡沿锅壁升起。"] = "The brew is simmering; fine bubbles rise along the cauldron wall.",
            ["液面已经稳定沸腾，可以翻转沙漏。"] = "The liquid is boiling steadily; the hourglass can be turned.",
            ["火舌过高，立刻停止鼓风。"] = "The flames are too high. Stop pumping the bellows.",
            ["炉火发生变化。"] = "The fire has changed.",
            ["把装有基液的坩埚降到炉火上后，才能开始计时。"] = "Lower the filled cauldron over the fire before timing the brew.",
            ["坩埚中没有药液，无法开始蒸馏。"] = "There is no liquid in the cauldron to distill.",
            ["药液已进入蒸馏器。"] = "The brew has entered the still.",
            ["药液进入蒸馏器，第一滴冷凝液落入接收瓶。"] = "The brew has entered the still and the first condensate reached the receiver.",
            ["已完成蒸馏；当前配方提示并不要求此时蒸馏。"] = "Distillation is complete, but the recipe did not call for it yet.",
            ["先在坩埚中准备药液，才能装瓶。"] = "Prepare a brew in the cauldron before bottling.",
            ["药剂完成装瓶。"] = "The potion has been bottled.",
            ["在配方完成前装瓶，成品会被稀释。"] = "Bottling before the recipe is complete will dilute the potion.",
            ["配方尚未完成，过早装瓶会截断药性。"] = "The recipe is incomplete; bottling now will weaken the potion.",
            ["熬煮完成。操作偏差已计入品质，但可以继续下一步。"] = "Cooking is complete. The deviation affects quality, but brewing can continue.",
            ["熬煮已结束；当前并不是配方建议的熬煮步骤。此操作会影响最终品质。"] = "Cooking ended outside the recipe's intended step and will affect quality.",
            ["当前步骤并不需要熬煮。"] = "The current step does not require cooking.",
            ["坩埚未处于炉火上，沙漏无法计量有效熬煮。"] = "The cauldron is not over the fire, so the hourglass cannot time cooking.",
            ["当前药剂不应在此刻进入蒸馏器。"] = "This potion should not be distilled yet.",
            ["坩埚在过热区停留过久，药性被灼伤。"] = "The cauldron stayed overheated too long and scorched the potion.",
            ["累计三次失误，药液已经变成不可用的灰烬与废液。"] = "Three mistakes ruined the brew, leaving only ash and waste.",
            ["物品栏中没有这种草药。"] = "That herb is not in the inventory.",
            ["这种草药的库存不足。"] = "There is not enough of that herb.",
            ["未知的炼金动作。"] = "Unknown alchemy action.",
            ["完美药剂：额外得到 3 瓶成品。"] = "Perfect potion: 3 finished bottles produced.",
            ["完美药剂：色泽清澈，额外得到 3 瓶成品。"] = "Perfect potion: clear and bright, with 3 finished bottles produced."
        };

    public static string Get(AlchemyTextKey key, bool useEnglish) =>
        (useEnglish ? English : Chinese)[key];

    public static string Content(string source, bool useEnglish) =>
        useEnglish ? source : ChineseContent.GetValueOrDefault(source, source);

    public static string BaseName(BaseLiquid liquid, bool useEnglish) => liquid switch
    {
        BaseLiquid.Water => useEnglish ? "Water" : "清水",
        BaseLiquid.Wine => useEnglish ? "Wine" : "葡萄酒",
        BaseLiquid.Spirits => useEnglish ? "Spirits" : "烈酒",
        BaseLiquid.Oil => useEnglish ? "Oil" : "油",
        _ => liquid.ToString()
    };

    public static string HeatName(HeatBand band, bool useEnglish) => band switch
    {
        HeatBand.Cold => Get(AlchemyTextKey.Cold, useEnglish),
        HeatBand.Simmering => Get(AlchemyTextKey.Simmering, useEnglish),
        HeatBand.Boiling => Get(AlchemyTextKey.Boiling, useEnglish),
        HeatBand.Overheated => Get(AlchemyTextKey.Overheated, useEnglish),
        _ => band.ToString()
    };

    public static string QualityGrade(int mistakes, bool useEnglish)
    {
        var score = Math.Clamp(84 - ((Math.Max(1, mistakes) - 1) * 7), 0, 84);
        return score switch
        {
            >= 80 => useEnglish ? "Excellent" : "优秀",
            >= 60 => useEnglish ? "Fair" : "普通",
            _ => useEnglish ? "Poor" : "糟糕"
        };
    }

    public static string Outcome(BrewOutcome outcome, int mistakes, bool useEnglish) => outcome switch
    {
        BrewOutcome.Brewing => Get(AlchemyTextKey.Brewing, useEnglish),
        BrewOutcome.Perfect => useEnglish ? "Perfect" : "完美",
        BrewOutcome.Diluted => QualityGrade(mistakes, useEnglish),
        BrewOutcome.Failed => useEnglish ? "Failed" : "失败",
        _ => outcome.ToString()
    };

    public static string StepGestureHint(RecipeStep step, bool useEnglish, bool bottleFromCauldron = false)
    {
        if (useEnglish)
        {
            return step.Kind switch
            {
                RecipeStepKind.PourBase => "Drag the bottle to the cauldron and raise the pointer to pour",
                RecipeStepKind.Grind => "Drag herbs into the mortar, then grind in a continuous circle",
                RecipeStepKind.AddIngredient when step.Form == IngredientForm.Ground => "Drag ground herbs from the mortar into the cauldron",
                RecipeStepKind.AddIngredient => "Take an herb from the drawer and drop it into the liquid",
                RecipeStepKind.Cook => "Pull and compress the bellows, then flip the hourglass",
                RecipeStepKind.Distill => "Fit an empty bottle under the condenser, then press the pump",
                RecipeStepKind.Bottle => bottleFromCauldron
                    ? "Dip an empty bottle into the cauldron to collect the potion"
                    : "Drag an empty bottle to the condenser outlet",
                _ => string.Empty
            };
        }

        return step.Kind switch
        {
            RecipeStepKind.PourBase => "把基液瓶拖到坩埚上方，再抬高鼠标倾倒",
            RecipeStepKind.Grind => "将草药拖入研钵并持续画圈研磨",
            RecipeStepKind.AddIngredient when step.Form == IngredientForm.Ground => "把研磨后的药粉从研钵拖入坩埚",
            RecipeStepKind.AddIngredient => "从药材柜取出草药并投入药液",
            RecipeStepKind.Cook => "拉动并压合风箱，然后翻转沙漏",
            RecipeStepKind.Distill => "先把空瓶放到冷凝出口下，再按压泵杆完成收集",
            RecipeStepKind.Bottle => bottleFromCauldron
                ? "将空瓶拖入炼药锅中承接药剂"
                : "将空瓶拖到冷凝出口",
            _ => string.Empty
        };
    }

    public static string EngineMessage(string message, bool useEnglish)
    {
        if (!useEnglish || string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        if (EnglishEngineMessages.TryGetValue(message, out var exact))
        {
            return exact;
        }

        var match = MistakeRegex().Match(message);
        if (match.Success)
        {
            return $"Mistake {match.Groups[1].Value}: {EngineMessage(match.Groups[2].Value, true)}";
        }

        match = AddedHerbRegex().Match(message);
        if (match.Success)
        {
            return $"Added {match.Groups[1].Value}/{match.Groups[2].Value} portions of herbs; add more of the same herb to continue.";
        }

        match = AddedPowderRegex().Match(message);
        if (match.Success)
        {
            return $"Ground and added {match.Groups[1].Value}/{match.Groups[2].Value} portions; continue with the same herb.";
        }

        match = PouredBaseRegex().Match(message);
        if (match.Success)
        {
            var liquid = TranslateChineseBase(match.Groups[1].Value);
            return match.Groups[2].Value.Contains("不同", StringComparison.Ordinal)
                ? $"Poured {liquid}. It differs from the recipe, but brewing can continue."
                : $"Poured {liquid}. The surface is calm and the first step is complete.";
        }

        match = HourglassStartedRegex().Match(message);
        if (match.Success)
        {
            return $"The hourglass is timing {match.Groups[1].Value} turn(s). Keep the brew in the recommended heat band.";
        }

        match = CookedTurnsRegex().Match(message);
        if (match.Success)
        {
            return $"Cooking finished after {match.Groups[1].Value} turn(s) with stable heat.";
        }

        match = TurnMismatchRegex().Match(message);
        if (match.Success)
        {
            return $"Cooked for {match.Groups[1].Value} turn(s); the recipe recommends {match.Groups[2].Value}.";
        }

        if (message.Contains("计时期间温度没有稳定保持", StringComparison.Ordinal))
        {
            return "The temperature did not remain in the recommended band while timing.";
        }

        if (message.Contains("品质：", StringComparison.Ordinal))
        {
            return message.Contains("1 瓶", StringComparison.Ordinal)
                ? "The brew is complete at reduced quality, yielding 1 bottle."
                : "The brew is complete at reduced quality.";
        }

        return message.Any(character => character is >= '\u4e00' and <= '\u9fff')
            ? "The workshop state has been updated."
            : message;
    }

    private static string TranslateChineseBase(string value) => value switch
    {
        "清水" => "water",
        "葡萄酒" => "wine",
        "烈酒" => "spirits",
        "油" => "oil",
        _ => "the selected base"
    };

    [GeneratedRegex("^失误 (\\d+)：(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MistakeRegex();

    [GeneratedRegex("^已投入 (\\d+)/(\\d+) 份药草", RegexOptions.CultureInvariant)]
    private static partial Regex AddedHerbRegex();

    [GeneratedRegex("^已研磨并投入 (\\d+)/(\\d+) 份药粉", RegexOptions.CultureInvariant)]
    private static partial Regex AddedPowderRegex();

    [GeneratedRegex("^已倒入(清水|葡萄酒|烈酒|油)。(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PouredBaseRegex();

    [GeneratedRegex("^沙漏开始(?:计时：|计量 )?(\\d+) 回合", RegexOptions.CultureInvariant)]
    private static partial Regex HourglassStartedRegex();

    [GeneratedRegex("^(\\d+) 回合熬煮完成", RegexOptions.CultureInvariant)]
    private static partial Regex CookedTurnsRegex();

    [GeneratedRegex("^本次熬煮为 (\\d+) 回合，配方建议为 (\\d+) 回合", RegexOptions.CultureInvariant)]
    private static partial Regex TurnMismatchRegex();
}
