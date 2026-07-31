using BohemiX.Modules.Alchemy.Controls;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class AlchemyCanvasSchedulingTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void RenderFrames_RequireVisibleNonMinimizedAttachment(
        bool isAttached,
        bool isEffectivelyVisible,
        bool isTopLevelVisible,
        bool isWindowMinimized,
        bool expected)
    {
        Assert.Equal(expected, AlchemyCanvas.ShouldRenderFrames(
            isAttached,
            isEffectivelyVisible,
            isTopLevelVisible,
            isWindowMinimized));
    }
}
