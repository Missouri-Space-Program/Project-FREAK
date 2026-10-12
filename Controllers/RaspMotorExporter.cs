using System.Globalization;
using System.IO;
using System.Text;

namespace Project_FREAK.Controllers;

public readonly record struct RaspDataPoint(double Time, double Thrust);

public sealed record RaspIntervalStats(
    double DurationSeconds,
    double TotalImpulseNewtonSeconds,
    double AverageThrustNewtons,
    double PeakThrustNewtons,
    int SampleCount,
    string MotorDesignation);

public static class RaspMotorExporter
{
    /// <summary>
    /// Validates the export parameters and returns any validation error messages.
    /// </summary>
    public static bool ValidateParameters(
        RaspMotorParameters parameters,
        double datasetMinTime,
        double datasetMaxTime,
        out List<string> validationErrors)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        validationErrors = new List<string>();

        if (string.IsNullOrWhiteSpace(parameters.MotorName))
            validationErrors.Add("Motor name cannot be empty.");

        if (!double.IsFinite(parameters.DiameterMillimeters) || parameters.DiameterMillimeters <= 0)
            validationErrors.Add("Casing diameter must be a positive number (in mm).");

        if (!double.IsFinite(parameters.LengthMillimeters) || parameters.LengthMillimeters <= 0)
            validationErrors.Add("Casing length must be a positive number (in mm).");

        if (string.IsNullOrWhiteSpace(parameters.Delays))
            validationErrors.Add("Delays must be specified (use 'P' for plugged / no ejection charge, '0' for none, or numbers like '4-8-12').");

        if (!double.IsFinite(parameters.PropellantWeightKilograms) || parameters.PropellantWeightKilograms <= 0)
            validationErrors.Add("Propellant weight must be greater than 0 kg.");

        if (!double.IsFinite(parameters.TotalWeightKilograms) || parameters.TotalWeightKilograms <= 0)
            validationErrors.Add("Total motor weight must be greater than 0 kg.");
        else if (parameters.TotalWeightKilograms < parameters.PropellantWeightKilograms)
            validationErrors.Add("Total motor weight cannot be less than propellant weight.");

        if (string.IsNullOrWhiteSpace(parameters.Manufacturer))
            validationErrors.Add("Manufacturer abbreviation cannot be empty.");

        if (!double.IsFinite(parameters.IntervalStartSeconds) || !double.IsFinite(parameters.IntervalEndSeconds) ||
            parameters.IntervalEndSeconds <= parameters.IntervalStartSeconds)
            validationErrors.Add("Interval end time must be greater than interval start time.");

        if (parameters.IntervalStartSeconds < datasetMinTime || parameters.IntervalEndSeconds > datasetMaxTime)
            validationErrors.Add($"Interval must fall within the dataset range ({datasetMinTime:F2} s to {datasetMaxTime:F2} s).");

