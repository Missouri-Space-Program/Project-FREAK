using System.IO;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class ReviewDataLoaderTests
{
    [Fact]
    public void Load_UsesAppliedSettingsForRawDataAndResolvesRelativeVideoPath()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "burn.mp4"), string.Empty);
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, """
                {
                  "time_values_seconds": [0, 1, 2],
                  "thrust_values_N": [10, 20, 30],
                  "pressure_values_PSI": [100, 200, 300],
                  "load_cell_voltages_mv": [5.27, 7.28, 9.29],
                  "pressure_transducer_voltages_v": [1, 3, 5],
                  "video_path": "burn.mp4"
                }
                """);

            var settings = new SettingsManager.SettingsData
            {
                UseRawDataForReview = true,
                LoadCellCalibrationEnabled = true,
                LoadCellCalibrationSlope = 100,
                LoadCellCalibrationOffset = 2,
                LoadCellCalibrationExcitationVoltage = 2.5,
                PressureZeroVoltage = 1,
                PressureFullScaleVoltage = 5,
                PressureFullScaleValue = 100
            };
            var dataset = ReviewDataLoader.Load(jsonPath, settings);
            string conversionDescription = dataset.ConversionDescription;

            Assert.Equal(new[] { 0d, 1d, 2d }, dataset.TimeSeconds);
            Assert.Equal(39.24, dataset.ThrustNewtons[0], 6);
            Assert.Equal(49.05, dataset.ThrustNewtons[1], 6);
            Assert.Equal(58.86, dataset.ThrustNewtons[2], 6);
            Assert.Equal(0, dataset.PressurePsi[0], 6);
            Assert.Equal(50, dataset.PressurePsi[1], 6);
            Assert.Equal(100, dataset.PressurePsi[2], 6);
            Assert.Equal(Path.Combine(folder, "burn.mp4"), dataset.VideoPath);
            Assert.Contains("applied calibration settings", conversionDescription);
            Assert.Contains("applied settings", conversionDescription);
            Assert.Contains("slope 100", conversionDescription);
            Assert.Contains("offset 2", conversionDescription);
            Assert.Contains("reference excitation 2.5 V", conversionDescription);
            Assert.Contains("zero 1 V", conversionDescription);
            Assert.Contains("full scale 5 V = 100 PSI", conversionDescription);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_RecalculatesSameFileWhenRawDataSettingChanges()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, """
                {
                  "time_values_seconds": [0, 1],
                  "thrust_values_N": [10, 20],
                  "pressure_values_PSI": [100, 200],
                  "load_cell_voltages_mv": [1.25, 1.27],
                  "pressure_transducer_voltages_v": [0.5, 2.5]
                }
                """);

            var storedDataset = ReviewDataLoader.Load(jsonPath, new SettingsManager.SettingsData());
            var rawDataset = ReviewDataLoader.Load(jsonPath, new SettingsManager.SettingsData { UseRawDataForReview = true });

            Assert.Equal(new[] { 10d, 20d }, storedDataset.ThrustNewtons);
            Assert.Equal(new[] { 100d, 200d }, storedDataset.PressurePsi);
            Assert.NotEqual(storedDataset.ThrustNewtons[0], rawDataset.ThrustNewtons[0]);
            Assert.NotEqual(storedDataset.PressurePsi[0], rawDataset.PressurePsi[0]);
            Assert.Contains("raw-data mode enabled", rawDataset.ConversionDescription);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_ConvertsLegacyRawVoltagesAndTrimsToShortestSeries()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, """
                {
                  "time_values_seconds": [0, 1, 2],
                  "load_cell_voltages_mv": [1.25, 1.27],
                  "pressure_transducer_voltages_v": [0.5, 2.5, 4.5]
                }
                """);

            var dataset = ReviewDataLoader.Load(jsonPath, new SettingsManager.SettingsData { UseRawDataForReview = true });

            Assert.Equal(new[] { 0d, 1d }, dataset.TimeSeconds);
            Assert.Equal(2, dataset.ThrustNewtons.Length);
            Assert.Equal(2, dataset.PressurePsi.Length);
            Assert.Equal((100387.5 * ((1.25 - 1.25) / 201) - 3.8069375) * 9.81, dataset.ThrustNewtons[0], 6);
            Assert.Equal(0, dataset.PressurePsi[0], 6);
            Assert.Equal(800, dataset.PressurePsi[1], 6);
            Assert.Contains("legacy load-cell calibration", dataset.ConversionDescription);
            Assert.Null(dataset.VideoPath);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_RejectsInvalidEnabledLoadCellCalibrationInsteadOfUsingLegacyValues()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, """
                {
                  "time_values_seconds": [0],
                  "load_cell_voltages_mv": [1.25],
                  "pressure_transducer_voltages_v": [0.5]
                }
                """);

            var settings = new SettingsManager.SettingsData
            {
                UseRawDataForReview = true,
                LoadCellCalibrationEnabled = true,
                LoadCellCalibrationSlope = double.NaN,
                LoadCellCalibrationOffset = 0,
                LoadCellCalibrationExcitationVoltage = 2.5
            };

            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ReviewDataLoader.Load(jsonPath, settings));
            Assert.Contains("Applied load-cell calibration is enabled", exception.Message);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_PrefersStoredUnitizedValuesByDefault()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, """
                {
                  "time_values_seconds": [0, 1],
                  "thrust_values_N": [10, 20],
                  "pressure_values_PSI": [100, 200],
                  "load_cell_voltages_mv": [5.27, 7.28],
                  "pressure_transducer_voltages_v": [1, 3]
                }
                """);

            var dataset = ReviewDataLoader.Load(jsonPath);

            Assert.Equal(new[] { 10d, 20d }, dataset.ThrustNewtons);
            Assert.Equal(new[] { 100d, 200d }, dataset.PressurePsi);
            Assert.Contains("Data source (default)", dataset.ConversionDescription);
            Assert.Contains("Unitized JSON values are used", dataset.ConversionDescription);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_RequiresUnitizedValuesWhenRawModeIsDisabled()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, """
                {
                  "time_values_seconds": [0, 1],
                  "load_cell_voltages_mv": [5.27, 7.28],
                  "pressure_transducer_voltages_v": [1, 3]
                }
                """);

            Assert.Throws<InvalidDataException>(() => ReviewDataLoader.Load(jsonPath));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_RejectsMissingRequiredSeries()
    {
        string folder = CreateTemporaryFolder();
        try
        {
            string jsonPath = Path.Combine(folder, "data.json");
            File.WriteAllText(jsonPath, "{\"time_values_seconds\":[0]}");

            Assert.Throws<InvalidDataException>(() => ReviewDataLoader.Load(jsonPath));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string CreateTemporaryFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
