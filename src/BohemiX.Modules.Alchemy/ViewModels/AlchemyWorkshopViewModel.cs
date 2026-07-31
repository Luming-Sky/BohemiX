using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.Models;
using BohemiX.Modules.Alchemy.Services;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.Modules.Alchemy.ViewModels;

public enum IngredientDropTarget
{
    Cauldron,
    Mortar
}

public partial class AlchemyWorkshopViewModel : ObservableObject, IDisposable, IActivatableModule
{
    private static readonly IBrush ColdHeatBrush = new SolidColorBrush(0xFF79949A);
    private static readonly IBrush SimmeringHeatBrush = new SolidColorBrush(0xFFD8AA4D);
    private static readonly IBrush BoilingHeatBrush = new SolidColorBrush(0xFFE47836);
    private static readonly IBrush OverheatedHeatBrush = new SolidColorBrush(0xFFC43D2F);
    private static readonly IBrush FailedLiquidBrush = new SolidColorBrush(0xFF17130F);
    private static readonly IBrush WaterLiquidBrush = new SolidColorBrush(0xFF759DA2);
    private static readonly IBrush WineLiquidBrush = new SolidColorBrush(0xFF733C43);
    private static readonly IBrush SpiritsLiquidBrush = new SolidColorBrush(0xFFB4A77E);
    private static readonly IBrush OilLiquidBrush = new SolidColorBrush(0xFFB18B3E);
    private readonly AlchemyEngine engine;
    private readonly IAlchemyAudioService audio;
    private readonly DispatcherTimer timer;
    private DateTimeOffset lastTick;
    private bool isActive;
    private bool disposed;
    private bool useEnglish;
    private double nextGrindingSoundProgress = .12;

    public event EventHandler? ExitRequested;

    public AlchemyWorkshopViewModel(AlchemyEngine engine, IAlchemyAudioService audio)
    {
        this.engine = engine;
        this.audio = audio;
        Recipes = AlchemyCatalog.Recipes;
        Ingredients = new ObservableCollection<IngredientSlotViewModel>(
            AlchemyCatalog.Ingredients.Select((definition, index) => new IngredientSlotViewModel(definition, index, useEnglish)));
        BaseLiquids =
        [
            new BaseSlotViewModel(BaseLiquid.Water, "Water", 0xFF6F9FB0),
            new BaseSlotViewModel(BaseLiquid.Wine, "Wine", 0xFF7A3837),
            new BaseSlotViewModel(BaseLiquid.Spirits, "Spirits", 0xFFC7B28A),
            new BaseSlotViewModel(BaseLiquid.Oil, "Oil", 0xFFB79342)
        ];
        CountOptions = [1, 2, 3];
        selectedRecipe = Recipes[0];
        BuildRecipeRows();
        Refresh();

        lastTick = DateTimeOffset.UtcNow;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += TimerTick;
    }

    public void Activate()
    {
        if (disposed || isActive)
        {
            return;
        }

        isActive = true;
        var now = DateTimeOffset.UtcNow;
        engine.Tick(now - lastTick);
        lastTick = now;
        Refresh();
        SyncContinuousAudio(engine.Snapshot);
        UpdateTimerState();
    }

    public void Deactivate()
    {
        isActive = false;
        timer.Stop();
        audio.StopAll();
        lastTick = DateTimeOffset.UtcNow;
    }

    public IReadOnlyList<AlchemyRecipe> Recipes { get; }

    public AlchemySnapshot Snapshot => engine.Snapshot;

    public bool UseEnglish => useEnglish;

    public string ExitTooltipText => useEnglish ? "Exit alchemy" : "\u9000\u51fa\u70bc\u836f";
    public string ExitConfirmationTitle => useEnglish ? "Abandon current brew?" : "\u653e\u5f03\u5f53\u524d\u70bc\u836f\uff1f";
    public string ExitConfirmationMessage => useEnglish
        ? "The ingredients in the cauldron and this brew's progress will be discarded."
        : "\u5769\u57da\u4e2d\u7684\u6750\u6599\u548c\u672c\u6b21\u70bc\u5236\u8fdb\u5ea6\u5c06\u88ab\u4e22\u5f03\u3002";
    public string ContinueBrewingText => useEnglish ? "Continue brewing" : "\u7ee7\u7eed\u70bc\u836f";
    public string AbandonAndExitText => useEnglish ? "Abandon and exit" : "\u653e\u5f03\u5e76\u9000\u51fa";
    public string RecipeBookText => useEnglish ? "Recipe book" : "\u914d\u65b9\u4e66";
    public string RecipeBookSubtitle => useEnglish ? "Recipes & steps" : "\u914d\u65b9\u4e0e\u6b65\u9aa4";

