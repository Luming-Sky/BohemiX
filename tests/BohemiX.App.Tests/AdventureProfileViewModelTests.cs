using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class AdventureProfileViewModelTests
{
    [Fact]
    public void EmptyProfile_ProvidesSixSpoilerFreeCoreStatisticsWithoutSampleData()
    {
        var profile = AdventureProfileViewModel.CreateEmpty(useEnglish: true);

        Assert.Equal(string.Empty, profile.PlayerName);
        Assert.Equal(string.Empty, profile.Platform);
        Assert.Equal("--", profile.Difficulty);
        Assert.Equal("Adventure Profile", profile.Title);
        Assert.Equal(6, profile.Statistics.Count);
        Assert.Equal(
            [
                AdventureStatisticKeys.InGameDays,
                AdventureStatisticKeys.MainStory,
                AdventureStatisticKeys.Achievements,
                AdventureStatisticKeys.Exploration,
                AdventureStatisticKeys.SaveSlots,
                AdventureStatisticKeys.InstalledMods,
            ],
            profile.Statistics.Select(item => item.Key));
        Assert.Equal("--", profile.Statistics[1].Value);
        Assert.DoesNotContain(profile.Statistics, item => item.Title.Contains("Quest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegisterStatistic_AllowsFutureCategoriesWithoutChangingTheControl()
    {
        var profile = AdventureProfileViewModel.CreateDefault(useEnglish: true);
        var combat = new AdventureStatisticDefinition("combat", "Combat", "战斗统计", 6);

        profile.RegisterStatistic(combat, "412");

        Assert.Equal(7, profile.Statistics.Count);
        Assert.Equal("combat", profile.Statistics[^1].Key);
        Assert.Equal("412", profile.Statistics[^1].Value);

        profile.RegisterStatistic(combat, "413");

        Assert.Equal(7, profile.Statistics.Count);
        Assert.Equal("413", profile.Statistics[^1].Value);
    }

    [Fact]
    public void RuntimeData_UpdatesLiveLauncherStatistics()
    {
        var profile = AdventureProfileViewModel.CreateDefault(useEnglish: false);

        profile.UpdateRuntimeData("GOG Edition", 207, 31, 9);

        Assert.Equal("GOG Edition", profile.Platform);
        Assert.Equal("207 小时", profile.HoursText);
        Assert.Equal("31", profile.Statistics.Single(item => item.Key == AdventureStatisticKeys.SaveSlots).Value);
        Assert.Equal("9", profile.Statistics.Single(item => item.Key == AdventureStatisticKeys.InstalledMods).Value);
    }

    [Fact]
    public void UseLanguage_RelabelsHeaderAndStatistics()
    {
        var profile = AdventureProfileViewModel.CreateDefault(useEnglish: false);

        profile.UseLanguage(useEnglish: true);

        Assert.Equal("Adventure Profile", profile.Title);
        Assert.Equal("Platform", profile.PlatformLabel);
        Assert.Equal("In-Game Days", profile.Statistics[0].Title);
        Assert.Equal("0 Hours", profile.HoursText);
    }

    [Fact]
    public void PlayerStatistics_ApplyStoredValuesToCoreCards()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = AdventureProfileViewModel.CreateEmpty(useEnglish: true);

        profile.ApplyPlayerStatistics(
        [
            new PlayerStatistic(PlayerStatisticKeys.Difficulty, 0, null, "Hardcore", now),
            new PlayerStatistic(PlayerStatisticKeys.HoursPlayed, 183, null, null, now),
            new PlayerStatistic(PlayerStatisticKeys.DaysPlayed, 96, null, null, now),
            new PlayerStatistic(PlayerStatisticKeys.MainStory, 18, 32, null, now),
            new PlayerStatistic(PlayerStatisticKeys.Achievements, 67, 96, null, now),
            new PlayerStatistic(PlayerStatisticKeys.Exploration, 128, 186, null, now),
        ]);

        Assert.Equal("Hardcore", profile.Difficulty);
        Assert.Equal("183 Hours", profile.HoursText);
        Assert.Equal("--", profile.Statistics[0].Value);
        Assert.Equal("18 / 32", profile.Statistics[1].Value);
        Assert.Equal("67 / 96", profile.Statistics[2].Value);
        Assert.Equal("128 / 186", profile.Statistics[3].Value);
    }

    [Fact]
    public async Task SaveOptions_OpenCommandOpensTheRequestedSave()
    {
        var profile = AdventureProfileViewModel.CreateEmpty(useEnglish: true);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        Guid? requestedId = null;

        profile.UpdateSaveOptions(
        [
            new AdventureSaveOption(firstId, "Henry I"),
            new AdventureSaveOption(secondId, "Henry II"),
        ],
        secondId,
        id =>
        {
            requestedId = id;
            return Task.CompletedTask;
        });

        Assert.True(profile.HasSaveOptions);
        Assert.False(profile.SaveOptions[0].IsSelected);
        Assert.True(profile.SaveOptions[1].IsSelected);

        await profile.SaveOptions[0].OpenCommand.ExecuteAsync(null);

        Assert.Equal(firstId, requestedId);
    }
}
