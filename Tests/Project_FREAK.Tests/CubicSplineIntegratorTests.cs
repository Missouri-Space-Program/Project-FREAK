using Project_FREAK.Controllers;
using MathNet.Numerics.Interpolation;
using Xunit;

namespace Project_FREAK.Tests;

public class CubicSplineIntegratorTests
{
    [Fact]
    public void Integrate_UsesNaturalCubicSplineForUnevenSamples()
    {
        double[] time = { 0, 1, 3, 4 };
        double[] values = { 2, 4, 8, 10 };
        double result = CubicSplineIntegrator.Integrate(
            time,
            values);
        var spline = CubicSpline.InterpolateNaturalSorted(time, values);

        Assert.Equal(spline.Integrate(time[0], time[^1]), result, 10);
    }

    [Fact]
    public void Integrate_AveragesDuplicateTimestampsBeforeInterpolation()
    {
        double[] time = { 0, 1, 1, 2 };
        double[] values = { 0, 2, 4, 4 };
        double result = CubicSplineIntegrator.Integrate(
            time,
            values);
        var spline = CubicSpline.InterpolateNaturalSorted(
            new[] { 0d, 1, 2 },
            new[] { 0d, 3, 4 });

        Assert.Equal(spline.Integrate(0, 2), result, 10);
    }

    [Fact]
    public void Integrate_FallsBackToTrapezoidForTwoSamples()
    {
        double result = CubicSplineIntegrator.Integrate(
            new[] { 0d, 2 },
            new[] { 1d, 3 });

        Assert.Equal(4, result, 10);
    }
}