namespace Project_FREAK.Controllers;

public readonly record struct DemoMotorReading(double ThrustNewtons, double PressurePsi);

public sealed class DemoMotorSimulator
{
    private const double CycleDurationSeconds = 12;
    private readonly Random _random;

    public DemoMotorSimulator(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    public DemoMotorReading Sample(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        double phase = elapsedSeconds % CycleDurationSeconds;
        double burnFraction = phase switch
        {
            < 1 => phase * 0.2,
            < 3 => 0.2 + (phase - 1) * 0.4,
            < 7 => 1,
            < 10 => (10 - phase) / 3,
            _ => 0
        };

        double thrust = Math.Clamp(burnFraction * 5000 + Noise(75), 0, 5000);
        double pressure = Math.Clamp(burnFraction * 1600 + Noise(24), 0, 1600);
        return new DemoMotorReading(thrust, pressure);
    }

    private double Noise(double maximumMagnitude) => (_random.NextDouble() * 2 - 1) * maximumMagnitude;
}
