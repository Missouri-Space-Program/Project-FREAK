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
    public void Sample_UsesConfiguredSensorRanges()
    {
        var simulator = new DemoMotorSimulator(new Random(42));

        var reading = simulator.Sample(4, thrustFullScaleNewtons: 5000, pressureFullScalePsi: 250);

        Assert.InRange(reading.ThrustNewtons, 4925, 5000);
        Assert.InRange(reading.PressurePsi, 246.25, 250);
    }

    [Fact]
    public void Sample_RejectsInvalidElapsedTime()
    {
        var simulator = new DemoMotorSimulator(new Random(42));

        Assert.Throws<ArgumentOutOfRangeException>(() => simulator.Sample(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => simulator.Sample(double.NaN));
    }

    [Fact]
    public void SampleBatch_GeneratesExpectedPoints()
    {
        var simulator = new DemoMotorSimulator(new Random(42));
        var batch = new DemoMotorReading[50];
        simulator.SampleBatch(1.0, 0.001, batch, 5000, 1600);

        Assert.Equal(50, batch.Length);
        for (int i = 0; i < batch.Length; i++)
        {
            Assert.InRange(batch[i].ThrustNewtons, 0, 5000);
            Assert.InRange(batch[i].PressurePsi, 0, 1600);
        }
    }
}
