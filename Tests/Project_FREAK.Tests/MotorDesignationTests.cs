using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class MotorDesignationTests
{
    [Theory]
    [InlineData(2.5, 'A')]
    [InlineData(2.5001, 'B')]
    [InlineData(81920, 'P')]
    public void Format_UsesPythonImpulseClassBoundaries(double impulse, char expectedClass)
    {
        string designation = MotorDesignation.Format(impulse, 2979.66);

        Assert.StartsWith(expectedClass.ToString(), designation);
        Assert.EndsWith("2980", designation);
    }

    [Fact]
    public void Format_IndicatesImpulseOutsideSupportedRange()
    {
        string designation = MotorDesignation.Format(81920.01, 1000);

        Assert.Contains("Unclassified", designation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.25)]
    [InlineData(-1)]
    public void Format_DoesNotAssignAClassBelowMinimumImpulse(double impulse)
    {
        Assert.Contains("Unclassified", MotorDesignation.Format(impulse, 10));
    }
}