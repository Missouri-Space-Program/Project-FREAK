using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class DemoMotorSimulatorTests
{
    [Fact]
    public void Sample_StaysWithinConfiguredSensorRanges()
    {
        var simulator = new DemoMotorSimulator(new Random(42));

        for (int sample = 0; sample < 1000; sample++)
        {
            var reading = simulator.Sample(sample * 0.05);

            Assert.InRange(reading.ThrustNewtons, 0, 5000);
            Assert.InRange(reading.PressurePsi, 0, 1600);
        }
    }

    [Fact]
    public void Sample_FollowsBurnCurveWithNoise()
    {
        var simulator = new DemoMotorSimulator(new Random(42));

        var earlyBurn = simulator.Sample(0.5);
        var peakBurn = simulator.Sample(4);
        var tailOff = simulator.Sample(9);
        var idle = simulator.Sample(11);
        var noisyPeak = simulator.Sample(4);

        Assert.True(peakBurn.ThrustNewtons > earlyBurn.ThrustNewtons);
        Assert.True(peakBurn.ThrustNewtons > tailOff.ThrustNewtons);
        Assert.True(tailOff.ThrustNewtons > idle.ThrustNewtons);
        Assert.NotEqual(peakBurn, noisyPeak);
    }

    [Fact]
    public void Sample_RejectsInvalidElapsedTime()
    {
        var simulator = new DemoMotorSimulator(new Random(42));

        Assert.Throws<ArgumentOutOfRangeException>(() => simulator.Sample(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => simulator.Sample(double.NaN));
    }
}
