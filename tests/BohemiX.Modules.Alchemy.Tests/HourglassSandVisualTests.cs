using BohemiX.Modules.Alchemy.Controls;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class HourglassSandVisualTests
{
    [Fact]
    public void Calculate_StartsWithFullSourceAndEmptyReceiver()
    {
        var state = HourglassSandVisual.Calculate(0);

        Assert.Equal(1, state.Remaining);
        Assert.Equal(0, state.Progress);
        Assert.True(state.SourceHalfWidth > state.ReceiverHalfWidth);
        Assert.True(state.StreamVisible);
    }

    [Fact]
    public void Calculate_TransfersSandContinuously()
    {
        var early = HourglassSandVisual.Calculate(0.25);
        var late = HourglassSandVisual.Calculate(0.75);

        Assert.True(late.SourceHalfWidth < early.SourceHalfWidth);
        Assert.True(late.SourceSurfaceY < early.SourceSurfaceY);
        Assert.True(late.ReceiverSurfaceY > early.ReceiverSurfaceY);
        Assert.Equal(HourglassSandVisual.InnerHalfWidth(late.ReceiverSurfaceY), late.ReceiverHalfWidth, 8);
    }

    [Fact]
    public void Calculate_ClampsProgressAndStopsStreamAtCompletion()
    {
        var state = HourglassSandVisual.Calculate(2);

        Assert.Equal(1, state.Progress);
        Assert.Equal(0, state.Remaining);
        Assert.False(state.StreamVisible);
    }

    [Fact]
    public void InnerBounds_PreservePaintedAsymmetryAndLowerChamberDepth()
    {
        var upperShoulder = HourglassSandVisual.InnerBounds(-42.2);
        var lowerShoulder = HourglassSandVisual.InnerBounds(31.8);
        var lowerFloor = HourglassSandVisual.InnerBounds(42.2);

        Assert.NotEqual(0, upperShoulder.Center);
        Assert.NotEqual(0, lowerShoulder.Center);
        Assert.True(lowerShoulder.HalfWidth > lowerFloor.HalfWidth);
        Assert.Equal(-0.95, lowerFloor.Center, 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(0.9)]
    [InlineData(1)]
    public void Calculate_ConservesSandAreaAcrossAsymmetricChambers(double progress)
    {
        var state = HourglassSandVisual.Calculate(progress);
        var sourceArea = HourglassSandVisual.SourceAreaAt(state.SourceSurfaceY);
        var receiverArea = HourglassSandVisual.ReceiverAreaAt(state.ReceiverSurfaceY);

        Assert.Equal(HourglassSandVisual.TransferableArea, sourceArea + receiverArea, 5);
        Assert.Equal(HourglassSandVisual.TransferableArea * (1 - progress), sourceArea, 5);
        Assert.Equal(HourglassSandVisual.TransferableArea * progress, receiverArea, 5);
    }

    [Fact]
    public void SettledState_UsesTheSameTotalAreaAsTheFlowingState()
    {
        var settledArea = HourglassSandVisual.SettledAreaAt(HourglassSandVisual.SettledSurfaceY);

        Assert.Equal(HourglassSandVisual.TransferableArea, settledArea, 5);
    }
}
