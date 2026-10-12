using Project_FREAK.Views;
using Xunit;

namespace Project_FREAK.Tests;

public class ReviewGraphScalingTests
{
    [Fact]
    public void GetYAxisLimits_ScalesToObservedPeakInsteadOfConfiguredRange()
    {
        var limits = ReviewDataPage.GetYAxisLimits(10, 120);

        Assert.Equal(0, limits.Minimum);
        Assert.Equal(126, limits.Maximum, 6);
    }

    [Fact]
    public void GetYAxisLimits_HandlesNegativeAndZeroOnlyData()
    {
        Assert.Equal((-21, 0), ReviewDataPage.GetYAxisLimits(-20, -5));
        Assert.Equal((0, 1), ReviewDataPage.GetYAxisLimits(0, 0));
    }
}