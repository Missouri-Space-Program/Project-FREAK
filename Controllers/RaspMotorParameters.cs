namespace Project_FREAK.Controllers;

/// <summary>
/// Parameters for exporting motor test data to the RASP / RockSim .eng format.
/// Specification reference: https://www.thrustcurve.org/info/raspformat.html
/// </summary>
public sealed class RaspMotorParameters
{
    /// <summary>
    /// Common name of the motor (e.g., F32 or MSP-F32). Must not contain spaces.
    /// </summary>
    public string MotorName { get; set; } = string.Empty;

    /// <summary>
    /// Casing outer diameter in millimeters (mm).
    /// </summary>
    public double DiameterMillimeters { get; set; } = 29;

    /// <summary>
    /// Casing overall length in millimeters (mm).
    /// </summary>
    public double LengthMillimeters { get; set; } = 124;

    /// <summary>
    /// Available delay times separated by dashes (e.g., "P" for plugged, "0" for none, or "4-8-12").
    /// </summary>
    public string Delays { get; set; } = "P";

    /// <summary>
    /// Consumables / propellant mass in kilograms (kg).
    /// </summary>
    public double PropellantWeightKilograms { get; set; } = 0.050;

    /// <summary>
    /// Total motor loaded mass ready for flight in kilograms (kg).
    /// </summary>
    public double TotalWeightKilograms { get; set; } = 0.100;

    /// <summary>
    /// Motor manufacturer abbreviation (e.g., MSP, AT, CTI, EST). Must not contain spaces.
    /// </summary>
    public string Manufacturer { get; set; } = "MSP";

    /// <summary>
    /// Optional user notes or test comments to include in header comment lines.
    /// </summary>
    public string Comments { get; set; } = string.Empty;

    /// <summary>
    /// Selected interval start time in seconds.
    /// </summary>
    public double IntervalStartSeconds { get; set; }

    /// <summary>
    /// Selected interval end time in seconds.
    /// </summary>
    public double IntervalEndSeconds { get; set; }

    /// <summary>
    /// Maximum data points to output (null or <= 0 for full resolution / all points).
    /// Classic RASP limit is 32 points; modern simulators comfortably support 100-500 points.
    /// </summary>
    public int? MaxDataPoints { get; set; } = 500;

    /// <summary>
    /// Clones this parameter set.
    /// </summary>
    public RaspMotorParameters Clone() => (RaspMotorParameters)MemberwiseClone();
}
