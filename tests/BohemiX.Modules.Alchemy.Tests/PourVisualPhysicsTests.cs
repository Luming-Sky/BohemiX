using Avalonia;
using BohemiX.Modules.Alchemy.Controls;
using BohemiX.Modules.Alchemy.Models;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class PourVisualPhysicsTests
{
    [Theory]
    [InlineData((int)BottleCapPhase.Closed, 0.0)]
    [InlineData((int)BottleCapPhase.Opening, 0.75)]
    [InlineData((int)BottleCapPhase.Open, 0.99)]
    public void CalculateFlow_BlocksLiquidUntilCorkIsFullyOpen(int phase, double capProgress)
    {
        var flow = PourVisualPhysics.CalculateFlow(
            BaseLiquid.Water,
            96,
            (BottleCapPhase)phase,
            capProgress,
            1);

        Assert.Equal(0, flow);
    }

    [Fact]
    public void CalculateFlow_RespondsToTiltAndLiquidViscosity()
    {
        var water = PourVisualPhysics.CalculateFlow(BaseLiquid.Water, 86, BottleCapPhase.Open, 1, 1);
        var oil = PourVisualPhysics.CalculateFlow(BaseLiquid.Oil, 86, BottleCapPhase.Open, 1, 1);

        Assert.True(water > 0.5);
        Assert.True(oil > 0);
        Assert.True(oil < water);
    }

    [Fact]
    public void GetBottleMouth_FollowsBottleRotation()
    {
        var center = new Point(100, 100);
        var upright = PourVisualPhysics.GetBottleMouth(center, 0, 1);
        var sideways = PourVisualPhysics.GetBottleMouth(center, 90, 1);

        Assert.Equal(100, upright.X, 3);
        Assert.Equal(51, upright.Y, 3);
        Assert.Equal(149, sideways.X, 3);
        Assert.Equal(100, sideways.Y, 3);
    }

    [Fact]
    public void CreateTrajectory_UsesGravityAndFindsCauldronImpact()
    {
        var trajectory = PourVisualPhysics.CreateTrajectory(
            new Point(700, 250),
            90,
            BaseLiquid.Water,
            1,
            380);

        Assert.NotNull(trajectory);
        var value = trajectory.Value;
        Assert.True(value.HitsCauldron);
        Assert.InRange(value.Impact.X, PourVisualPhysics.CauldronInteriorLeft, PourVisualPhysics.CauldronInteriorRight);
        Assert.Equal(380, value.Impact.Y, 3);

        var midpoint = value.PositionAt(0.5);
        var linearMidpointY = (value.Mouth.Y + value.Impact.Y) / 2;
        Assert.True(midpoint.Y < linearMidpointY);
    }

    [Fact]
    public void CreateTrajectory_DetectsStreamOutsideCauldron()
    {
        var trajectory = PourVisualPhysics.CreateTrajectory(
            new Point(980, 250),
            90,
            BaseLiquid.Spirits,
            1,
            380);

        Assert.NotNull(trajectory);
        Assert.False(trajectory.Value.HitsCauldron);
        Assert.Equal(0, PourVisualPhysics.ProgressDelta(BaseLiquid.Spirits, 1, 0.5, false));
    }

    [Fact]
    public void ProgressDelta_IsContinuousAndFlowDriven()
    {
        var shortFrame = PourVisualPhysics.ProgressDelta(BaseLiquid.Wine, 0.65, 1.0 / 60, true);
        var longFrame = PourVisualPhysics.ProgressDelta(BaseLiquid.Wine, 0.65, 2.0 / 60, true);

        Assert.True(shortFrame > 0);
        Assert.Equal(shortFrame * 2, longFrame, 8);
        Assert.True(longFrame < 0.02);
    }
}
