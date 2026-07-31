using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class ModConflictAnalyzerTests
{
    [Fact]
    public void AnalyzeConflicts_ReturnsSharedVirtualPathsAcrossMods()
    {
        var analyzer = new ModConflictAnalyzer();
        var mods = new[]
        {
            new ModManifest(
                "hd-textures",
                "HD Textures",
                "1.0.0",
                @"D:\Mods\HdTextures",
                0,
                true,
                [
                    new ModFileEntry(@"Data\Textures\Armor.dds", 1024, "a"),
                    new ModFileEntry(@"Data\Textures\Horse.dds", 2048, "b")
                ]),
            new ModManifest(
                "weather-pack",
                "Weather Pack",
                "1.0.0",
                @"D:\Mods\WeatherPack",
                1,
                true,
                [
                    new ModFileEntry("data/textures/armor.dds", 4096, "c")
                ])
        };

        var conflicts = analyzer.AnalyzeConflicts(mods);

        var conflict = Assert.Single(conflicts);
        Assert.Equal("DATA/TEXTURES/ARMOR.DDS", conflict.NormalizedVirtualPath);
        Assert.Equal(["hd-textures", "weather-pack"], conflict.ModIds);
        Assert.Equal(["hd-textures", "weather-pack"], conflict.LoadOrderModIds);
        Assert.Equal("weather-pack", conflict.WinningModId);
        Assert.Equal(64, conflict.Fingerprint.Length);
        Assert.Equal(conflict.Fingerprint, analyzer.AnalyzeConflicts(mods).Single().Fingerprint);

        var reversedLoadOrderConflict = analyzer.AnalyzeConflicts(mods.Select(mod => mod with
        {
            LoadOrder = mod.Id == "hd-textures" ? 2 : 1
        })).Single();
        Assert.NotEqual(conflict.Fingerprint, reversedLoadOrderConflict.Fingerprint);
    }

    [Fact]
    public void BuildPlan_ExcludesDisabledModsAndBuildsWinningVirtualPathEntries()
    {
        var builder = new ModMountPlanBuilder(new ModConflictAnalyzer());
        var mods = new[]
        {
            new ModManifest(
                "alpha",
                "Alpha",
                "1.0.0",
                @"D:\Mods\Alpha",
                0,
                true,
                [
                    new ModFileEntry(@"Data\Config\shared.xml", 10, "alpha-shared"),
                    new ModFileEntry(@"Data\Config\alpha.xml", 11, "alpha-only")
                ]),
            new ModManifest(
                "beta",
                "Beta",
                "1.0.0",
                @"D:\Mods\Beta",
                1,
                true,
                [
                    new ModFileEntry(@"data/config/shared.xml", 20, "beta-shared")
                ]),
            new ModManifest(
                "gamma-disabled",
                "Gamma Disabled",
                "1.0.0",
                @"D:\Mods\Gamma",
                2,
                false,
                [
                    new ModFileEntry(@"data/config/shared.xml", 30, "gamma-disabled")
                ])
        };

        var plan = builder.BuildPlan(mods);
        var sharedEntry = Assert.Single(plan.Entries, entry => entry.NormalizedVirtualPath == "DATA/CONFIG/SHARED.XML");

        Assert.Equal(3, plan.TotalModCount);
        Assert.Equal(2, plan.EnabledModCount);
        Assert.Equal(1, plan.DisabledModCount);
        Assert.Equal(3, plan.EnabledFileCount);
        Assert.Equal(2, plan.UniqueVirtualPathCount);
        Assert.Equal(1, plan.ShadowedProviderCount);
        Assert.Single(plan.Conflicts);
        Assert.Equal("beta", sharedEntry.WinningModId);
        Assert.Equal(["alpha", "beta"], sharedEntry.LoadOrderModIds);
        Assert.Equal(20, sharedEntry.WinningSizeInBytes);
        Assert.Equal("beta-shared", sharedEntry.WinningContentHash);
    }
}
