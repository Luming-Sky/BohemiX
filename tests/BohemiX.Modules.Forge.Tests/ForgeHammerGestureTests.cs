using System.Numerics;
using BohemiX.Modules.Forge.Controls;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeHammerGestureTests
{
    [Fact]
    public void ClickOnWorkpieceProducesOneLightCorrectiveStrike()
    {
        var result = ForgeHammerGesture.Resolve(
            startedOnWorkpiece: true,
            new Vector2(.42f, .58f),
            new Vector2(500, 400),
            new Vector2(502, 402));

        Assert.True(result.ShouldStrike);
        Assert.Equal(ForgeHammerGesture.LightStrikeForce, result.Force, 3);
        Assert.Equal(new Vector2(.42f, .58f), result.Lattice);
    }

    [Fact]
    public void UpwardPullControlsStrikeForceAndClampsIt()
    {
        var medium = ForgeHammerGesture.Resolve(
            true,
            new Vector2(.5f),
            new Vector2(400, 500),
            new Vector2(400, 436));
        var heavy = ForgeHammerGesture.Resolve(
            true,
            new Vector2(.5f),
            new Vector2(400, 500),
            new Vector2(400, 250));

        Assert.True(medium.ShouldStrike);
        Assert.Equal(.5, medium.Force, 3);
        Assert.True(heavy.ShouldStrike);
        Assert.Equal(1, heavy.Force, 3);
    }

    [Fact]
    public void WorkpieceMissAndSidewaysDragDoNotStrike()
    {
        var miss = ForgeHammerGesture.Resolve(
            false,
            new Vector2(.5f),
            new Vector2(400, 500),
            new Vector2(400, 420));
        var sideways = ForgeHammerGesture.Resolve(
            true,
            new Vector2(.5f),
            new Vector2(400, 500),
            new Vector2(440, 500));

        Assert.False(miss.ShouldStrike);
        Assert.False(sideways.ShouldStrike);
    }

    [Fact]
    public void HeldHammerChargeMapsTimeToAControlledStrikeForce()
    {
        var light = ForgeHammerGesture.ResolveHold(true, new Vector2(.5f), .05);
        var full = ForgeHammerGesture.ResolveHold(true, new Vector2(.5f), ForgeHammerGesture.ChargeDurationSeconds);
        var miss = ForgeHammerGesture.ResolveHold(false, new Vector2(.5f), 2);

        Assert.True(light.ShouldStrike);
        Assert.InRange(light.Force, .18, .23);
        Assert.Equal(1, full.Force, 3);
        Assert.False(miss.ShouldStrike);
    }

    [Fact]
    public void QuenchDepthComesFromDownwardTravelRatherThanScreenPosition()
    {
        var shallow = ForgeProcessGestures.ResolveQuench(
            new Vector2(400, 300),
            new Vector2(400, 345),
            3,
            .04);
        var deep = ForgeProcessGestures.ResolveQuench(
            new Vector2(400, 300),
            new Vector2(400, 435),
            3,
            .04);

        Assert.True(shallow.HasChanged);
        Assert.Equal(.3, shallow.Depth, 2);
        Assert.Equal(.9, deep.Depth, 2);
    }

    [Fact]
    public void GrindingGestureMapsTravelAcrossTheWholeEdge()
    {
        var left = ForgeProcessGestures.ResolveGrinding(
            new Vector2(500, 400),
            new Vector2(380, 400),
            -4,
            .04);
        var right = ForgeProcessGestures.ResolveGrinding(
            new Vector2(500, 400),
            new Vector2(620, 400),
            4,
            .04);

        Assert.True(left.HasChanged);
        Assert.Equal(0, left.Position, 2);
        Assert.Equal(1, right.Position, 2);
        Assert.True(left.Delta > 0);
    }
}
