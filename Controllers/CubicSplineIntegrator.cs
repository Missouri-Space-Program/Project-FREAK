using MathNet.Numerics.Integration;
using MathNet.Numerics.Interpolation;

namespace Project_FREAK.Controllers;

public static class CubicSplineIntegrator
{
    public static (double Integral, double Average) IntegrateAndAverage(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values)
    {
        double integral = Integrate(time, values);
        if (values.Count == 0)
            return (integral, 0);

        double duration = time.Count > 1 ? time[^1] - time[0] : 0;
        if (duration > 0)
            return (integral, integral / duration);

        double sum = 0;
        for (int index = 0; index < values.Count; index++)
            sum += values[index];
        return (integral, sum / values.Count);
    }

    public static double Interpolate(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        double atTime)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(values);
        if (time.Count != values.Count)
            throw new ArgumentException("Time and value arrays must have the same length.");
        if (!double.IsFinite(atTime))
            throw new ArgumentOutOfRangeException(nameof(atTime), "Interpolation time must be finite.");

        var (uniqueTime, uniqueValues) = GetUniqueSamples(time, values);
        if (uniqueTime.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(time));
        if (atTime < uniqueTime[0] || atTime > uniqueTime[^1])
            throw new ArgumentOutOfRangeException(nameof(atTime), "Interpolation time must be within the sample range.");
        if (uniqueTime.Count == 1)
            return uniqueValues[0];

        var spline = CubicSpline.InterpolateNaturalSorted(uniqueTime.ToArray(), uniqueValues.ToArray());
        return spline.Interpolate(atTime);
    }

    public static (double[] Time, double[] Values) ClipInterval(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        double startTime,
        double endTime)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(values);
        if (time.Count != values.Count)
            throw new ArgumentException("Time and value arrays must have the same length.");
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || startTime >= endTime)
            throw new ArgumentOutOfRangeException(nameof(startTime), "The interval must have finite, increasing boundaries.");
        if (time.Count < 2 || startTime < time[0] || endTime > time[^1])
            throw new ArgumentOutOfRangeException(nameof(startTime), "The interval must be within the sample range.");

        var clippedTime = new List<double> { startTime };
        var clippedValues = new List<double> { Interpolate(time, values, startTime) };
        for (int index = 0; index < time.Count; index++)
        {
            if (time[index] <= startTime || time[index] >= endTime)
                continue;
            clippedTime.Add(time[index]);
            clippedValues.Add(values[index]);
        }
        clippedTime.Add(endTime);
        clippedValues.Add(Interpolate(time, values, endTime));
        return (clippedTime.ToArray(), clippedValues.ToArray());
    }

    public static (double Integral, double Average) IntegrateAndAverage(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        double startTime,
        double endTime)
    {
        double integral = Integrate(time, values, startTime, endTime);
        return (integral, integral / (endTime - startTime));
    }

    public static double Integrate(IReadOnlyList<double> time, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(values);
        if (time.Count != values.Count)
            throw new ArgumentException("Time and value arrays must have the same length.");
        if (time.Count < 2)
            return 0;

        var (uniqueTime, uniqueValues) = GetUniqueSamples(time, values);
        if (uniqueTime.Count < 2)
            return 0;

        var spline = CubicSpline.InterpolateNaturalSorted(uniqueTime.ToArray(), uniqueValues.ToArray());
        return spline.Integrate(uniqueTime[0], uniqueTime[^1]);
    }

    public static double Integrate(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values,
        double startTime,
        double endTime)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(values);
        if (time.Count != values.Count)
            throw new ArgumentException("Time and value arrays must have the same length.");
        if (!double.IsFinite(startTime) || !double.IsFinite(endTime) || startTime > endTime)
            throw new ArgumentOutOfRangeException(nameof(startTime), "Integration boundaries must be finite and increasing.");
        if (time.Count < 2)
            return 0;

        var (uniqueTime, uniqueValues) = GetUniqueSamples(time, values);
        if (uniqueTime.Count < 2)
            return 0;
        if (startTime < uniqueTime[0] || endTime > uniqueTime[^1])
            throw new ArgumentOutOfRangeException(nameof(startTime), "Integration boundaries must be within the sample range.");

        var spline = CubicSpline.InterpolateNaturalSorted(uniqueTime.ToArray(), uniqueValues.ToArray());
        return spline.Integrate(startTime, endTime);
    }

    private static (List<double> Time, List<double> Values) GetUniqueSamples(
        IReadOnlyList<double> time,
        IReadOnlyList<double> values)
    {
        var uniqueTime = new List<double>(time.Count);
        var uniqueValues = new List<double>(values.Count);
        var duplicateCounts = new List<int>(time.Count);
        for (int index = 0; index < time.Count; index++)
        {
            if (!double.IsFinite(time[index]) || !double.IsFinite(values[index]))
                throw new ArgumentException("Time and value samples must be finite.");
            if (index > 0 && time[index] < time[index - 1])
                throw new ArgumentException("Time values must be in ascending order.", nameof(time));

            if (uniqueTime.Count > 0 && time[index] == uniqueTime[^1])
            {
                int duplicateCount = duplicateCounts[^1];
                uniqueValues[^1] += (values[index] - uniqueValues[^1]) / (duplicateCount + 1);
                duplicateCounts[^1] = duplicateCount + 1;
                continue;
            }

            uniqueTime.Add(time[index]);
            uniqueValues.Add(values[index]);
            duplicateCounts.Add(1);
        }
        return (uniqueTime, uniqueValues);
    }
}