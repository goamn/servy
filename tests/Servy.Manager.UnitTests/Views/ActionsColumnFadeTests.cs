using Servy.Manager.Views;

namespace Servy.Manager.UnitTests.Views;

public sealed class ActionsColumnFadeTests
{
    [Fact]
    public void ShouldShowActionsColumnFade_WhenMoreThanToleranceRemains()
    {
        Assert.True(MainWindow.ShouldShowActionsColumnFade(scrollableWidth: 1000, horizontalOffset: 800));
    }

    [Theory]
    [InlineData(1000, 999)]
    [InlineData(1000, 1000)]
    [InlineData(0, 0)]
    public void ShouldHideActionsColumnFade_AtOrNearRightEdgeOrWithoutOverflow(
        double scrollableWidth,
        double horizontalOffset)
    {
        Assert.False(MainWindow.ShouldShowActionsColumnFade(scrollableWidth, horizontalOffset));
    }
}
