using BohemiX.App.Views;

namespace BohemiX.App.Tests;

public sealed class ModInspectorScrollTests
{
    [Fact]
    public void WheelInput_CalculatesAnOffsetForLongInspectorContent()
    {
        var nextOffset = MainWindow.CalculateScrollOffsetForWheelForTesting(
            currentOffset: 0,
            extentHeight: 900,
            viewportHeight: 200,
            deltaY: -1);

        Assert.Equal(88, nextOffset);
    }

    [Fact]
    public void WheelInput_DoesNotMovePastInspectorBounds()
    {
        var nextOffset = MainWindow.CalculateScrollOffsetForWheelForTesting(
            currentOffset: 700,
            extentHeight: 900,
            viewportHeight: 200,
            deltaY: -1);

        Assert.Null(nextOffset);
    }
}