    public ObservableCollection<IngredientSlotViewModel> Ingredients { get; }

    public IReadOnlyList<BaseSlotViewModel> BaseLiquids { get; }

    public IReadOnlyList<int> CountOptions { get; }

    public ObservableCollection<RecipeStepRowViewModel> RecipeRows { get; } = [];

    public ObservableCollection<string> RecentEvents { get; } = [];

    public void UseLanguage(bool value)
    {
        if (!SetProperty(ref useEnglish, value))
        {
            return;
        }

        OnPropertyChanged(nameof(ExitTooltipText));
        OnPropertyChanged(nameof(ExitConfirmationTitle));
        OnPropertyChanged(nameof(ExitConfirmationMessage));
        OnPropertyChanged(nameof(ContinueBrewingText));
        OnPropertyChanged(nameof(AbandonAndExitText));
        OnPropertyChanged(nameof(RecipeBookText));
        OnPropertyChanged(nameof(RecipeBookSubtitle));
        OnPropertyChanged(nameof(UseEnglish));
        foreach (var ingredient in Ingredients)
        {
            ingredient.UseLanguage(useEnglish);
        }

        BuildRecipeRows();
        Refresh();
    }

    internal string Text(AlchemyTextKey key) => AlchemyTextCatalog.Get(key, useEnglish);

    internal string RecipeName(AlchemyRecipe recipe) => AlchemyTextCatalog.Content(recipe.Name, useEnglish);

    internal string RecipeEffect(AlchemyRecipe recipe) => AlchemyTextCatalog.Content(recipe.Effect, useEnglish);

    internal string StepLabel(RecipeStep step) => AlchemyTextCatalog.Content(step.Label, useEnglish);

    internal string StepHint(RecipeStep step) => AlchemyTextCatalog.StepGestureHint(
        step,
        useEnglish,
        step.Kind == RecipeStepKind.Bottle
        && !SelectedRecipe.Steps.Any(item => item.Kind == RecipeStepKind.Distill));

    internal string BaseName(BaseLiquid liquid) => AlchemyTextCatalog.BaseName(liquid, useEnglish);

    [ObservableProperty]
    private AlchemyRecipe selectedRecipe;

    [ObservableProperty]
    private int selectedCount = 1;

    [ObservableProperty]
    private int selectedTurns = 1;

    [ObservableProperty]
    private bool isRecipeBookOpen;

    [ObservableProperty]
    private bool isExitConfirmationOpen;

    [ObservableProperty]
    private string currentStepText = string.Empty;

    [ObservableProperty]
    private string recipeProgressText = string.Empty;

    [ObservableProperty]
    private string lastMessage = string.Empty;

    [ObservableProperty]
    private string mistakeText = "Mistakes: none";

    [ObservableProperty]
    private string outcomeText = "Brewing";

    [ObservableProperty]
    private string yieldText = "Yield --";

    [ObservableProperty]
    private string temperatureText = "0°";

    [ObservableProperty]
    private string heatBandText = "Cold";

    [ObservableProperty]
    private double temperature;

    [ObservableProperty]
    private IBrush heatBrush = ColdHeatBrush;

    [ObservableProperty]
    private double hourglassProgress;

    [ObservableProperty]
    private string hourglassText = "Hourglass ready";

    [ObservableProperty]
    private string cauldronContentsText = "Empty cauldron";

    [ObservableProperty]
    private string mortarText = "Drag in herbs";

    [ObservableProperty]
    private string distillerText = "Still ready";

    [ObservableProperty]
    private bool isMortarLoaded;

    [ObservableProperty]
    private bool isCauldronLowered;

    [ObservableProperty]
    private bool isHourglassRunning;

    [ObservableProperty]
    private bool isBrewFailed;

    [ObservableProperty]
    private bool isBrewComplete;

    [ObservableProperty]
    private double fireOpacity = 0.18;

