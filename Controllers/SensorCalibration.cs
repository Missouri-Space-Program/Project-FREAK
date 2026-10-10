namespace Project_FREAK.Controllers;

public readonly record struct LoadCellCalibration(double Slope, double Offset, double ExcitationReferenceVoltage);

public static class SensorCalibration
{
    public static LoadCellCalibration FitLoadCell(
        double weight1Kg,
        double bridgeVoltage1,
        double excitationVoltage1,
        double weight2Kg,
        double bridgeVoltage2,
        double excitationVoltage2)
    {
        if (!double.IsFinite(weight1Kg) || !double.IsFinite(weight2Kg) ||
            !double.IsFinite(bridgeVoltage1) || !double.IsFinite(bridgeVoltage2) ||
            !double.IsFinite(excitationVoltage1) || !double.IsFinite(excitationVoltage2) ||
            excitationVoltage1 <= 0 || excitationVoltage2 <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(excitationVoltage1), "Calibration samples must be finite and excitation voltages must be positive.");
        }
        if (Math.Abs(weight2Kg - weight1Kg) < 1e-12)
        {
            throw new ArgumentException("The two calibration loads must have different known masses.");
        }

        double excitationReference = (excitationVoltage1 + excitationVoltage2) / 2;
        double normalizedVoltage1 = bridgeVoltage1 * excitationReference / excitationVoltage1;
        double normalizedVoltage2 = bridgeVoltage2 * excitationReference / excitationVoltage2;
        double voltageDifference = normalizedVoltage2 - normalizedVoltage1;
        if (Math.Abs(voltageDifference) < 1e-12)
        {
            throw new ArgumentException("The two calibration samples must have different bridge voltages.");
        }

        double slope = (weight2Kg - weight1Kg) / voltageDifference;
        double offset = weight1Kg - slope * normalizedVoltage1;
        return new LoadCellCalibration(slope, offset, excitationReference);
    }

    public static double LoadKilograms(LoadCellCalibration calibration, double bridgeVoltage, double excitationVoltage)
    {
        if (!double.IsFinite(bridgeVoltage) || !double.IsFinite(excitationVoltage) || excitationVoltage <= 0 || calibration.ExcitationReferenceVoltage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(excitationVoltage), "A finite bridge voltage and positive excitation voltage are required.");
        }

        return (calibration.Slope * bridgeVoltage + calibration.Offset) * excitationVoltage / calibration.ExcitationReferenceVoltage;
    }

    public static double ScalePressure(double voltage, double zeroVoltage, double fullScaleVoltage, double fullScaleValue)
    {
        if (!double.IsFinite(voltage) || !double.IsFinite(zeroVoltage) || !double.IsFinite(fullScaleVoltage) ||
            !double.IsFinite(fullScaleValue) || fullScaleVoltage <= zeroVoltage || fullScaleValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fullScaleVoltage), "Pressure calibration requires finite values, a higher full-scale voltage, and a positive full-scale value.");
        }

        return (voltage - zeroVoltage) * fullScaleValue / (fullScaleVoltage - zeroVoltage);
    }
}
