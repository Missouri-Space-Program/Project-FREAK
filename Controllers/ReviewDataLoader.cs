using System.IO;
using System.Globalization;
using System.Text.Json;

namespace Project_FREAK.Controllers;

public sealed record ReviewDataset(
    double[] TimeSeconds,
    double[] ThrustNewtons,
    double[] PressurePsi,
    string? VideoPath,
    string ConversionDescription);

public static class ReviewDataLoader
{
    public static ReviewDataset Load(string jsonPath)
    {
        return Load(jsonPath, new SettingsManager.SettingsData());
    }

    public static ReviewDataset Load(string jsonPath, SettingsManager.SettingsData settings)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            throw new FileNotFoundException("The selected review JSON file was not found.", jsonPath);
        ArgumentNullException.ThrowIfNull(settings);

        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The review JSON must contain an object.");

        double[] time = ReadArray(root, "time_values_seconds")
            ?? throw new InvalidDataException("The JSON file does not contain time_values_seconds.");
        double[]? rawThrust = ReadArray(root, "load_cell_amplifier_output_voltages_v")
            ?? ReadArray(root, "load_cell_voltages_mv");
        double[]? rawPressure = ReadArray(root, "pressure_transducer_voltages_v");
        double[]? rawExcitation = ReadArray(root, "load_cell_excitation_voltages_v");
        double[]? storedThrust = ReadArray(root, "thrust_values_N");
        double[]? storedPressure = ReadArray(root, "pressure_values_PSI");
        bool useRawThrust = settings.UseRawDataForReview && rawThrust is { Length: > 0 };
        bool useRawPressure = settings.UseRawDataForReview && rawPressure is { Length: > 0 };
        double[]? thrust = useRawThrust ? ConvertRawThrust(rawThrust!, rawExcitation, settings) : storedThrust;
        double[]? pressure = useRawPressure
            ? rawPressure!
                .Select(voltage => SensorCalibration.ScalePressure(
                    voltage,
                    settings.PressureZeroVoltage,
                    settings.PressureFullScaleVoltage,
                    settings.PressureFullScaleValue))
                .ToArray()
            : storedPressure;
        var conversions = new List<string>();

        if (useRawThrust)
        {
            conversions.Add(settings.LoadCellCalibrationEnabled
                ? rawExcitation is { Length: > 0 }
                    ? $"raw load-cell values use applied calibration settings: slope {FormatSetting(settings.LoadCellCalibrationSlope)}, offset {FormatSetting(settings.LoadCellCalibrationOffset)}, and per-sample excitation voltages"
                    : $"raw load-cell values use applied calibration settings: slope {FormatSetting(settings.LoadCellCalibrationSlope)}, offset {FormatSetting(settings.LoadCellCalibrationOffset)}, reference excitation {FormatSetting(settings.LoadCellCalibrationExcitationVoltage)} V (this JSON has no per-sample excitation series)"
                : "raw load-cell values use the legacy load-cell calibration (no applied load-cell calibration is enabled)");
        }
        else if (storedThrust is not null)
        {
            conversions.Add("stored unitized thrust values in newtons");
        }

        if (useRawPressure)
        {
            conversions.Add($"raw pressure values use applied settings: zero {FormatSetting(settings.PressureZeroVoltage)} V, full scale {FormatSetting(settings.PressureFullScaleVoltage)} V = {FormatSetting(settings.PressureFullScaleValue)} PSI");
        }
        else if (storedPressure is not null)
        {
            conversions.Add("stored unitized pressure values in PSI");
        }

        if (time.Length == 0 || thrust is null || pressure is null)
            throw new InvalidDataException("The JSON file must include time, thrust, and pressure data.");

        int length = Math.Min(time.Length, Math.Min(thrust.Length, pressure.Length));
        if (length == 0)
            throw new InvalidDataException("The JSON file does not contain complete time-series data.");

        Array.Resize(ref time, length);
        Array.Resize(ref thrust, length);
        Array.Resize(ref pressure, length);
        for (int index = 1; index < time.Length; index++)
        {
            if (time[index] < time[index - 1])
                throw new InvalidDataException("The time_values_seconds array must be in ascending order.");
        }

        return new ReviewDataset(
            time,
            thrust,
            pressure,
            ResolveVideoPath(root, jsonPath),
            settings.UseRawDataForReview
                ? $"Data source (raw-data mode enabled): {string.Join("; ", conversions)}."
                : $"Data source (default): {string.Join("; ", conversions)}. Unitized JSON values are used; enable raw-data mode in General Settings to recalculate from sensor voltages.");
    }

    private static double[] ConvertRawThrust(
        double[] rawThrust,
        double[]? rawExcitation,
        SettingsManager.SettingsData settings)
    {
        if (settings.LoadCellCalibrationEnabled)
        {
            if (!double.IsFinite(settings.LoadCellCalibrationSlope) ||
                !double.IsFinite(settings.LoadCellCalibrationOffset) ||
                !double.IsFinite(settings.LoadCellCalibrationExcitationVoltage) ||
                settings.LoadCellCalibrationExcitationVoltage <= 0)
            {
                throw new InvalidDataException("Applied load-cell calibration is enabled, but its slope, offset, or reference excitation voltage is invalid.");
            }

            var calibration = new LoadCellCalibration(
                settings.LoadCellCalibrationSlope,
                settings.LoadCellCalibrationOffset,
                settings.LoadCellCalibrationExcitationVoltage);

            int sampleCount = rawExcitation is { Length: > 0 }
                ? Math.Min(rawThrust.Length, rawExcitation.Length)
                : rawThrust.Length;
            var thrustNewtons = new double[sampleCount];
            for (int index = 0; index < sampleCount; index++)
            {
                double excitationVoltage = rawExcitation is { Length: > 0 }
                    ? rawExcitation[index]
                    : calibration.ExcitationReferenceVoltage;
                if (!double.IsFinite(excitationVoltage) || excitationVoltage <= 0)
                    throw new InvalidDataException($"The load-cell excitation voltage at sample {index} must be positive and finite.");

                thrustNewtons[index] = SensorCalibration.LoadKilograms(
                    calibration,
                    (rawThrust[index] - 1.25) / 201,
                    excitationVoltage) * SensorCalibration.StandardGravityMetersPerSecondSquared;
            }

            return thrustNewtons;
        }

        return rawThrust
            .Select(voltage => (100387.5 * ((voltage - 1.25) / 201) - 3.8069375) * SensorCalibration.StandardGravityMetersPerSecondSquared)
            .ToArray();
    }

    private static string FormatSetting(double value) => value.ToString("G6", CultureInfo.CurrentCulture);

    private static double[]? ReadArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The {propertyName} field must be an array.");

        var values = new List<double>();
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double value) || !double.IsFinite(value))
                throw new InvalidDataException($"The {propertyName} field contains a non-numeric value.");
            values.Add(value);
        }
        return values.ToArray();
    }

    private static string? ResolveVideoPath(JsonElement root, string jsonPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(jsonPath))!;
        if (root.TryGetProperty("video_path", out JsonElement videoPathElement) &&
            videoPathElement.ValueKind == JsonValueKind.String)
        {
            string? videoPath = videoPathElement.GetString();
            if (!string.IsNullOrWhiteSpace(videoPath))
            {
                string candidate = Path.IsPathRooted(videoPath) ? videoPath : Path.Combine(directory, videoPath);
                if (File.Exists(candidate) && string.Equals(Path.GetExtension(candidate), ".mp4", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(candidate);
            }
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