    [ObservableProperty]
    private double bubbleOpacity;

    [ObservableProperty]
    private double steamOpacity;

    [ObservableProperty]
    private double liquidOpacity;

    [ObservableProperty]
    private IBrush liquidBrush = new SolidColorBrush(0x007F8F8F);

    [ObservableProperty]
    private double grindingProgress;

    [ObservableProperty]
    private double pestleRotation = -18;

    [RelayCommand]
    private void ToggleRecipeBook() => IsRecipeBookOpen = !IsRecipeBookOpen;

    [RelayCommand]
    private void ToggleCauldron() => Run(new AlchemyCommand(AlchemyCommandKind.ToggleCauldron));

    [RelayCommand]
    private void PumpBellows() => Run(new AlchemyCommand(AlchemyCommandKind.PumpBellows));

    [RelayCommand]
    private void StartHourglass() => Run(new AlchemyCommand(AlchemyCommandKind.StartHourglass, Turns: SelectedTurns));

    [RelayCommand]
    private void Distill() => Run(new AlchemyCommand(AlchemyCommandKind.Distill));

    [RelayCommand]
    private void Bottle() => Run(new AlchemyCommand(AlchemyCommandKind.Bottle));

    [RelayCommand]
    private void Reset()
    {
        engine.Reset();
        GrindingProgress = 0;
        nextGrindingSoundProgress = .12;
        audio.StopAll();
        Refresh();
        SyncContinuousAudio(engine.Snapshot);
        UpdateTimerState();
    }

    public bool HasActiveBrew => IsActiveBrew(Snapshot);

    internal static bool IsActiveBrew(AlchemySnapshot snapshot) =>
        snapshot.Outcome == BrewOutcome.Brewing
        && (snapshot.Base is not null
            || snapshot.Cauldron.Count > 0
            || snapshot.Mortar is not null
            || snapshot.CurrentStepIndex > 0
            || snapshot.CurrentStepProgress > 0
            || snapshot.HourglassRunning
            || snapshot.Temperature > 0.5);

    [RelayCommand]
    private void RequestExit()
    {
        PlayUiInteraction(AlchemySoundCue.UiClick, .36);
        if (HasActiveBrew)
        {
            IsExitConfirmationOpen = true;
            return;
        }

        ExitWorkshop(resetSession: IsBrewComplete || IsBrewFailed);
    }

    [RelayCommand]
    private void CancelExit()
    {
        PlayUiInteraction(AlchemySoundCue.UiClick, .34);
        IsExitConfirmationOpen = false;
    }

    [RelayCommand]
    private void ConfirmExit()
    {
        PlayUiInteraction(AlchemySoundCue.UiClick, .4);
        ExitWorkshop(resetSession: true);
    }

