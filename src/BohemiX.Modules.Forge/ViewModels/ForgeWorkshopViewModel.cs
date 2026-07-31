using System.Collections.ObjectModel;
using Avalonia.Threading;
using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Input;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Services;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.Modules.Forge.ViewModels;

public partial class ForgeWorkshopViewModel : ObservableObject, IDisposable, IActivatableModule
{
    private readonly ForgeEngine engine;
    private readonly IForgeAudioService audio;
    private readonly IForgeProgressStore progressStore;
    private readonly DispatcherTimer timer;
    private DateTimeOffset lastTick = DateTimeOffset.UtcNow;
    private DateTimeOffset lastGrindSound = DateTimeOffset.MinValue;
    private ForgeResult? recordedResult;
    private ForgeProfile profile = new(1, [], new Dictionary<string, int>());
    private bool isActive;
    private bool useEnglish;
    private bool disposed;

    public event EventHandler? ExitRequested;

    public ForgeWorkshopViewModel(
        ForgeEngine engine,
        IForgeCatalog catalog,
        IForgeAudioService audio,
        IForgeProgressStore progressStore,
        ForgeGraphicsSettings? graphicsSettings = null)
    {
        this.engine = engine;
        this.audio = audio;
        this.progressStore = progressStore;
        GraphicsSettings = graphicsSettings ?? new ForgeGraphicsSettings();
        Recipes = catalog.Recipes;
        RecipeChoices = Recipes.Select(recipe => new ForgeRecipeChoiceViewModel(
            recipe,
            () => Run(new SelectRecipeCommand(recipe.Id)))).ToArray();
        Materials = catalog.Materials;
        snapshot = engine.Snapshot;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += OnTick;
    }

    public IReadOnlyList<ForgeRecipeDefinition> Recipes { get; }
    public IReadOnlyList<ForgeRecipeChoiceViewModel> RecipeChoices { get; }
    public IReadOnlyList<ForgeMaterialDefinition> Materials { get; }
    public ForgeGraphicsSettings GraphicsSettings { get; }
    public ObservableCollection<QualityReason> ResultReasons { get; } = [];

    private ForgeSnapshot snapshot;

    public ForgeSnapshot Snapshot
    {
        get => snapshot;
        private set => SetProperty(ref snapshot, value);
    }

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private bool isExitConfirmationOpen;

    [ObservableProperty]
    private bool isRecipeBookOpen;

    [ObservableProperty]
    private bool apprenticeHints = true;

    [ObservableProperty]
    private string? renderError;

    [ObservableProperty]
    private double hammerCharge;

    [ObservableProperty]
    private bool isHammerCharging;

    [ObservableProperty]
    private double grinderSpeed;

    public string HammerChargePercent => $"{Math.Round(HammerCharge * 100):0}%";
    public string GrinderSpeedText => $"{GrinderSpeed:0.00}x";

