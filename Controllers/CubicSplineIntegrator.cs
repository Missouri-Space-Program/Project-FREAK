using MathNet.Numerics.Integration;
using MathNet.Numerics.Interpolation;

namespace Project_FREAK.Controllers;

public static class CubicSplineIntegrator
{
    public static double Integrate(IReadOnlyList<double> time, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(values);
        if (time.Count != values.Count)
            throw new ArgumentException("Time and value arrays must have the same length.");
        if (time.Count < 2)
            return 0;

        var uniqueTime = new List<double>(time.Count);
        var uniqueValues = new List<double>(values.Count);
        var duplicateCounts = new List<int>(time.Count);
        for (int index = 0; index < time.Count; index++)
        {
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

        if (uniqueTime.Count < 2)
            return 0;

        var spline = CubicSpline.InterpolateNaturalSorted(uniqueTime.ToArray(), uniqueValues.ToArray());
        return spline.Integrate(uniqueTime[0], uniqueTime[^1]);
    }
}