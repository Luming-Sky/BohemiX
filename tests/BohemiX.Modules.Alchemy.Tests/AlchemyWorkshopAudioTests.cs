using Avalonia.Headless.XUnit;
using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.Models;
using BohemiX.Modules.Alchemy.Services;
using BohemiX.Modules.Alchemy.ViewModels;
using NAudio.Vorbis;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class AlchemyWorkshopAudioTests
{
    [Fact]
    public void SampledAudioAssets_AreCopiedToRuntimeOutput()
    {
        var audioDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Alchemy", "Audio");
        string[] expectedFiles =
        [
            "pour_water.ogg",
            "water_drop.ogg",
            "mortar_grind.ogg",
            "bellows.ogg",
            "herb_to_mortar.ogg",
            "powder_pour.ogg",
            "herb_drop.ogg",
            "cauldron_lower.ogg",
            "cauldron_lift.ogg",
            "hourglass_flip.ogg",
            "hourglass_sand.ogg",
            "fire_loop.ogg",
            "glass_settle.ogg",
            "brew_complete.ogg",
            "brew_diluted.ogg",
            "ui_click.ogg",
            "book_open.ogg",
            "book_close.ogg",
            "book_page.ogg"
        ];

        foreach (var fileName in expectedFiles)
        {
            var path = Path.Combine(audioDirectory, fileName);
            Assert.True(File.Exists(path), $"Missing runtime audio asset: {fileName}");
            using var reader = new VorbisWaveReader(path);
            Assert.True(reader.TotalTime > TimeSpan.Zero, $"Empty runtime audio asset: {fileName}");
            var buffer = new byte[Math.Max(reader.WaveFormat.AverageBytesPerSecond / 20, reader.WaveFormat.BlockAlign)];
            Assert.True(reader.Read(buffer, 0, buffer.Length) > 0, $"Unreadable runtime audio asset: {fileName}");
        }
    }

    [AvaloniaFact]
    public void AcceptedWorkshopActions_PlayTheirMatchingCues()
    {
        var audio = new RecordingAudioService();
        using var viewModel = CreateViewModel(audio);

        viewModel.Activate();
        viewModel.SetPouringSound(true, .8);
        viewModel.SetPouringSound(false);
        viewModel.PourBase(Wine(viewModel));
        viewModel.DropIngredient(Belladonna(viewModel), IngredientDropTarget.Mortar);
        viewModel.UpdateGrindingGesture(.2, 0);
        Assert.True(viewModel.CompleteGrindingIntoCauldronGesture());
        viewModel.ToggleCauldronFromGesture();
        viewModel.PlayBellowsSound(.8);
        viewModel.BlowBellows(.8);
        viewModel.FlipHourglassFromGesture();
        viewModel.DistillFromGesture();
        viewModel.BottleFromGesture();

        Assert.Contains(AlchemySoundCue.Fire, audio.StartedLoops);
        Assert.Contains(AlchemySoundCue.Pour, audio.StartedLoops);
        Assert.Contains(AlchemySoundCue.Pour, audio.StoppedLoops);
        Assert.Contains(AlchemySoundCue.HourglassSand, audio.StartedLoops);
        Assert.Contains(AlchemySoundCue.HerbToMortar, audio.Cues);
        Assert.Contains(AlchemySoundCue.Grind, audio.Cues);
        Assert.Contains(AlchemySoundCue.PowderPour, audio.Cues);
        Assert.Contains(AlchemySoundCue.CauldronSplash, audio.Cues);
        Assert.Contains(AlchemySoundCue.CauldronLower, audio.Cues);
        Assert.Contains(AlchemySoundCue.Bellows, audio.Cues);
        Assert.Contains(AlchemySoundCue.HourglassStart, audio.Cues);
        Assert.Contains(AlchemySoundCue.Distill, audio.Cues);
        Assert.Contains(AlchemySoundCue.Bottle, audio.Cues);
        Assert.Contains(AlchemySoundCue.BrewDiluted, audio.Cues);
    }

    [AvaloniaFact]
    public void RejectedAction_DoesNotPlayAnInteractionCue()
    {
        var audio = new RecordingAudioService();
        using var viewModel = CreateViewModel(audio);

        viewModel.DistillFromGesture();

        Assert.Empty(audio.Cues);
    }

    [AvaloniaFact]
    public void RawHerbDroppedIntoCauldron_PlaysRustleAndSplash()
    {
        var audio = new RecordingAudioService();
        using var viewModel = CreateViewModel(audio);
        viewModel.PourBase(Wine(viewModel));
        audio.Cues.Clear();

        Assert.True(viewModel.DropIngredient(Belladonna(viewModel), IngredientDropTarget.Cauldron));

        Assert.Contains(AlchemySoundCue.HerbDrop, audio.Cues);
        Assert.Contains(AlchemySoundCue.CauldronSplash, audio.Cues);
    }

    [AvaloniaFact]
    public void RecipeDeviation_PlaysMistakeCueAlongsideAcceptedAction()
    {
        var audio = new RecordingAudioService();
        using var viewModel = CreateViewModel(audio);

        viewModel.PourBase(viewModel.BaseLiquids.Single(liquid => liquid.Value == BaseLiquid.Water));

        Assert.Equal([AlchemySoundCue.Mistake], audio.Cues);
    }

    [AvaloniaFact]
    public void GrindingGesture_IsThrottledByProgress()
    {
        var audio = new RecordingAudioService();
        using var viewModel = CreateViewModel(audio);
        viewModel.DropIngredient(Belladonna(viewModel), IngredientDropTarget.Mortar);
        audio.Cues.Clear();

        viewModel.UpdateGrindingGesture(.11, 0);
        viewModel.UpdateGrindingGesture(.12, 0);
        viewModel.UpdateGrindingGesture(.20, 0);

        Assert.Equal([AlchemySoundCue.Grind], audio.Cues);
    }

    [AvaloniaFact]
    public void UiInteraction_PlaysExactlyOneRequestedCue()
    {
        var audio = new RecordingAudioService();
        using var viewModel = CreateViewModel(audio);

        viewModel.PlayUiInteraction(AlchemySoundCue.BookPage, .4);

        Assert.Equal([AlchemySoundCue.BookPage], audio.Cues);
    }

    private static AlchemyWorkshopViewModel CreateViewModel(RecordingAudioService audio) =>
        new(new AlchemyEngine(), audio);

    private static BaseSlotViewModel Wine(AlchemyWorkshopViewModel viewModel) =>
        viewModel.BaseLiquids.Single(liquid => liquid.Value == BaseLiquid.Wine);

    private static IngredientSlotViewModel Belladonna(AlchemyWorkshopViewModel viewModel) =>
        viewModel.Ingredients.Single(ingredient => ingredient.Id == "belladonna");

    private sealed class RecordingAudioService : IAlchemyAudioService
    {
        public List<AlchemySoundCue> Cues { get; } = [];
        public List<AlchemySoundCue> StartedLoops { get; } = [];
        public List<AlchemySoundCue> StoppedLoops { get; } = [];

        public void Play(AlchemySoundCue cue, double intensity = 1) => Cues.Add(cue);
        public void StartLoop(AlchemySoundCue cue, double intensity = 1) => StartedLoops.Add(cue);
        public void StopLoop(AlchemySoundCue cue) => StoppedLoops.Add(cue);
        public void StopAll() { }
        public void Dispose() { }
    }
}