    public bool IsRecipeSelect => Snapshot.State == ForgeStateId.RecipeSelect;
    public bool IsMaterialSelect => Snapshot.State == ForgeStateId.MaterialSelect;
    public bool IsHeating => Snapshot.State is ForgeStateId.Heating or ForgeStateId.Reheat;
    public bool IsHammering => Snapshot.State is ForgeStateId.Hammering or ForgeStateId.RotateWorkpiece;
    public bool IsQuenching => Snapshot.State == ForgeStateId.Quenching;
    public bool IsGrinding => Snapshot.State == ForgeStateId.Grinding;
    public bool IsGrindingPreview => IsGrinding && !Snapshot.GrindingEngaged;
    public bool IsGrindingActive => IsGrinding && Snapshot.GrindingEngaged;
    public bool IsInspection => Snapshot.State == ForgeStateId.Inspection;
    public bool IsResult => Snapshot.State == ForgeStateId.Result;
    public bool HasActiveCraft => !IsRecipeSelect && !IsResult;
    public bool ShowRecipeStrip => Snapshot.RecipeId is not null && !IsRecipeSelect && !IsMaterialSelect && !IsResult;
    public string StageTitle => Snapshot.State switch
    {
        ForgeStateId.RecipeSelect => L("选择配方", "Choose recipe"),
        ForgeStateId.MaterialSelect => L("选择钢材", "Choose steel"),
        ForgeStateId.Heating => L("初次加热", "Initial heating"),
        ForgeStateId.Hammering => L("锤击成形", "Hammer shaping"),
        ForgeStateId.RotateWorkpiece => L("翻转工件", "Turn over"),
        ForgeStateId.Reheat => L("重新加热", "Reheat"),
        ForgeStateId.Quenching => L("淬火", "Quench"),
        ForgeStateId.Grinding => L("打磨", "Grind"),
        ForgeStateId.Inspection => L("检验", "Inspect"),
        ForgeStateId.Result => L("锻造结果", "Forge result"),
        _ => string.Empty
    };
    public string HeatDescription => Snapshot.HeatBand switch
    {
        ForgeHeatBand.Cold => L("金属尚未发光", "Metal is not glowing"),
        ForgeHeatBand.DarkRed => L("暗红微光", "Dark red glow"),
        ForgeHeatBand.CherryRed => L("樱桃红", "Cherry red"),
        ForgeHeatBand.OrangeRed => L("明亮橙红", "Bright orange-red"),
        ForgeHeatBand.Yellow => L("耀眼黄色", "Blinding yellow"),
        ForgeHeatBand.Burnt => L("表面正在烧损", "Surface is burning"),
        _ => string.Empty
    };
    public string HammerFaceText => L("平锤面", "Flat face");
    public string RecommendedHammerFaceText => HammerFaceText;
    private ForgeRecipeDefinition? CurrentRecipe => Snapshot.RecipeId is null
        ? null
        : Recipes.FirstOrDefault(recipe => recipe.Id.Equals(Snapshot.RecipeId, StringComparison.OrdinalIgnoreCase));
    public string RecipeDisplayName => CurrentRecipe is { } recipe
        ? useEnglish ? recipe.Name : recipe.NameZh ?? recipe.Name
        : Snapshot.RecipeName ?? string.Empty;
    public string MaterialDisplayName => Snapshot.MaterialId switch
    {
        "soft-steel" => L("软钢", "Soft Steel"),
        "balanced-steel" => L("均衡钢", "Balanced Steel"),
        "high-carbon-steel" => L("高碳钢", "High-Carbon Steel"),
        _ => Snapshot.MaterialName ?? string.Empty
    };
    public string ActiveZoneText => CurrentRecipe?.Zones
        .FirstOrDefault(zone => zone.Label.Equals(Snapshot.ActiveZone, StringComparison.OrdinalIgnoreCase)) is { } zone
            ? useEnglish ? zone.Label : zone.LabelZh ?? zone.Label
            : null
        ?? Snapshot.ActiveZone
        ?? string.Empty;
    public string RecipeRouteText => CurrentRecipe is { } recipe
        ? string.Join(" → ", recipe.Zones.OrderBy(zone => zone.Order)
            .Select(zone => useEnglish ? zone.Label : zone.LabelZh ?? zone.Label))
        : string.Empty;
    public string CurrentOperationText => string.Format(L("当前工序  {0}", "Current operation  {0}"), ActiveZoneText);
    public string CraftFeedbackText => Snapshot.State switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat when Snapshot.HeatBand is ForgeHeatBand.Cold or ForgeHeatBand.DarkRed
            => L("继续加热钢坯，观察暗红光泽逐渐变亮。", "Keep heating the billet and watch the dark red glow brighten."),
        ForgeStateId.Heating or ForgeStateId.Reheat when Snapshot.HeatBand is ForgeHeatBand.CherryRed or ForgeHeatBand.OrangeRed
            => L("金属已可加工，将它移到铁砧上。", "The metal is workable. Move it to the anvil."),
        ForgeStateId.Heating or ForgeStateId.Reheat
            => L("亮黄色表示温度过高，停止鼓风并将工件移出炉膛。", "Bright yellow means the metal is too hot. Stop the bellows and remove it from the forge."),
        ForgeStateId.Hammering when Snapshot.CanProceed
            => L("工件已成形，请选择淬火介质。", "The shape is ready. Choose a quenching medium."),
        ForgeStateId.Hammering when Snapshot.HeatBand is ForgeHeatBand.Cold or ForgeHeatBand.DarkRed
            => L("金属已经变暗，继续锤击可能开裂，请重新加热。", "The metal has gone dark; continued work may crack it. Reheat it."),
        ForgeStateId.Hammering when Snapshot.LastMessage == "The strike falls outside the recipe working areas."
            => L("这一锤落在加工区域之外，请观察轮廓并修正需要调整的位置。", "That strike missed a working area. Inspect the shape and correct any area that needs it."),
        ForgeStateId.Hammering when Snapshot.LastMessage == "The hammer face does not suit this operation."
            => L("请继续使用平锤面加工。", "Continue this step with the flat hammer face."),
        ForgeStateId.Hammering when Snapshot.LastMessage == "This area is formed; choose any remaining area."
            => L("该区域已经成形，可继续加工其他未完成区域。", "This area is formed. Work on any area that remains unfinished."),
        ForgeStateId.Hammering
            => string.Format(L("当前落锤区域：{0}。可按任意顺序加工，并随时修正不平整处。", "Current strike area: {0}. Work in any order and correct uneven spots as needed."), ActiveZoneText),
        ForgeStateId.RotateWorkpiece => L("用钳子夹稳工件，等待翻面完成。", "Hold the workpiece with the tongs and wait for the turn to finish."),
        ForgeStateId.Quenching when Snapshot.CanProceed => L("第一阵蒸汽已经散去，可以完成淬火。", "The first burst of steam has settled. You can finish quenching."),
        ForgeStateId.Quenching => L("平稳浸入刃口，不要猛然撞入淬火槽。", "Immerse the working edge steadily; do not slam it into the quench tank."),
        ForgeStateId.Grinding when Snapshot.CanProceed => L("刃口已经均匀，可以进行检验。", "The edge is even. Proceed to inspection."),
        ForgeStateId.Grinding => L("保持均匀速度，让整段刃口稳定接触砂轮。", "Maintain an even speed so the entire edge contacts the wheel."),
        ForgeStateId.Inspection => L("转动工件，检查轮廓、刃口、过薄区域和裂纹。", "Turn the workpiece and check the outline, edge, thin spots, and cracks."),
        _ => string.Empty
    };
    public string ResultTitle => Snapshot.Result?.Quality switch
    {
        ForgeQuality.Broken => L("损坏", "Broken"),
        ForgeQuality.Poor => L("粗劣", "Poor"),
        ForgeQuality.Normal => L("普通", "Normal"),
        ForgeQuality.Fine => L("精良", "Fine"),
        ForgeQuality.Excellent => L("卓越", "Excellent"),
        ForgeQuality.Masterwork => L("大师之作", "Masterwork"),
        _ => string.Empty
    };
    public string ResultScoreText => Snapshot.Result is null
        ? string.Empty
        : string.Format(L("工艺评分  {0}", "Craft score  {0}"), Snapshot.Result.Score);

    public string RecipeTooltip => L("配方  Tab", "Recipe  Tab");
    public string PauseTooltip => L("暂停  Esc", "Pause  Esc");
    public string ExitWorkshopText => L("退出锻造", "Exit forge");
    public string ForgeLedgerTitle => L("锻造账册", "FORGE LEDGER");
    public string ChooseRecipeTitle => L("选择锻造配方", "Choose a forging recipe");
    public string ChooseRecipeDescription => L("每种武器都有独特的轮廓、加工热区和淬火方式", "Each weapon has its own profile, working heat, and quenching method");
    public string MaterialStockTitle => L("钢材库", "MATERIAL STOCK");
    public string ChooseSteelTitle => L("选择钢材", "Choose steel");
    public string ChooseSteelDescription => L("不同钢材的加工热区、冷却速度和开裂风险各不相同", "Each steel has a different working heat, cooling rate, and crack risk");
    public string SoftSteelText => L("软钢", "Soft Steel");
    public string SoftSteelDescription => L("易于掌握 · 容易成形 · 适合水淬", "Forgiving · Easy to shape · Water quench");
    public string BalancedSteelText => L("均衡钢", "Balanced Steel");
    public string BalancedSteelDescription => L("性能稳定 · 加工热区宽容", "Reliable · Broad working heat");
    public string HighCarbonSteelText => L("高碳钢", "High-Carbon Steel");
    public string HighCarbonSteelDescription => L("刃口锋利 · 易开裂 · 适合油淬", "Sharp · Crack-prone · Oil quench");
    public string BellowsText => L("鼓风", "Bellows");
    public string MoveToAnvilText => L("移至铁砧", "Move to anvil");
    public string TurnOverText => L("翻面", "Turn over");
    public string ReturnToForgeText => L("返回炉膛", "Return to forge");
    public string WaterQuenchText => L("水淬", "Water quench");
    public string OilQuenchText => L("油淬", "Oil quench");
    public string FinishQuenchingText => L("完成淬火", "Finish quenching");
    public string StartGrindingText => L("开始打磨", "Start grinding");
    public string KeepQuenchedEdgeText => L("保留淬火刃口", "Keep quenched edge");
    public string DecreaseSpeedTooltip => L("减速  S / ↓", "Slow down  S / ↓");
    public string IncreaseSpeedTooltip => L("加速  W / ↑", "Speed up  W / ↑");
    public string InspectText => L("检验", "Inspect");
    public string SubmitInspectionText => L("提交检验", "Submit inspection");
    public string CraftVerdictText => L("工艺评定", "CRAFT VERDICT");
    public string ForgeAgainText => L("再次锻造", "Forge again");
    public string SmithReferenceText => L("铁匠参考", "SMITH'S REFERENCE");
    public string RecipeGuidanceText => L("观察金属颜色；橙红状态最适合加工。根据形状需要及时翻面和回炉。", "Watch the metal color; orange-red is most workable. Turn and reheat as the shape requires.");
    public string CollapseText => L("收起", "Close");
    public string ForgePausedTitle => L("锻造已暂停", "Forge paused");
    public string ForgePausedDescription => L("模拟与工件温度变化已暂停", "Simulation and workpiece temperature are paused");
    public string ResumeForgingText => L("继续锻造", "Resume forging");
    public string ApprenticeHintsText => L("学徒提示", "Apprentice hints");
    public string AbandonForgeTitle => L("放弃当前锻造？", "Abandon this forging session?");
    public string AbandonForgeDescription => L("未完成的工件和本次加工记录将被丢弃。", "The unfinished workpiece and this session's work will be discarded.");
    public string AbandonAndReturnText => L("放弃并返回", "Abandon and return");
    public int BestScore => Snapshot.RecipeId is null || Snapshot.MaterialId is null
        ? 0
        : profile.BestScores.GetValueOrDefault($"{Snapshot.RecipeId}:{Snapshot.MaterialId}");

    public bool HasRenderError => !string.IsNullOrWhiteSpace(RenderError);

    public void UseLanguage(bool english)
    {
        if (useEnglish == english)
        {
            return;
        }

        useEnglish = english;
        foreach (var choice in RecipeChoices)
        {
            choice.UseLanguage(english);
        }

        NotifyLocalizedText();
        RefreshResultReasons();
    }

    private string L(string chinese, string english) => useEnglish ? english : chinese;

    public void SetRenderError(string? message)
    {
        RenderError = message;
        OnPropertyChanged(nameof(HasRenderError));
    }

    public void SetHammerInteraction(double charge, bool charging)
    {
        HammerCharge = Math.Clamp(charge, 0, 1);
        IsHammerCharging = charging;
        OnPropertyChanged(nameof(HammerChargePercent));
    }

    public void Start()
    {
        if (disposed || isActive)
        {
            return;
        }

        isActive = true;
        lastTick = DateTimeOffset.UtcNow;
        UpdateTimerState();
    }

    public void Activate() => Start();

    public void Stop()
    {
        isActive = false;
        timer.Stop();
        audio.StopAll();
    }

    public void Deactivate() => Stop();

    public ForgeCommandResult StrikeAt(double x, double y, double force)
    {
        if (IsPaused || Snapshot.State != ForgeStateId.Hammering)
        {
            return new ForgeCommandResult(false, IsPaused ? "Paused" : "The workpiece is not on the anvil.", Snapshot);
        }

        return Run(new HammerStrikeCommand(x, y, force, HammerFace.Flat), ForgeSoundCue.Hammer, force);
    }

    public void PumpFromGesture(double strength) => Run(new PumpBellowsCommand(strength), ForgeSoundCue.Bellows, strength);

    public void UpdateQuench(double depth, double speed)
    {
        if (Snapshot.State == ForgeStateId.Quenching)
        {
            Run(new UpdateQuenchCommand(depth, speed));
        }
    }

    public void FinishQuench()
    {
        if (Snapshot.State == ForgeStateId.Quenching)
        {
            Run(new CompleteQuenchCommand(), ForgeSoundCue.Quench);
        }
    }

    public void GrindAt(double position, double speed, double delta)
    {
        if (Snapshot.State == ForgeStateId.Grinding && Snapshot.GrindingEngaged)
        {
            var result = Run(new GrindStrokeCommand(position, speed, delta));
            var now = DateTimeOffset.UtcNow;
            if (result.Accepted && now - lastGrindSound >= TimeSpan.FromMilliseconds(95))
            {
                var intensity = Math.Clamp(
                    .18 + speed * .42 + Snapshot.GrinderSpeed * .30 + Snapshot.GrindingQuality * .10,
                    .18,
                    1);
                audio.Play(ForgeSoundCue.Grind, intensity);
                lastGrindSound = now;
            }
        }
    }

    public bool AdjustGrinderSpeed(int direction)
    {
        if (!IsGrindingActive || direction == 0)
        {
            return false;
        }

        GrinderSpeed = AdjustedGrinderSpeed(GrinderSpeed, direction);
        return true;
    }

    public bool HandleKeyboardAction(ForgeInputAction action)
    {
        if (action == ForgeInputAction.TogglePause)
        {
            TogglePause();
            return true;
        }

        if (IsPaused)
        {
            return false;
        }

        return action switch
        {
            ForgeInputAction.ToggleRecipe => ToggleRecipeFromKeyboard(),
            ForgeInputAction.PumpBellows => IsHeating && Run(new PumpBellowsCommand(.85), ForgeSoundCue.Bellows).Accepted,
            ForgeInputAction.ContextAction => RunContextAction(),
            ForgeInputAction.RotateWorkpiece => IsHammering && Run(new RotateWorkpieceCommand(), ForgeSoundCue.Rotate).Accepted,
            ForgeInputAction.Reheat => IsHammering && Run(new MoveToForgeCommand()).Accepted,
            ForgeInputAction.QuenchWater => IsHammering && Snapshot.CanProceed && Run(new BeginQuenchCommand(QuenchMedium.Water), ForgeSoundCue.Quench, .55).Accepted,
            ForgeInputAction.QuenchOil => IsHammering && Snapshot.CanProceed && Run(new BeginQuenchCommand(QuenchMedium.Oil), ForgeSoundCue.Quench, .4).Accepted,
            ForgeInputAction.BeginGrinding => IsGrindingPreview && Run(new BeginGrindingCommand()).Accepted,
            ForgeInputAction.SkipGrinding => IsGrindingPreview && Run(new SkipGrindingCommand()).Accepted,
            ForgeInputAction.IncreaseGrinderSpeed => AdjustGrinderSpeed(1),
            ForgeInputAction.DecreaseGrinderSpeed => AdjustGrinderSpeed(-1),
            _ => false
        };
    }

    private bool ToggleRecipeFromKeyboard()
    {
        if (Snapshot.RecipeId is null || IsResult)
        {
            return false;
        }

        ToggleRecipeBook();
        return true;
    }

    private bool RunContextAction()
    {
        if (IsHeating && Snapshot.CanProceed)
        {
            return Run(new MoveToAnvilCommand()).Accepted;
        }
        if (IsQuenching)
        {
            return Run(new CompleteQuenchCommand(), ForgeSoundCue.Quench).Accepted;
        }
        if (IsGrindingActive && Snapshot.CanProceed)
        {
            return Run(new SubmitInspectionCommand()).Accepted;
        }
        if (IsInspection)
        {
            return Run(new SubmitInspectionCommand()).Accepted;
        }

        return false;
    }

    internal static double AdjustedGrinderSpeed(double current, int direction) =>
        Math.Clamp(Math.Round((current + Math.Sign(direction) * .10) * 20) / 20, 0, 1.35);

    [RelayCommand]
    private void SelectMaterial(ForgeMaterialDefinition material) => Run(new SelectMaterialCommand(material.Id));

    [RelayCommand]
    private void SelectSoftSteel() => Run(new SelectMaterialCommand("soft-steel"));

    [RelayCommand]
    private void SelectBalancedSteel() => Run(new SelectMaterialCommand("balanced-steel"));

    [RelayCommand]
    private void SelectHighCarbonSteel() => Run(new SelectMaterialCommand("high-carbon-steel"));

    [RelayCommand]
    private void PumpBellows() => Run(new PumpBellowsCommand(.85), ForgeSoundCue.Bellows);

    [RelayCommand]
    private void MoveToAnvil() => Run(new MoveToAnvilCommand());

    [RelayCommand]
    private void MoveToForge() => Run(new MoveToForgeCommand());

    [RelayCommand]
    private void RotateWorkpiece() => Run(new RotateWorkpieceCommand(), ForgeSoundCue.Rotate);

    [RelayCommand]
    private void QuenchWater() => Run(new BeginQuenchCommand(QuenchMedium.Water), ForgeSoundCue.Quench, .55);

    [RelayCommand]
    private void QuenchOil() => Run(new BeginQuenchCommand(QuenchMedium.Oil), ForgeSoundCue.Quench, .4);

    [RelayCommand]
    private void CompleteQuench() => FinishQuench();

    [RelayCommand]
    private void BeginGrinding() => Run(new BeginGrindingCommand());

    [RelayCommand]
    private void SkipGrinding() => Run(new SkipGrindingCommand());

    [RelayCommand]
    private void IncreaseGrinderSpeed() => AdjustGrinderSpeed(1);

    [RelayCommand]
    private void DecreaseGrinderSpeed() => AdjustGrinderSpeed(-1);

    [RelayCommand]
    private void SubmitInspection() => Run(new SubmitInspectionCommand());

    [RelayCommand]
    private void Restart() => Run(new RestartForgeCommand());

    [RelayCommand]
    private void RequestExit()
    {
        if (HasActiveCraft)
        {
            IsExitConfirmationOpen = true;
            return;
        }

        ExitWorkshop(resetSession: IsResult);
    }

    [RelayCommand]
    private void CancelExit() => IsExitConfirmationOpen = false;

    [RelayCommand]
    private void ConfirmExit()
    {
        ResetWorkshop();
        IsExitConfirmationOpen = false;
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    [RelayCommand]
    private void ToggleRecipeBook() => IsRecipeBookOpen = !IsRecipeBookOpen;

    [RelayCommand]
    private void ToggleHints() => ApprenticeHints = !ApprenticeHints;

    partial void OnGrinderSpeedChanged(double value)
    {
        OnPropertyChanged(nameof(GrinderSpeedText));
        if (Snapshot.State == ForgeStateId.Grinding && Snapshot.GrindingEngaged &&
            Math.Abs(Snapshot.GrinderSpeed - value) > .005)
        {
            Run(new SetGrinderSpeedCommand(value));
        }
    }

    partial void OnApprenticeHintsChanged(bool value)
    {
        profile = profile with { ApprenticeHints = value };
    }

    partial void OnIsPausedChanged(bool value) => UpdateTimerState();

    partial void OnIsExitConfirmationOpenChanged(bool value)
    {
        lastTick = DateTimeOffset.UtcNow;
        UpdateTimerState();
    }

    private void ExitWorkshop(bool resetSession)
    {
        if (resetSession)
        {
            ResetWorkshop();
        }

        IsExitConfirmationOpen = false;
        timer.Stop();
        audio.StopAll();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetWorkshop()
    {
        Snapshot = engine.Execute(new AbandonForgeCommand()).Snapshot;
        recordedResult = null;
        IsPaused = false;
        IsRecipeBookOpen = false;
        audio.StopAll();
        NotifyState();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - lastTick;
        lastTick = now;
        if (!IsPaused)
        {
            var previous = snapshot;
            snapshot = engine.Tick(elapsed);
            NotifyTickChanges(previous, snapshot);
            UpdateTimerState();
        }
    }

    private void NotifyTickChanges(ForgeSnapshot previous, ForgeSnapshot current)
    {
        if (previous.State != current.State)
        {
            NotifyState();
            return;
        }

        if (previous.HeatBand != current.HeatBand)
        {
            OnPropertyChanged(nameof(HeatDescription));
            OnPropertyChanged(nameof(CraftFeedbackText));
        }

        if (previous.CanProceed != current.CanProceed ||
            !ReferenceEquals(previous.Result, current.Result))
        {
            OnPropertyChanged(nameof(Snapshot));
            OnPropertyChanged(nameof(CraftFeedbackText));
        }

        if (!string.Equals(previous.LastMessage, current.LastMessage, StringComparison.Ordinal) ||
            !string.Equals(previous.ActiveZone, current.ActiveZone, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ActiveZoneText));
            OnPropertyChanged(nameof(CraftFeedbackText));
        }

        if (!ReferenceEquals(previous.Result, current.Result))
        {
            OnPropertyChanged(nameof(ResultTitle));
            OnPropertyChanged(nameof(ResultScoreText));
            RefreshResultReasons();
        }
    }

    private ForgeCommandResult Run(ForgeCommand command, ForgeSoundCue? cue = null, double intensity = 1)
    {
        if (IsPaused && command is not RestartForgeCommand)
        {
            return new ForgeCommandResult(false, "Paused", Snapshot);
        }

        var result = engine.Execute(command);
        Snapshot = result.Snapshot;
        if (result.Accepted && cue is not null)
        {
            audio.Play(cue.Value, intensity);
        }
        NotifyState();
        UpdateTimerState();
        _ = RecordResultIfNeededAsync();
        return result;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsRecipeSelect));
        OnPropertyChanged(nameof(IsMaterialSelect));
        OnPropertyChanged(nameof(IsHeating));
        OnPropertyChanged(nameof(IsHammering));
        OnPropertyChanged(nameof(IsQuenching));
        OnPropertyChanged(nameof(IsGrinding));
        OnPropertyChanged(nameof(IsGrindingPreview));
        OnPropertyChanged(nameof(IsGrindingActive));
        OnPropertyChanged(nameof(IsInspection));
        OnPropertyChanged(nameof(IsResult));
        OnPropertyChanged(nameof(ShowRecipeStrip));
        OnPropertyChanged(nameof(StageTitle));
        OnPropertyChanged(nameof(HeatDescription));
        OnPropertyChanged(nameof(HammerFaceText));
        OnPropertyChanged(nameof(RecommendedHammerFaceText));
        OnPropertyChanged(nameof(RecipeDisplayName));
        OnPropertyChanged(nameof(MaterialDisplayName));
        OnPropertyChanged(nameof(ActiveZoneText));
        OnPropertyChanged(nameof(RecipeRouteText));
        OnPropertyChanged(nameof(CurrentOperationText));
        OnPropertyChanged(nameof(CraftFeedbackText));
        OnPropertyChanged(nameof(ResultTitle));
        OnPropertyChanged(nameof(ResultScoreText));
        OnPropertyChanged(nameof(BestScore));
        if (Math.Abs(GrinderSpeed - Snapshot.GrinderSpeed) > .005)
        {
            GrinderSpeed = Snapshot.GrinderSpeed;
        }
        RefreshResultReasons();
    }

    private void RefreshResultReasons()
    {
        ResultReasons.Clear();
        if (snapshot.Result is not null)
        {
            foreach (var reason in snapshot.Result.Reasons)
            {
                ResultReasons.Add(LocalizeReason(reason));
            }
        }
    }

    private QualityReason LocalizeReason(QualityReason reason)
    {
        if (!useEnglish)
        {
            return reason;
        }

        return reason with
        {
            Category = reason.Category switch
            {
                "温控" => "Heat",
                "形状" => "Shape",
                "工序" => "Process",
                "回炉" => "Reheating",
                "淬火" => "Quenching",
                "打磨" => "Grinding",
                "损伤" => "Damage",
                _ => reason.Category
            },
            Message = reason.Message switch
            {
                "大部分锤击都保持在明亮的橙红工作热区。" => "Most strikes were made in the bright orange-red working heat.",
                "较多加工发生在金属光泽已经变暗之后。" => "Too much work was done after the metal had gone dark.",
                "轮廓与厚度已经贴近配方目标。" => "The outline and thickness closely match the recipe target.",
                "成品仍存在局部厚度或轮廓误差。" => "The finished piece still has local thickness or outline errors.",
                "各加工区域按照配方顺序完成。" => "The working areas were completed in the intended process.",
                "部分锤击越过了当前应加工的区域。" => "Some strikes landed outside the intended working areas.",
                "回炉次数保持在配方建议的加工节奏内。" => "Reheating stayed within the recipe's recommended rhythm.",
                "回炉次数影响了钢材的一致性。" => "The number of reheats reduced the steel's consistency.",
                "介质、浸入深度和速度与钢材匹配。" => "The medium, immersion depth, and speed matched the steel.",
                "介质、深度或浸入速度不适合这类钢材。" => "The medium, depth, or immersion speed did not suit this steel.",
                "刃口以稳定、均匀的走刀完成打磨。" => "The edge was ground with steady, even passes.",
                "砂轮速度或刃口覆盖不够均匀。" => "Wheel speed or edge coverage was uneven.",
                "金属上留有冷锤、过热或急促加工造成的应变。" => "Cold hammering, overheating, or rushed work left strain in the metal.",
                _ => reason.Message
            }
        };
    }

    private void NotifyLocalizedText()
    {
        string[] properties =
        [
            nameof(StageTitle), nameof(HeatDescription), nameof(HammerFaceText), nameof(RecommendedHammerFaceText),
            nameof(RecipeDisplayName), nameof(MaterialDisplayName), nameof(ActiveZoneText), nameof(RecipeRouteText),
            nameof(CurrentOperationText), nameof(CraftFeedbackText), nameof(ResultTitle), nameof(ResultScoreText),
            nameof(RecipeTooltip), nameof(PauseTooltip), nameof(ExitWorkshopText), nameof(ForgeLedgerTitle), nameof(ChooseRecipeTitle),
            nameof(ChooseRecipeDescription), nameof(MaterialStockTitle), nameof(ChooseSteelTitle), nameof(ChooseSteelDescription),
            nameof(SoftSteelText), nameof(SoftSteelDescription), nameof(BalancedSteelText), nameof(BalancedSteelDescription),
            nameof(HighCarbonSteelText), nameof(HighCarbonSteelDescription), nameof(BellowsText), nameof(MoveToAnvilText),
            nameof(TurnOverText), nameof(ReturnToForgeText), nameof(WaterQuenchText), nameof(OilQuenchText),
            nameof(FinishQuenchingText), nameof(StartGrindingText), nameof(KeepQuenchedEdgeText),
            nameof(DecreaseSpeedTooltip), nameof(IncreaseSpeedTooltip), nameof(InspectText), nameof(SubmitInspectionText),
            nameof(CraftVerdictText), nameof(ForgeAgainText), nameof(SmithReferenceText), nameof(RecipeGuidanceText),
            nameof(CollapseText), nameof(ForgePausedTitle), nameof(ForgePausedDescription), nameof(ResumeForgingText),
            nameof(ApprenticeHintsText), nameof(AbandonForgeTitle), nameof(AbandonForgeDescription), nameof(AbandonAndReturnText)
        ];

        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }

    public async Task ReloadPlayerProfileAsync(CancellationToken cancellationToken = default)
    {
        recordedResult = null;
        profile = await progressStore.LoadAsync(cancellationToken);
        ApprenticeHints = profile.ApprenticeHints;
        Snapshot = engine.Execute(new RestartForgeCommand()).Snapshot;
        NotifyState();
        OnPropertyChanged(nameof(BestScore));
        UpdateTimerState();
    }

    private void UpdateTimerState()
    {
        if (!isActive || disposed || IsPaused || IsExitConfirmationOpen || !engine.RequiresContinuousTick)
        {
            timer.Stop();
            return;
        }

        if (!timer.IsEnabled)
        {
            lastTick = DateTimeOffset.UtcNow;
            timer.Start();
        }
    }

    private async Task RecordResultIfNeededAsync()
    {
        var result = Snapshot.Result;
        if (result is null || ReferenceEquals(recordedResult, result) || Snapshot.RecipeId is null || Snapshot.MaterialId is null)
        {
            return;
        }

        recordedResult = result;
        profile = await progressStore.RecordAsync(
            profile,
            new ForgeHistoryEntry(DateTimeOffset.UtcNow, Snapshot.RecipeId, Snapshot.MaterialId, result.Quality, result.Score));
        OnPropertyChanged(nameof(BestScore));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        timer.Tick -= OnTick;
        audio.Dispose();
    }
}