    private void ExitWorkshop(bool resetSession)
    {
        if (resetSession)
        {
            ResetFromGesture();
        }

        IsRecipeBookOpen = false;
        IsExitConfirmationOpen = false;
        timer.Stop();
        audio.StopAll();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsExitConfirmationOpenChanged(bool value)
    {
        lastTick = DateTimeOffset.UtcNow;
        SyncContinuousAudio(engine.Snapshot);
        UpdateTimerState();
    }

    public void PourBase(BaseSlotViewModel baseSlot) =>
        Run(new AlchemyCommand(AlchemyCommandKind.PourBase, Base: baseSlot.Value));

    public void SetPouringSound(bool isPouring, double flowRate = 0)
    {
        if (!isActive || disposed || !isPouring)
        {
            audio.StopLoop(AlchemySoundCue.Pour);
            return;
        }

        audio.StartLoop(AlchemySoundCue.Pour, Math.Clamp(.38 + (flowRate * .62), .38, 1));
    }

    public void BlowBellows(double strength) =>
        Run(new AlchemyCommand(AlchemyCommandKind.PumpBellows, Strength: Math.Clamp(strength, 0.15, 1)));

    public void PlayBellowsSound(double strength) =>
        audio.Play(AlchemySoundCue.Bellows, Math.Clamp(strength, 0.35, 1));

    internal void PlayUiInteraction(AlchemySoundCue cue = AlchemySoundCue.UiClick, double intensity = .34) =>
        audio.Play(cue, Math.Clamp(intensity, .12, .65));

    public void ToggleCauldronFromGesture() =>
        Run(new AlchemyCommand(AlchemyCommandKind.ToggleCauldron));

    public void FlipHourglassFromGesture() =>
        Run(new AlchemyCommand(AlchemyCommandKind.StartHourglass, Turns: SelectedTurns));

    public void DistillFromGesture() =>
        Run(new AlchemyCommand(AlchemyCommandKind.Distill));

    public void BottleFromGesture() =>
        Run(new AlchemyCommand(AlchemyCommandKind.Bottle));

    public void CycleRecipeFromGesture(int direction)
    {
        var current = 0;
        for (var index = 0; index < Recipes.Count; index++)
        {
            if (ReferenceEquals(Recipes[index], SelectedRecipe) || Recipes[index].Id == SelectedRecipe.Id)
            {
                current = index;
                break;
            }
        }

        var next = (current + Math.Sign(direction) + Recipes.Count) % Recipes.Count;
        SelectedRecipe = Recipes[next];
    }

    public void ResetFromGesture()
    {
        engine.Reset();
        GrindingProgress = 0;
        PestleRotation = -18;
        nextGrindingSoundProgress = .12;
        audio.StopAll();
        Refresh();
        SyncContinuousAudio(engine.Snapshot);
        UpdateTimerState();
    }

    public bool DropIngredient(IngredientSlotViewModel ingredient, IngredientDropTarget target)
    {
        var kind = target == IngredientDropTarget.Mortar
            ? AlchemyCommandKind.LoadMortar
            : AlchemyCommandKind.AddRawIngredient;
        var result = Run(new AlchemyCommand(kind, IngredientId: ingredient.Id, Count: SelectedCount));
        if (result.Accepted && target == IngredientDropTarget.Mortar && IsMortarLoaded)
        {
            GrindingProgress = 0;
            nextGrindingSoundProgress = .12;
        }

        return result.Accepted;
    }

    public void UpdateGrindingGesture(double progress, double rotation)
    {
        if (!IsMortarLoaded)
        {
            return;
        }

        GrindingProgress = Math.Clamp(progress, 0, 1);
        PestleRotation = Math.Clamp(rotation, -32, 28);
        if (GrindingProgress >= nextGrindingSoundProgress)
        {
            audio.Play(AlchemySoundCue.Grind, .3 + (GrindingProgress * .45));
            nextGrindingSoundProgress = Math.Min(1.01, GrindingProgress + .16);
        }
    }

    public bool CompleteGrindingIntoCauldronGesture()
    {
        if (!IsMortarLoaded)
        {
            return false;
        }

        var result = Run(new AlchemyCommand(AlchemyCommandKind.TransferGroundMortar));
        if (result.Accepted)
        {
            GrindingProgress = 0;
            PestleRotation = -18;
            nextGrindingSoundProgress = .12;
        }

        return result.Accepted;
    }

    partial void OnSelectedRecipeChanged(AlchemyRecipe value)
    {
        if (engine is null)
        {
            return;
        }

        engine.SelectRecipe(value);
        GrindingProgress = 0;
        nextGrindingSoundProgress = .12;
        audio.StopAll();
        BuildRecipeRows();
        Refresh();
        SyncContinuousAudio(engine.Snapshot);
        UpdateTimerState();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        timer.Tick -= TimerTick;
        audio.StopAll();
    }

    private void TimerTick(object? sender, EventArgs e)
    {
        var before = engine.Snapshot;
        var now = DateTimeOffset.UtcNow;
        engine.Tick(now - lastTick);
        lastTick = now;
        var after = engine.Snapshot;
        Refresh();
        PlayTimedStateCues(before, after);
        SyncContinuousAudio(after);
        UpdateTimerState();
    }

    private EngineMessage Run(AlchemyCommand command)
    {
        var before = engine.Snapshot;
        var result = engine.Execute(command);
        var after = engine.Snapshot;
        Refresh();
        if (result.Accepted)
        {
            PlayCommandCue(command, before, after);
        }
        SyncContinuousAudio(after);
        UpdateTimerState();
        return result;
    }

    private void PlayCommandCue(AlchemyCommand command, AlchemySnapshot before, AlchemySnapshot after)
    {
        AlchemySoundCue? cue = command.Kind switch
        {
            AlchemyCommandKind.PourBase => null,
            AlchemyCommandKind.LoadMortar => AlchemySoundCue.HerbToMortar,
            AlchemyCommandKind.TransferGroundMortar => AlchemySoundCue.PowderPour,
            AlchemyCommandKind.AddRawIngredient => AlchemySoundCue.HerbDrop,
            AlchemyCommandKind.ToggleCauldron => after.CauldronLowered ? AlchemySoundCue.CauldronLower : AlchemySoundCue.CauldronLift,
            AlchemyCommandKind.PumpBellows => null,
            AlchemyCommandKind.StartHourglass => AlchemySoundCue.HourglassStart,
            AlchemyCommandKind.Distill => AlchemySoundCue.Distill,
            AlchemyCommandKind.Bottle => AlchemySoundCue.Bottle,
            _ => null
        };
        var intensity = command.Kind == AlchemyCommandKind.PumpBellows
            ? Math.Clamp(command.Strength, .25, 1)
            : 1;
        if (cue is { } oneShotCue)
        {
            audio.Play(oneShotCue, intensity);
        }

        if (command.Kind is AlchemyCommandKind.TransferGroundMortar or AlchemyCommandKind.AddRawIngredient)
        {
            audio.Play(AlchemySoundCue.CauldronSplash,
                command.Kind == AlchemyCommandKind.AddRawIngredient ? .78 : .62);
        }

        if (after.Mistakes > before.Mistakes)
        {
            audio.Play(AlchemySoundCue.Mistake, .55);
        }

        if (after.Outcome != before.Outcome && after.Outcome != BrewOutcome.Brewing)
        {
            audio.Play(after.Outcome == BrewOutcome.Perfect ? AlchemySoundCue.BrewComplete : AlchemySoundCue.BrewDiluted,
                after.Outcome == BrewOutcome.Perfect ? 1 : .72);
        }
    }

    private void PlayTimedStateCues(AlchemySnapshot before, AlchemySnapshot after)
    {
        if (before.HourglassRunning && !after.HourglassRunning)
        {
            audio.StopLoop(AlchemySoundCue.HourglassSand);
            audio.Play(AlchemySoundCue.HourglassComplete, .8);
        }

        if (after.Mistakes > before.Mistakes)
        {
            audio.Play(AlchemySoundCue.Mistake, .55);
        }
    }

    private void SyncContinuousAudio(AlchemySnapshot snapshot)
    {
        if (!isActive || disposed)
        {
            return;
        }

        var fireIntensity = .34
            + (snapshot.Temperature / 100 * .38)
            + (snapshot.CauldronLowered ? .12 : 0);
        audio.StartLoop(AlchemySoundCue.Fire, Math.Clamp(fireIntensity, .3, .84));

        if (snapshot.HourglassRunning && !IsExitConfirmationOpen)
        {
            audio.StartLoop(AlchemySoundCue.HourglassSand, .62);
        }
        else
        {
            audio.StopLoop(AlchemySoundCue.HourglassSand);
        }
    }

    private void UpdateTimerState()
    {
        if (!isActive || disposed || IsExitConfirmationOpen || !engine.RequiresContinuousTick)
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

    private void BuildRecipeRows()
    {
        RecipeRows.Clear();
        for (var i = 0; i < SelectedRecipe.Steps.Count; i++)
        {
            RecipeRows.Add(new RecipeStepRowViewModel(i + 1, StepLabel(SelectedRecipe.Steps[i]), useEnglish));
        }
    }

    private void Refresh()
    {
        var snapshot = engine.Snapshot;
        Temperature = snapshot.Temperature;
        TemperatureText = $"{snapshot.Temperature:0}°";
        HeatBandText = FormatHeat(snapshot.HeatBand);
        HeatBrush = HeatBrushFor(snapshot.HeatBand);
        HourglassProgress = snapshot.HourglassProgress;
        IsHourglassRunning = snapshot.HourglassRunning;
        HourglassText = snapshot.HourglassRunning
            ? $"{Text(AlchemyTextKey.HourglassRunning)} · {snapshot.HourglassProgress:P0}"
            : Text(AlchemyTextKey.HourglassReady);
        IsCauldronLowered = snapshot.CauldronLowered;
        IsMortarLoaded = snapshot.Mortar is not null;
        IsBrewFailed = snapshot.Outcome == BrewOutcome.Failed;
        IsBrewComplete = snapshot.Outcome is BrewOutcome.Perfect or BrewOutcome.Diluted;
        LastMessage = AlchemyTextCatalog.EngineMessage(snapshot.LastMessage, useEnglish);
        MistakeText = snapshot.Mistakes == 0
            ? Text(AlchemyTextKey.MistakesNone)
            : $"{Text(AlchemyTextKey.Mistakes)}：{snapshot.Mistakes}";
        OutcomeText = FormatOutcome(snapshot.Outcome, snapshot.Mistakes);
        YieldText = snapshot.Outcome == BrewOutcome.Brewing
            ? $"{Text(AlchemyTextKey.Yield)} --"
            : useEnglish
                ? $"{Text(AlchemyTextKey.Yield)} {snapshot.Yield} {Text(AlchemyTextKey.Bottles)}"
                : $"{Text(AlchemyTextKey.Yield)} {snapshot.Yield} {Text(AlchemyTextKey.Bottles)}";

        CurrentStepText = snapshot.CurrentStepIndex < snapshot.Recipe.Steps.Count
            ? FormatCurrentStep(snapshot.Recipe.Steps[snapshot.CurrentStepIndex], snapshot.CurrentStepProgress)
            : Text(AlchemyTextKey.RecipeComplete);
        RecipeProgressText = snapshot.CurrentStepIndex < snapshot.Recipe.Steps.Count
            ? $"{snapshot.CurrentStepIndex + 1} / {snapshot.Recipe.Steps.Count}"
            : $"{snapshot.Recipe.Steps.Count} / {snapshot.Recipe.Steps.Count}";

        for (var i = 0; i < RecipeRows.Count; i++)
        {
            RecipeRows[i].SetState(i < snapshot.CurrentStepIndex, i == snapshot.CurrentStepIndex, useEnglish);
        }

        foreach (var slot in Ingredients)
        {
            slot.Quantity = snapshot.Inventory.GetValueOrDefault(slot.Id);
        }

        MortarText = snapshot.Mortar is null
            ? Text(AlchemyTextKey.DragInHerbs)
            : $"{IngredientName(snapshot.Mortar.IngredientId)} x{snapshot.Mortar.Count}";
        CauldronContentsText = BuildCauldronText(snapshot);
        DistillerText = snapshot.Distilled ? Text(AlchemyTextKey.CondensateReady) : Text(AlchemyTextKey.StillReady);

        LiquidOpacity = snapshot.Base is null ? 0 : 0.78;
        LiquidBrush = LiquidBrushFor(snapshot.Base, snapshot.Outcome);
        FireOpacity = snapshot.CauldronLowered
            ? Math.Clamp(0.72 + (snapshot.Temperature / 350), 0.72, 1)
            : 0.34;
        BubbleOpacity = snapshot.HeatBand switch
        {
            HeatBand.Simmering => 0.35,
            HeatBand.Boiling => 0.9,
            HeatBand.Overheated => 1,
            _ => 0
        };
        SteamOpacity = snapshot.HeatBand is HeatBand.Boiling or HeatBand.Overheated ? 0.8 : 0.12;

        RecentEvents.Clear();
        foreach (var entry in snapshot.EventLog.TakeLast(4).Reverse())
        {
            RecentEvents.Add(AlchemyTextCatalog.EngineMessage(entry, useEnglish));
        }
    }

    private string BuildCauldronText(AlchemySnapshot snapshot)
    {
        if (snapshot.Base is null)
        {
            return Text(AlchemyTextKey.EmptyCauldron);
        }

        var ingredients = snapshot.Cauldron
            .Select(item => useEnglish
                ? $"{(item.Form == IngredientForm.Ground ? $"{Text(AlchemyTextKey.Ground)} " : string.Empty)}{IngredientName(item.IngredientId)} x{item.Count}"
                : $"{IngredientName(item.IngredientId)}{(item.Form == IngredientForm.Ground ? "粉" : string.Empty)} x{item.Count}")
            .ToArray();
        return ingredients.Length == 0
            ? FormatBase(snapshot.Base.Value)
            : $"{FormatBase(snapshot.Base.Value)} · {string.Join(" · ", ingredients)}";
    }

    private string FormatCurrentStep(RecipeStep step, int progress)
    {
        var required = step.Kind == RecipeStepKind.Cook ? step.Turns : step.Count;
        var label = StepLabel(step);
        return required > 1 && progress > 0
            ? $"{label}（{progress}/{required}）"
            : label;
    }

    private string IngredientName(string id)
    {
        var source = AlchemyCatalog.Ingredients.FirstOrDefault(item => item.Id == id)?.Name ?? id;
        return AlchemyTextCatalog.Content(source, useEnglish);
    }

    private string FormatBase(BaseLiquid liquid) => AlchemyTextCatalog.BaseName(liquid, useEnglish);

    private string FormatHeat(HeatBand band) => AlchemyTextCatalog.HeatName(band, useEnglish);

    private string FormatOutcome(BrewOutcome outcome, int mistakes) =>
        AlchemyTextCatalog.Outcome(outcome, mistakes, useEnglish);

    private static IBrush HeatBrushFor(HeatBand band) => band switch
    {
        HeatBand.Cold => ColdHeatBrush,
        HeatBand.Simmering => SimmeringHeatBrush,
        HeatBand.Boiling => BoilingHeatBrush,
        HeatBand.Overheated => OverheatedHeatBrush,
        _ => Brushes.Gray
    };

    private static IBrush LiquidBrushFor(BaseLiquid? liquid, BrewOutcome outcome)
    {
        if (outcome == BrewOutcome.Failed)
        {
            return FailedLiquidBrush;
        }

        return liquid switch
        {
            BaseLiquid.Water => WaterLiquidBrush,
            BaseLiquid.Wine => WineLiquidBrush,
            BaseLiquid.Spirits => SpiritsLiquidBrush,
            BaseLiquid.Oil => OilLiquidBrush,
            _ => Brushes.Transparent
        };
    }
}

public partial class IngredientSlotViewModel : ObservableObject
{
    private readonly IngredientDefinition definition;

    public IngredientSlotViewModel(IngredientDefinition definition, int index, bool useEnglish = false)
    {
        this.definition = definition;
        Id = definition.Id;
        name = AlchemyTextCatalog.Content(definition.Name, useEnglish);
        description = AlchemyTextCatalog.Content(definition.Description, useEnglish);
        IconData = StreamGeometry.Parse(definition.Geometry);
        AccentBrush = new SolidColorBrush(definition.Color);
        Index = index;
        quantity = definition.StartingQuantity;
    }

    public string Id { get; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string description;

    public StreamGeometry IconData { get; }
    public IBrush AccentBrush { get; }
    public int Index { get; }

    [ObservableProperty]
    private int quantity;

    public void UseLanguage(bool useEnglish)
    {
        Name = AlchemyTextCatalog.Content(definition.Name, useEnglish);
        Description = AlchemyTextCatalog.Content(definition.Description, useEnglish);
    }
}

public sealed record BaseSlotViewModel(BaseLiquid Value, string Name, uint Color)
{
    public IBrush AccentBrush { get; } = new SolidColorBrush(Color);
}

public partial class RecipeStepRowViewModel(int number, string text, bool useEnglish = false) : ObservableObject
{
    public int Number { get; } = number;
    public string Text { get; } = text;

    [ObservableProperty]
    private string stateText = AlchemyTextCatalog.Get(AlchemyTextKey.Pending, useEnglish);

    [ObservableProperty]
    private IBrush stateBrush = new SolidColorBrush(0xFF8A765F);

    [ObservableProperty]
    private double rowOpacity = 0.68;

    public void SetState(bool completed, bool current, bool useEnglish = false)
    {
        StateText = completed
            ? AlchemyTextCatalog.Get(AlchemyTextKey.Complete, useEnglish)
            : current
                ? AlchemyTextCatalog.Get(AlchemyTextKey.Current, useEnglish)
                : AlchemyTextCatalog.Get(AlchemyTextKey.Pending, useEnglish);
        StateBrush = completed
            ? new SolidColorBrush(0xFF5F8058)
            : current
                ? new SolidColorBrush(0xFFB56B32)
                : new SolidColorBrush(0xFF8A765F);
        RowOpacity = completed ? 0.58 : current ? 1 : 0.68;
    }
}
