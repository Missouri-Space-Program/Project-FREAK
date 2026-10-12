using System.Globalization;
using System.IO;
using MathNet.Numerics.Interpolation;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class RaspMotorExporterTests
{
    private static ReviewDataset CreateSampleDataset()
    {
        // 100 samples from 0.0 to 0.99 seconds (100 Hz), triangular thrust curve peaking at 50 N
        int count = 100;
        double[] time = new double[count];
        double[] thrust = new double[count];
        double[] pressure = new double[count];

        for (int i = 0; i < count; i++)
        {
            time[i] = i * 0.01;
            // Peak at i = 50 (t = 0.5s)
            thrust[i] = i <= 50 ? i : (100 - i);
            pressure[i] = thrust[i] * 10;
        }

        return new ReviewDataset(time, thrust, pressure, null, "Test conversion");
    }

    [Fact]
    public void ValidateParameters_WithValidInputs_Succeeds()
    {
        var parameters = new RaspMotorParameters
        {
            MotorName = "F32",
            DiameterMillimeters = 29,
            LengthMillimeters = 124,
            Delays = "P",
            PropellantWeightKilograms = 0.0377,
            TotalWeightKilograms = 0.0695,
            Manufacturer = "MSP",
            IntervalStartSeconds = 0.1,
            IntervalEndSeconds = 0.9
        };

        bool valid = RaspMotorExporter.ValidateParameters(parameters, 0.0, 1.0, out var errors);

        Assert.True(valid);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("", 29, 124, "P", 0.05, 0.1, "MSP", 0.1, 0.9, "Motor name")]
    [InlineData("F32", 0, 124, "P", 0.05, 0.1, "MSP", 0.1, 0.9, "diameter")]
    [InlineData("F32", 29, -5, "P", 0.05, 0.1, "MSP", 0.1, 0.9, "length")]
    [InlineData("F32", 29, 124, "", 0.05, 0.1, "MSP", 0.1, 0.9, "Delays")]
    [InlineData("F32", 29, 124, "P", 0, 0.1, "MSP", 0.1, 0.9, "Propellant")]
    [InlineData("F32", 29, 124, "P", 0.15, 0.1, "MSP", 0.1, 0.9, "cannot be less")]
    [InlineData("F32", 29, 124, "P", 0.05, 0.1, "", 0.1, 0.9, "Manufacturer")]
    [InlineData("F32", 29, 124, "P", 0.05, 0.1, "MSP", 0.9, 0.1, "end time must be greater")]
    [InlineData("F32", 29, 124, "P", 0.05, 0.1, "MSP", -1.0, 0.5, "within the dataset range")]
    public void ValidateParameters_WithInvalidInputs_ReportsExpectedErrors(
        string motorName,
        double diameter,
        double length,
        string delays,
        double propellant,
        double totalWeight,
        string mfg,
        double start,
        double end,
        string expectedErrorSubstring)
    {
        var parameters = new RaspMotorParameters
        {
            MotorName = motorName,
            DiameterMillimeters = diameter,
            LengthMillimeters = length,
            Delays = delays,
            PropellantWeightKilograms = propellant,
            TotalWeightKilograms = totalWeight,
            Manufacturer = mfg,
            IntervalStartSeconds = start,
            IntervalEndSeconds = end
        };

        bool valid = RaspMotorExporter.ValidateParameters(parameters, 0.0, 1.0, out var errors);

        Assert.False(valid);
        Assert.Contains(errors, err => err.Contains(expectedErrorSubstring, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CalculateIntervalStatistics_CalculatesExpectedValues()
    {
        var dataset = CreateSampleDataset();

        var stats = RaspMotorExporter.CalculateIntervalStatistics(
            dataset.TimeSeconds,
            dataset.ThrustNewtons,
            0.2,
            0.8);

        Assert.Equal(0.6, stats.DurationSeconds, 3);
        Assert.True(stats.PeakThrustNewtons >= 49);
        Assert.True(stats.AverageThrustNewtons > 0);
        Assert.True(stats.TotalImpulseNewtonSeconds > 0);
        Assert.False(string.IsNullOrWhiteSpace(stats.MotorDesignation));
    }

    [Fact]
    public void CalculateIntervalStatistics_InterpolatesBoundariesAndUsesSelectedDuration()
    {
        double[] time = [0, 1, 2];
        double[] thrust = [0, 10, 20];

        var stats = RaspMotorExporter.CalculateIntervalStatistics(time, thrust, 0.25, 1.25);

        Assert.Equal(1.0, stats.DurationSeconds, 10);
        Assert.Equal(7.5, stats.TotalImpulseNewtonSeconds, 10);
        Assert.Equal(7.5, stats.AverageThrustNewtons, 10);
        Assert.Equal(12.5, stats.PeakThrustNewtons, 10);
        Assert.Equal(1, stats.SampleCount);
    }

    [Fact]
    public void CalculateIntervalStatistics_IntegratesOriginalSplineWithinCurvedInterval()
    {
        double[] time = [0, 1, 2, 3];
        double[] thrust = [0, 10, 0, 0];
        var spline = CubicSpline.InterpolateNaturalSorted(time, thrust);
        double expectedImpulse = spline.Integrate(0.5, 2.5);

        var stats = RaspMotorExporter.CalculateIntervalStatistics(time, thrust, 0.5, 2.5);

        Assert.Equal(expectedImpulse, stats.TotalImpulseNewtonSeconds, 10);
        Assert.Equal(expectedImpulse / 2, stats.AverageThrustNewtons, 10);
    }

    [Fact]
    public void ExtractDataPoints_IntermediateThrustsAreStrictlyPositiveAndFinalIsZero()
    {
        // Dataset with a dip below zero to simulate sensor baseline drift
        double[] time = [0.0, 0.1, 0.2, 0.3, 0.4, 0.5];
        double[] thrust = [0.0, 10.0, -1.5, 25.0, 5.0, 0.0];

        var points = RaspMotorExporter.ExtractDataPoints(time, thrust, 0.0, 0.5, maxPoints: null);

        Assert.NotEmpty(points);

        // All intermediate points must have thrust > 0 (ThrustCurve requirement)
        for (int i = 0; i < points.Count - 1; i++)
        {
            Assert.True(points[i].Thrust > 0, $"Point {i} thrust was {points[i].Thrust}, expected > 0.");
            Assert.True(points[i].Time > 0, $"Point {i} time was {points[i].Time}, expected > 0.");
            if (i > 0)
            {
                Assert.True(points[i].Time > points[i - 1].Time, "Points must be strictly increasing in time.");
            }
        }

        // Final point must have thrust = 0 at the burn duration
        Assert.Equal(0.0, points[^1].Thrust);
        Assert.True(points[^1].Time >= 0.5);
    }

    [Fact]
    public void ExtractDataPoints_DoesNotInventThrustForNonBurningInterval()
    {
        var points = RaspMotorExporter.ExtractDataPoints([0, 1, 2], [0, -2, 0], 0, 2);

        Assert.Empty(points);
    }

    [Fact]
    public void ExtractDataPoints_TerminalZeroRemainsAtSelectedBurnEnd()
    {
        var points = RaspMotorExporter.ExtractDataPoints([0, 0.99995, 1], [10, 10, 10], 0, 1);

        Assert.Equal(0, points[^1].Thrust);
        Assert.Equal(1, points[^1].Time, 10);
    }

    [Fact]
    public void SimplifyPoints_PreservesPeakThrustAndRespectsMaxPoints()
    {
        var dataset = CreateSampleDataset(); // 100 points, peak at t=0.5, thrust=50

        // Extract with maxPoints = 32 (classic RASP limit)
        var points = RaspMotorExporter.ExtractDataPoints(
            dataset.TimeSeconds,
            dataset.ThrustNewtons,
            0.0,
            0.99,
            maxPoints: 32);

        Assert.True(points.Count <= 32, $"Expected <= 32 points, got {points.Count}");

        // Peak thrust (50 N) must be preserved in the curve
        double maxExtractedThrust = points.Max(p => p.Thrust);
        Assert.Equal(50.0, maxExtractedThrust, 1);

        // Final point is 0 thrust
        Assert.Equal(0.0, points[^1].Thrust);
        Assert.Equal(0.99, points[^1].Time, 4);
    }

    [Fact]
    public void GenerateEngContent_ConformsToRaspSpecification()
    {
        var dataset = CreateSampleDataset();
        var parameters = new RaspMotorParameters
        {
            MotorName = "F32",
            DiameterMillimeters = 24.5,
            LengthMillimeters = 124,
            Delays = "5-10-15",
            PropellantWeightKilograms = 0.0377,
            TotalWeightKilograms = 0.0695,
            Manufacturer = "RV",
            IntervalStartSeconds = 0.0,
            IntervalEndSeconds = 0.99,
            Comments = "Sample test comment\nSecond note",
            MaxDataPoints = 32
        };

        string content = RaspMotorExporter.GenerateEngContent(dataset, parameters, "test_file.json");

        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);

        // 1. Initial lines must be comments starting with ';'
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(';'))
            {
                headerIndex = i;
                break;
            }
        }

        Assert.True(headerIndex > 0, "Expected comment lines before the header line.");

        // Comments should contain user notes
        Assert.Contains(lines, l => l.Contains("Sample test comment"));
        Assert.Contains(lines, l => l.Contains("Second note"));
        Assert.Contains(lines, l => l.Contains("Source: test_file.json"));

        // 2. The Header line must have exactly 7 space-separated tokens
        string headerLine = lines[headerIndex];
        string[] tokens = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, tokens.Length);

        Assert.Equal("F32", tokens[0]);
        Assert.Equal("24.5", tokens[1]);
        Assert.Equal("124", tokens[2]);
        Assert.Equal("5-10-15", tokens[3]);
        Assert.Equal("0.0377", tokens[4]);
        Assert.Equal("0.0695", tokens[5]);
        Assert.Equal("RV", tokens[6]);

        // 3. Data points immediately follow header
        // All data points before the final point must have thrust > 0
        int dataStartIndex = headerIndex + 1;
        int dataEndIndex = lines.Length - 1; // last line should be ';'

        Assert.Equal(";", lines[^1].Trim());

        for (int i = dataStartIndex; i < dataEndIndex - 1; i++)
        {
            string[] pointParts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, pointParts.Length);

            double time = double.Parse(pointParts[0], CultureInfo.InvariantCulture);
            double thrust = double.Parse(pointParts[1], CultureInfo.InvariantCulture);

            Assert.True(time > 0, $"Data point time {time} must be > 0");
            Assert.True(thrust > 0, $"Intermediate thrust {thrust} must be > 0");
        }

        // Final data point before trailing ';' must have thrust = 0
        string[] finalPointParts = lines[dataEndIndex - 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, finalPointParts.Length);
        double finalThrust = double.Parse(finalPointParts[1], CultureInfo.InvariantCulture);
        Assert.Equal(0.0, finalThrust);
    }

    [Fact]
    public void ExportToFile_WritesValidFileToDisk()
    {
        var dataset = CreateSampleDataset();
        var parameters = new RaspMotorParameters
        {
            MotorName = "MSP-G50",
            DiameterMillimeters = 29,
            LengthMillimeters = 150,
            Delays = "P",
            PropellantWeightKilograms = 0.080,
            TotalWeightKilograms = 0.150,
            Manufacturer = "MSP",
            IntervalStartSeconds = 0.05,
            IntervalEndSeconds = 0.95
        };

        string tempPath = Path.Combine(Path.GetTempPath(), $"test_motor_{Guid.NewGuid():N}.eng");
        try
        {
            RaspMotorExporter.ExportToFile(tempPath, dataset, parameters, "test.json");

            Assert.True(File.Exists(tempPath));
            string readContent = File.ReadAllText(tempPath);

            Assert.Contains("MSP-G50 29 150 P 0.08 0.15 MSP", readContent);
            Assert.EndsWith(";" + Environment.NewLine, readContent);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Sanitize_HandlesSpacesInMotorNameAndManufacturer()
    {
        var dataset = CreateSampleDataset();
        var parameters = new RaspMotorParameters
        {
            MotorName = "MSP F 32 Custom",
            DiameterMillimeters = 29,
            LengthMillimeters = 124,
            Delays = "6, 10, 14",
            PropellantWeightKilograms = 0.05,
            TotalWeightKilograms = 0.1,
            Manufacturer = "Missouri Space Program",
            IntervalStartSeconds = 0.0,
            IntervalEndSeconds = 0.99
        };

        string content = RaspMotorExporter.GenerateEngContent(dataset, parameters);
        var lines = content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        // Find header line
        string headerLine = lines.First(l => !l.StartsWith(';'));
        string[] tokens = headerLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(7, tokens.Length);
        Assert.Equal("MSP-F-32-Custom", tokens[0]);
        Assert.Equal("6-10-14", tokens[3]);
        Assert.Equal("Missouri-Space-Program", tokens[6]);
    }
}