public sealed class ForgeRecipeChoiceViewModel : ObservableObject
{
    private bool useEnglish;

    public ForgeRecipeChoiceViewModel(ForgeRecipeDefinition recipe, Action select)
    {
        Recipe = recipe;
        SelectCommand = new RelayCommand(select);
    }

    public ForgeRecipeDefinition Recipe { get; }
    public string DisplayName => useEnglish ? Recipe.Name : Recipe.NameZh ?? Recipe.Name;
    public string RouteText => string.Join(" · ", Recipe.Zones
        .OrderBy(zone => zone.Order)
        .Where(zone => !zone.Id.Equals("correction", StringComparison.OrdinalIgnoreCase))
        .Select(zone => useEnglish ? zone.Label : zone.LabelZh ?? zone.Label));
    public Uri? ThumbnailUri => Recipe.Visual is null
        ? null
        : new Uri($"avares://BohemiX.Modules.Forge/Assets/Thumbnails/{Recipe.Visual.ThumbnailAsset}");
    public bool IsDuellingLongswordRecipe => Recipe.Id.Equals("duelling-longsword", StringComparison.OrdinalIgnoreCase);
    public bool IsBasilardRecipe => Recipe.Id.Equals("basilard", StringComparison.OrdinalIgnoreCase);
    public bool IsAxeRecipe => Recipe.Id.Equals("bearded-axe", StringComparison.OrdinalIgnoreCase);
    public IRelayCommand SelectCommand { get; }

    public void UseLanguage(bool english)
    {
        if (useEnglish == english)
        {
            return;
        }

        useEnglish = english;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(RouteText));
    }
}