        return validationErrors.Count == 0;
    }

    /// <summary>
    /// Calculates statistics for the specified time interval of a review dataset.
    /// </summary>
    public static RaspIntervalStats CalculateIntervalStatistics(
        double[] timeSeconds,
        double[] thrustNewtons,
        double startSeconds,
        double endSeconds)
    {
        ArgumentNullException.ThrowIfNull(timeSeconds);
        ArgumentNullException.ThrowIfNull(thrustNewtons);

        if (timeSeconds.Length != thrustNewtons.Length)
            throw new ArgumentException("Time and thrust arrays must have the same length.");
        if (timeSeconds.Length == 0)
            return new RaspIntervalStats(0, 0, 0, 0, 0, "No data");

        (startSeconds, endSeconds) = NormalizeInterval(startSeconds, endSeconds);
        if (!IsIntervalInRange(timeSeconds, startSeconds, endSeconds))
            return new RaspIntervalStats(0, 0, 0, 0, 0, "No samples");

        (double[] subTime, double[] subThrust) = CubicSplineIntegrator.ClipInterval(
            timeSeconds, thrustNewtons, startSeconds, endSeconds);
        double duration = endSeconds - startSeconds;
        double peakThrust = 0;
        int sampleCount = 0;
        for (int i = 0; i < subThrust.Length; i++)
        {
            if (subThrust[i] > peakThrust)
                peakThrust = subThrust[i];
        }
        for (int i = 0; i < timeSeconds.Length; i++)
        {
            if (timeSeconds[i] >= startSeconds && timeSeconds[i] <= endSeconds)
                sampleCount++;
        }

        (double totalImpulse, double averageThrust) = CubicSplineIntegrator.IntegrateAndAverage(
            timeSeconds, thrustNewtons, startSeconds, endSeconds);
        string designation = MotorDesignation.Format(totalImpulse, averageThrust);

        return new RaspIntervalStats(duration, totalImpulse, averageThrust, peakThrust, sampleCount, designation);
    }

    /// <summary>
    /// Finds sample indices spanning the interval [startSeconds, endSeconds].
    /// </summary>
    public static (int startIndex, int endIndex) GetIntervalIndices(double[] time, double startSeconds, double endSeconds)
    {
        if (time.Length == 0)
            return (0, -1);

        if (startSeconds > endSeconds)
            (startSeconds, endSeconds) = (endSeconds, startSeconds);

        int startIndex = Array.BinarySearch(time, startSeconds);
        if (startIndex < 0) startIndex = ~startIndex;

        int endIndex = Array.BinarySearch(time, endSeconds);
        if (endIndex < 0) endIndex = Math.Max(startIndex, ~endIndex - 1);

        startIndex = Math.Clamp(startIndex, 0, time.Length - 1);
        endIndex = Math.Clamp(endIndex, 0, time.Length - 1);

        return (startIndex, endIndex);
    }

    /// <summary>
    /// Extracts, normalizes, and optionally decimates data points for RASP export.
    /// Relative time starts at 0, all intermediate points have thrust &gt; 0, and
    /// the final point has thrust = 0.
    /// </summary>
    public static List<RaspDataPoint> ExtractDataPoints(
        double[] timeSeconds,
        double[] thrustNewtons,
        double startSeconds,
        double endSeconds,
        int? maxPoints = null)
    {
        ArgumentNullException.ThrowIfNull(timeSeconds);
        ArgumentNullException.ThrowIfNull(thrustNewtons);

        if (timeSeconds.Length != thrustNewtons.Length)
            throw new ArgumentException("Time and thrust arrays must have the same length.");
        if (timeSeconds.Length == 0)
            return new List<RaspDataPoint>();

        (startSeconds, endSeconds) = NormalizeInterval(startSeconds, endSeconds);
        if (!IsIntervalInRange(timeSeconds, startSeconds, endSeconds))
            return new List<RaspDataPoint>();

        (double[] intervalTime, double[] intervalThrust) = CubicSplineIntegrator.ClipInterval(
            timeSeconds, thrustNewtons, startSeconds, endSeconds);
        double burnDuration = Math.Max(0.001, endSeconds - startSeconds);
        var rawPoints = new List<RaspDataPoint>();

        double prevTime = 0.0;
        for (int i = 0; i < intervalTime.Length; i++)
        {
            double relTime = intervalTime[i] - startSeconds;
            double thrust = intervalThrust[i];

            if (thrust <= 0.0)
                continue;

            // RASP specification:
            // 1. Implicit first point at (0, 0) is assumed and must NOT be explicitly written.
            // 2. An entry with zero thrust other than the final point is rejected as a premature burn end.
            // Therefore, if relTime <= 0 with zero/negative thrust, skip it.
            if (relTime <= 0.0)
            {
                // If there is positive thrust right at the start boundary, give it a tiny positive time offset.
                relTime = 0.0001;
            }

            // Ensure strictly monotonic ascending time.
            if (relTime <= prevTime)
                relTime = prevTime + 0.0001;

            if (relTime >= burnDuration)
                break;

            rawPoints.Add(new RaspDataPoint(relTime, thrust));
            prevTime = relTime;
        }

        if (rawPoints.Count == 0)
            return rawPoints;

        // Simplify if requested and count exceeds maxPoints.
        // The final zero-thrust point will be added after simplification, so we target (maxPoints - 1) points.
        List<RaspDataPoint> simplifiedPoints = rawPoints;
        if (maxPoints is > 2 && rawPoints.Count > maxPoints.Value - 1)
        {
            simplifiedPoints = SimplifyPoints(rawPoints, maxPoints.Value - 1);
        }

        // Final point must have thrust = 0, indicating burn end time.
        simplifiedPoints.Add(new RaspDataPoint(burnDuration, 0.0));

        return simplifiedPoints;
    }

    private static (double Start, double End) NormalizeInterval(double startSeconds, double endSeconds) =>
        startSeconds <= endSeconds ? (startSeconds, endSeconds) : (endSeconds, startSeconds);

    private static bool IsIntervalInRange(double[] timeSeconds, double startSeconds, double endSeconds) =>
        double.IsFinite(startSeconds) && double.IsFinite(endSeconds) &&
        endSeconds > startSeconds && startSeconds >= timeSeconds[0] && endSeconds <= timeSeconds[^1];

    /// <summary>
    /// Simplifies a curve of points using Ramer-Douglas-Peucker algorithm while ensuring
    /// the peak thrust point is strictly preserved.
    /// </summary>
    public static List<RaspDataPoint> SimplifyPoints(List<RaspDataPoint> points, int targetMaxPoints)
    {
        if (points.Count <= targetMaxPoints || targetMaxPoints < 2)
            return points;

        // Find peak thrust point to ensure it is always preserved.
        int peakIndex = 0;
        double maxThrust = points[0].Thrust;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Thrust > maxThrust)
            {
                maxThrust = points[i].Thrust;
                peakIndex = i;
            }
        }

        // Binary search on epsilon to find the tolerance that yields <= targetMaxPoints.
        double low = 0.0;
        double high = Math.Max(1.0, maxThrust * 2.0);
        List<RaspDataPoint> best = points;

        for (int iter = 0; iter < 24; iter++)
        {
            double mid = (low + high) / 2.0;
            var candidate = SimplifyWithPeak(points, peakIndex, mid);

            if (candidate.Count <= targetMaxPoints)
            {
                best = candidate;
                high = mid; // Try smaller epsilon to keep more detail
            }
            else
            {
                low = mid; // Need larger epsilon to drop more points
            }
        }

        // If best is still larger than target (extremely rare), downsample uniformly while preserving peak.
        if (best.Count > targetMaxPoints)
        {
            best = UniformDownsampleWithPeak(best, targetMaxPoints);
        }

        return best;
    }

    private static List<RaspDataPoint> SimplifyWithPeak(List<RaspDataPoint> points, int peakIndex, double epsilon)
    {
        if (peakIndex <= 0 || peakIndex >= points.Count - 1)
        {
            var result = new List<RaspDataPoint>();
            RdpRecursive(points, 0, points.Count - 1, epsilon, result);
            result.Add(points[^1]);
            return result;
        }

        var part1 = new List<RaspDataPoint>();
        RdpRecursive(points, 0, peakIndex, epsilon, part1);
        part1.Add(points[peakIndex]);

        var part2 = new List<RaspDataPoint>();
        RdpRecursive(points, peakIndex, points.Count - 1, epsilon, part2);
        part2.Add(points[^1]);

        // Combine parts, avoiding duplicating the peak point
        var combined = new List<RaspDataPoint>(part1.Count + part2.Count - 1);
        combined.AddRange(part1);
        for (int i = 1; i < part2.Count; i++)
            combined.Add(part2[i]);

        return combined;
    }

    private static void RdpRecursive(
        List<RaspDataPoint> points,
        int firstIndex,
        int lastIndex,
        double epsilon,
        List<RaspDataPoint> output)
    {
        output.Add(points[firstIndex]);

        double maxDistance = 0;
        int maxIndex = firstIndex;

        double t1 = points[firstIndex].Time;
        double f1 = points[firstIndex].Thrust;
        double t2 = points[lastIndex].Time;
        double f2 = points[lastIndex].Thrust;

        double dt = t2 - t1;
        double df = f2 - f1;
        double lineLenSq = dt * dt + df * df;

        for (int i = firstIndex + 1; i < lastIndex; i++)
        {
            double distance;
            if (lineLenSq < 1e-12)
            {
                double dti = points[i].Time - t1;
                double dfi = points[i].Thrust - f1;
                distance = Math.Sqrt(dti * dti + dfi * dfi);
            }
            else
            {
                // Perpendicular distance from points[i] to line segment (firstIndex, lastIndex)
                double cross = Math.Abs(df * points[i].Time - dt * points[i].Thrust + t2 * f1 - f2 * t1);
                distance = cross / Math.Sqrt(lineLenSq);
            }

            if (distance > maxDistance)
            {
                maxDistance = distance;
                maxIndex = i;
            }
        }

        if (maxDistance > epsilon && maxIndex > firstIndex && maxIndex < lastIndex)
        {
            RdpRecursive(points, firstIndex, maxIndex, epsilon, output);
            RdpRecursive(points, maxIndex, lastIndex, epsilon, output);
        }
    }

    private static List<RaspDataPoint> UniformDownsampleWithPeak(List<RaspDataPoint> points, int targetCount)
    {
        if (points.Count <= targetCount || targetCount < 3)
            return points;

        int peakIndex = 0;
        double maxThrust = points[0].Thrust;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Thrust > maxThrust)
            {
                maxThrust = points[i].Thrust;
                peakIndex = i;
            }
        }

        var result = new HashSet<int> { 0, peakIndex, points.Count - 1 };
        int remaining = targetCount - result.Count;
        double step = (double)(points.Count - 1) / (remaining + 1);

        for (int i = 1; i <= remaining; i++)
        {
            int index = Math.Clamp((int)Math.Round(i * step), 0, points.Count - 1);
            result.Add(index);
        }

        var sortedIndices = result.ToList();
        sortedIndices.Sort();

        return sortedIndices.Select(idx => points[idx]).ToList();
    }

    /// <summary>
    /// Generates the complete string content of a .eng file complying with the RASP format specification.
    /// </summary>
    public static string GenerateEngContent(
        ReviewDataset dataset,
        RaspMotorParameters parameters,
        string? sourceFileName = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(parameters);

        var stats = CalculateIntervalStatistics(
            dataset.TimeSeconds,
            dataset.ThrustNewtons,
            parameters.IntervalStartSeconds,
            parameters.IntervalEndSeconds);

        var points = ExtractDataPoints(
            dataset.TimeSeconds,
            dataset.ThrustNewtons,
            parameters.IntervalStartSeconds,
            parameters.IntervalEndSeconds,
            parameters.MaxDataPoints);
        if (stats.PeakThrustNewtons <= 0 || stats.TotalImpulseNewtonSeconds <= 0 || points.Count == 0)
            throw new InvalidDataException("The selected interval must contain a positive-thrust burn with positive total impulse.");

        var sb = new StringBuilder();

        // 1. Comments
        sb.AppendLine("; RASP motor data generated by Project-FREAK");
        if (!string.IsNullOrWhiteSpace(sourceFileName))
            sb.AppendLine($"; Source: {Path.GetFileName(sourceFileName)}");
        sb.AppendLine($"; Motor Designation: {stats.MotorDesignation}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"; Total Impulse: {stats.TotalImpulseNewtonSeconds:F2} N-s"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"; Average Thrust: {stats.AverageThrustNewtons:F2} N"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"; Peak Thrust: {stats.PeakThrustNewtons:F2} N"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"; Burn Time: {stats.DurationSeconds:F3} s"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"; Selected Interval: {parameters.IntervalStartSeconds:F3} s - {parameters.IntervalEndSeconds:F3} s"));

        if (!string.IsNullOrWhiteSpace(parameters.Comments))
        {
            using var reader = new StringReader(parameters.Comments);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    sb.AppendLine($"; {line.TrimStart(';', ' ')}");
            }
        }

        // 2. Header Line
        // 7 tokens separated by single space:
        // <motor_name> <diameter_mm> <length_mm> <delays> <propellant_weight_kg> <total_weight_kg> <manufacturer>
        string motorName = SanitizeToken(parameters.MotorName, stats.MotorDesignation);
        string diameter = parameters.DiameterMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        string length = parameters.LengthMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
        string delays = SanitizeDelays(parameters.Delays);
        string propellantWeight = parameters.PropellantWeightKilograms.ToString("0.#####", CultureInfo.InvariantCulture);
        string totalWeight = parameters.TotalWeightKilograms.ToString("0.#####", CultureInfo.InvariantCulture);
        string manufacturer = SanitizeToken(parameters.Manufacturer, "MSP");

        sb.AppendLine($"{motorName} {diameter} {length} {delays} {propellantWeight} {totalWeight} {manufacturer}");

        // 3. Data points
        foreach (var point in points)
        {
            string timeStr = point.Time.ToString("0.0000", CultureInfo.InvariantCulture);
            string thrustStr = point.Thrust == 0.0 ? "0" : point.Thrust.ToString("0.000", CultureInfo.InvariantCulture);
            sb.AppendLine($"   {timeStr} {thrustStr}");
        }

        // 4. Trailing semicolon
        sb.AppendLine(";");

        return sb.ToString();
    }

    /// <summary>
    /// Exports the dataset over the configured interval to a .eng file on disk.
    /// </summary>
    public static void ExportToFile(
        string targetFilePath,
        ReviewDataset dataset,
        RaspMotorParameters parameters,
        string? sourceFileName = null)
    {
        ArgumentNullException.ThrowIfNull(targetFilePath);
        string content = GenerateEngContent(dataset, parameters, sourceFileName);
        string? dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(targetFilePath, content, Encoding.ASCII);
    }

    private static string SanitizeToken(string? token, string fallback)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.IsNullOrWhiteSpace(fallback) ? "MOTOR" : fallback.Replace(" ", "-");

        // RASP tokens are whitespace-separated. Replace any whitespace with hyphens.
        string sanitized = string.Join("-", token.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
    }

    private static string SanitizeDelays(string? delays)
    {
        if (string.IsNullOrWhiteSpace(delays))
            return "P";

        // Replace whitespace and commas with dashes
        string sanitized = delays.Trim().Replace(" ", "-").Replace(",", "-");
        while (sanitized.Contains("--", StringComparison.Ordinal))
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);

        return string.IsNullOrEmpty(sanitized) ? "P" : sanitized;
    }
}
