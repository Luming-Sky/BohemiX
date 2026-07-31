using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Services;
using BohemiX.Modules.Forge.ViewModels;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeWorkshopExitTests
{
    [Fact]
    public void WorkshopText_DefaultsToChinese_AndCanSwitchToEnglish()
    {
        using var viewModel = CreateViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal("选择配方", viewModel.StageTitle);
        Assert.Equal("金属尚未发光", viewModel.HeatDescription);
        Assert.Equal("锻造账册", viewModel.ForgeLedgerTitle);
        Assert.Equal("选择锻造配方", viewModel.ChooseRecipeTitle);
        Assert.DoesNotContain(" / ", viewModel.RecipeChoices[0].DisplayName);

        viewModel.UseLanguage(english: true);

        Assert.Equal("Choose recipe", viewModel.StageTitle);
        Assert.Equal("Metal is not glowing", viewModel.HeatDescription);
        Assert.Equal("FORGE LEDGER", viewModel.ForgeLedgerTitle);
        Assert.Equal("Choose a forging recipe", viewModel.ChooseRecipeTitle);
        Assert.Equal(viewModel.RecipeChoices[0].Recipe.Name, viewModel.RecipeChoices[0].DisplayName);
        Assert.Contains(nameof(ForgeWorkshopViewModel.StageTitle), changed);
        Assert.Contains(nameof(ForgeWorkshopViewModel.CraftFeedbackText), changed);
    }

    [Fact]
    public void IdleWorkshop_ExitsWithoutConfirmation()
    {
        using var viewModel = CreateViewModel();
        var exitCount = 0;
        viewModel.ExitRequested += (_, _) => exitCount++;

        viewModel.RequestExitCommand.Execute(null);

        Assert.Equal(1, exitCount);
        Assert.False(viewModel.IsExitConfirmationOpen);
        Assert.Equal(ForgeStateId.RecipeSelect, viewModel.Snapshot.State);
    }

    [Fact]
    public void ActiveCraft_CanCancelOrReturnToForgeStart()
    {
        using var viewModel = CreateViewModel();
        var exitCount = 0;
        viewModel.ExitRequested += (_, _) => exitCount++;
        viewModel.RecipeChoices[0].SelectCommand.Execute(null);
        Assert.Equal(ForgeStateId.MaterialSelect, viewModel.Snapshot.State);

        viewModel.RequestExitCommand.Execute(null);
        Assert.True(viewModel.IsExitConfirmationOpen);
        Assert.Equal(0, exitCount);

        viewModel.CancelExitCommand.Execute(null);
        Assert.False(viewModel.IsExitConfirmationOpen);
        Assert.Equal(ForgeStateId.MaterialSelect, viewModel.Snapshot.State);

        viewModel.RequestExitCommand.Execute(null);
        viewModel.ConfirmExitCommand.Execute(null);

        Assert.Equal(0, exitCount);
        Assert.False(viewModel.IsExitConfirmationOpen);
        Assert.Equal(ForgeStateId.RecipeSelect, viewModel.Snapshot.State);
        Assert.Null(viewModel.Snapshot.RecipeId);
    }

    private static ForgeWorkshopViewModel CreateViewModel()
    {
        var catalog = new ForgeCatalog();
        return new ForgeWorkshopViewModel(
            new ForgeEngine(catalog),
            catalog,
            new SilentAudioService(),
            new MemoryProgressStore());
    }

    private sealed class SilentAudioService : IForgeAudioService
    {
        public void Play(ForgeSoundCue cue, double intensity = 1) { }
        public void StopAll() { }
        public void Dispose() { }
    }

    private sealed class MemoryProgressStore : IForgeProgressStore
    {
        private static readonly ForgeProfile Empty = new(1, [], new Dictionary<string, int>());

        public Task<ForgeProfile> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Empty);

        public Task<ForgeProfile> RecordAsync(
            ForgeProfile current,
            ForgeHistoryEntry entry,
            CancellationToken cancellationToken = default) => Task.FromResult(current);
    }
}
