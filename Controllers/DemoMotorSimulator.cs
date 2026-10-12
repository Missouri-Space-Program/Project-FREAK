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

    public DemoMotorReading Sample(double elapsedSeconds, double thrustFullScaleNewtons = 5000, double pressureFullScalePsi = 1600)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (!double.IsFinite(thrustFullScaleNewtons) || thrustFullScaleNewtons <= 0)
            throw new ArgumentOutOfRangeException(nameof(thrustFullScaleNewtons));
        if (!double.IsFinite(pressureFullScalePsi) || pressureFullScalePsi <= 0)
            throw new ArgumentOutOfRangeException(nameof(pressureFullScalePsi));

        double phase = elapsedSeconds % CycleDurationSeconds;
        double burnFraction = phase switch
        {
            < 1 => phase * 0.2,
            < 3 => 0.2 + (phase - 1) * 0.4,
            < 7 => 1,
            < 10 => (10 - phase) / 3,
            _ => 0
        };

        double thrust = Math.Clamp(burnFraction * thrustFullScaleNewtons + Noise(thrustFullScaleNewtons * 0.015 * burnFraction), 0, thrustFullScaleNewtons);
        double pressure = Math.Clamp(burnFraction * pressureFullScalePsi + Noise(pressureFullScalePsi * 0.015 * burnFraction), 0, pressureFullScalePsi);
        return new DemoMotorReading(thrust, pressure);
    }

    public void SampleBatch(
        double startSeconds,
        double scanIntervalSeconds,
        Span<DemoMotorReading> destination,
        double thrustFullScaleNewtons = 5000,
        double pressureFullScalePsi = 1600)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = Sample(startSeconds + i * scanIntervalSeconds, thrustFullScaleNewtons, pressureFullScalePsi);
        }
    }

    private double Noise(double maximumMagnitude) => (_random.NextDouble() * 2 - 1) * maximumMagnitude;
}
