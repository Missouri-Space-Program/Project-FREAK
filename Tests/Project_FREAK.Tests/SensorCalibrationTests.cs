using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests;

public class SensorCalibrationTests
{
    [Fact]
    public void FitLoadCell_CalculatesTwoPointLineAndExcitationCompensation()
    {
        var calibration = SensorCalibration.FitLoadCell(0, 0.001, 2.5, 10, 0.003, 2.5);

        Assert.Equal(5000, calibration.Slope, 6);
        Assert.Equal(-5, calibration.Offset, 6);
        Assert.Equal(2.5, calibration.ExcitationReferenceVoltage, 6);
        Assert.Equal(5, SensorCalibration.LoadKilograms(calibration, 0.002, 2.5), 6);
    }

    [Fact]
    public void FitLoadCell_NormalizesSamplesToCommonExcitationVoltage()
    {
        var calibration = SensorCalibration.FitLoadCell(0, 0.001, 2.5, 10, 0.006, 5.0);

        Assert.Equal(10, SensorCalibration.LoadKilograms(calibration, 0.0045, 3.75), 6);
    }

    [Fact]
    public void LoadKilograms_AtLiveExcitation_NormalizesBridgeVoltageToReference()
    {
        var calibration = SensorCalibration.FitLoadCell(0, 0.001, 2.5, 10, 0.006, 5.0);

        // Bridge output is proportional to excitation: 0.006 V at 5.0 V equals 0.0045 V at 3.75 V.
        Assert.Equal(10, SensorCalibration.LoadKilograms(calibration, 0.006, 5.0), 6);
        Assert.Equal(0, SensorCalibration.LoadKilograms(calibration, 0.002, 5.0), 6);
    }

    [Fact]
    public void FitLoadCell_RejectsDuplicateVoltageSamples()
    {
        Assert.Throws<ArgumentException>(() => SensorCalibration.FitLoadCell(0, 0.001, 2.5, 10, 0.001, 2.5));
    }

    [Fact]
    public void ScalePressure_MapsConfiguredEndpoints()
    {
        Assert.Equal(0, SensorCalibration.ScalePressure(0.5, 0.5, 4.5, 1600), 6);
        Assert.Equal(800, SensorCalibration.ScalePressure(2.5, 0.5, 4.5, 1600), 6);
        Assert.Equal(1600, SensorCalibration.ScalePressure(4.5, 0.5, 4.5, 1600), 6);
    }
}
