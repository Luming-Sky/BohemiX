using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class ModLoadOrderPlannerTests
{
    [Fact]
    public void MoveToIndex_OrdersDeterministicallyAndClampsTargetIndex()
    {
        var planner = new ModLoadOrderPlanner();
        var mods = new[]
        {
            CreateMod("gamma", loadOrder: 2),
            CreateMod("beta", loadOrder: 1),
            CreateMod("alpha", loadOrder: 1)
        };

        var plan = planner.MoveToIndex(mods, "gamma", -10);

        Assert.True(plan.Changed);
        Assert.Equal("gamma", plan.MovedModId);
        Assert.Equal(2, plan.FromIndex);
        Assert.Equal(0, plan.ToIndex);
        Assert.Equal(["gamma", "alpha", "beta"], plan.OrderedModIds);
    }

    [Fact]
    public void MoveByOffset_ClampsAndMissingModKeepsCurrentOrder()
    {
        var planner = new ModLoadOrderPlanner();
        var mods = new[]
        {
            CreateMod("alpha", loadOrder: 0),
            CreateMod("beta", loadOrder: 1),
            CreateMod("gamma", loadOrder: 2)
        };

        var movedPlan = planner.MoveByOffset(mods, "alpha", 99);
        var missingPlan = planner.MoveByOffset(mods, "missing", 1);

        Assert.True(movedPlan.Changed);
        Assert.Equal(0, movedPlan.FromIndex);
        Assert.Equal(2, movedPlan.ToIndex);
        Assert.Equal(["beta", "gamma", "alpha"], movedPlan.OrderedModIds);

        Assert.False(missingPlan.Changed);
        Assert.Equal(-1, missingPlan.FromIndex);
        Assert.Equal(-1, missingPlan.ToIndex);
        Assert.Equal(["alpha", "beta", "gamma"], missingPlan.OrderedModIds);
    }

    private static ModManifest CreateMod(string id, int loadOrder)
    {
        return new ModManifest(
            id,
            id,
            "1.0.0",
            @$"D:\Mods\{id}",
            loadOrder,
            true,
            []);
    }
}
