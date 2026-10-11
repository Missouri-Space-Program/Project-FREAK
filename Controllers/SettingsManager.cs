using System;
using System.IO;
using System.Text.Json;
using System.ComponentModel;

namespace Project_FREAK.Controllers;

public class SettingsManager : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? AppliedSettingsChanged;

    // Paths to the settings files

    private static readonly string DefaultSavedSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Project-FREAK", "saved_settings.json");

    private readonly string SavedSettingsPath;

    // Settings data
    private SettingsData _appliedSettings = new();
    public SettingsData AppliedSettings
    {
        get => _appliedSettings;
        private set
        {
            _appliedSettings = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppliedSettings))); // Notify when AppliedSettings changes

        }
    }

    // True while a recording is active. Apply and Save are blocked so a recording keeps one calibration and configuration.
    public bool IsLockedForRecording { get; private set; }

    public void SetRecordingLock(bool isLocked) => IsLockedForRecording = isLocked;

    public SettingsManager() : this(DefaultSavedSettingsPath)
    {
    }

    public SettingsManager(string savedSettingsPath)
    {
        SavedSettingsPath = savedSettingsPath;
        LoadSavedSettings();
    }

    // Loads saved settings from disk
    public void LoadSavedSettings()
    {
        try
        {
            if (File.Exists(SavedSettingsPath))
            {
                var json = File.ReadAllText(SavedSettingsPath);
                var settings = JsonSerializer.Deserialize<SettingsData>(json);
                AppliedSettings = (settings ?? new SettingsData()).NormalizeChannels(); // Set AppliedSettings to the loaded settings or a new instance if null

            }
        }
        catch { /* Handle errors */ }
    }


    // Saves the applied settings to disk
    public void SaveAppliedSettingsToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(AppliedSettings);
            Directory.CreateDirectory(Path.GetDirectoryName(SavedSettingsPath)!); // Ensure the directory exists

            File.WriteAllText(SavedSettingsPath, json);
        }
        catch { /* Handle errors */ }
    }

    // Updates the applied settings for this session only. Nothing is written to disk here; use SaveAppliedSettingsToDisk to persist.
    public void UpdateAppliedSettings(SettingsData newSettings)
    {
        AppliedSettings = newSettings.Clone().NormalizeChannels();
        AppliedSettingsChanged?.Invoke(this, EventArgs.Empty); // Trigger the AppliedSettingsChanged event
    }

    // Class representing the settings data
    public class SettingsData
    {
        public bool DemoModeEnabled { get; set; } // Legacy flag; current demo mode is selected when a LabJack connection is unavailable.
        public bool UseRawDataForReview { get; set; } // Recalculate review values from recorded sensor voltages instead of stored engineering units.
        public string RtspUrl { get; set; } = "rtsp://admin:MSPMOTORTEST2025@192.168.20.4:554/cam/realmonitor?channel=1&subtype=0"; // Camera stream URL used by the recording view.
        public bool LoadCellCalibrationEnabled { get; set; } // Use the saved two-point load-cell calibration instead of the fallback scale.
        public double LoadCellCalibrationSlope { get; set; } // Fitted kilograms per normalized bridge-signal volt.
        public double LoadCellCalibrationOffset { get; set; } // Fitted load in kilograms at zero normalized bridge signal.
        public double LoadCellCalibrationExcitationVoltage { get; set; } // Mean calibration excitation voltage used as the reference.
        public double LoadCellFullScaleValue { get; set; } = 500; // Rated load-cell capacity in the configured unit.
        public string LoadCellFullScaleUnit { get; set; } = "kg"; // Rated capacity unit: kg or N.
        public double LoadCellPoint1WeightKg { get; set; } // Known mass for calibration point 1, in kilograms.
        public double LoadCellPoint1BridgeVoltage { get; set; } // Captured conditioned bridge signal for point 1, in volts.
        public double LoadCellPoint1ExcitationVoltage { get; set; } // Excitation voltage measured when point 1 was captured.
        public double LoadCellPoint2WeightKg { get; set; } // Known mass for calibration point 2, in kilograms.
        public double LoadCellPoint2BridgeVoltage { get; set; } // Captured conditioned bridge signal for point 2, in volts.
        public double LoadCellPoint2ExcitationVoltage { get; set; } // Excitation voltage measured when point 2 was captured.
        public double PressureZeroVoltage { get; set; } = 0.5; // Sensor output voltage at zero pressure, in volts.
        public double PressureFullScaleVoltage { get; set; } = 4.5; // Sensor output voltage at its rated full-scale pressure, in volts.
        public double PressureFullScaleValue { get; set; } = 1600; // Pressure represented by PressureFullScaleVoltage, in PSI.
        public string PressureInputChannel { get; set; } = "AIN0"; // LabJack analog input carrying the pressure sensor signal.
        public string LoadCellInputChannel { get; set; } = "AIN5"; // LabJack analog input carrying the conditioned load-cell signal.
        public string IgniterOutputChannel { get; set; } = "DAC0"; // LabJack analog output connected to the igniter control circuit.
        public int SamplingRateHz { get; set; } = 1000; // Sensor sampling rate in Hz (e.g. 100, 250, 500, 1000, 2000).
        public int LiveGraphWindowSeconds { get; set; } = 15; // Relative time window duration in seconds for live graphs (e.g. 5, 10, 15, 30, 60).

        public SettingsData Clone() => (SettingsData)MemberwiseClone();

        public double GetLoadCellFullScaleNewtons() =>
            LoadCellFullScaleUnit == "N" ? LoadCellFullScaleValue : LoadCellFullScaleValue * 9.80665;

        public SettingsData NormalizeChannels()
        {
            if (!IsAnalogInputChannel(PressureInputChannel) || PressureInputChannel == "AIN6")
                PressureInputChannel = "AIN0";
            if (!IsAnalogInputChannel(LoadCellInputChannel) ||
                LoadCellInputChannel == "AIN6" ||
                LoadCellInputChannel == PressureInputChannel)
            {
                LoadCellInputChannel = PressureInputChannel == "AIN5" ? "AIN0" : "AIN5";
            }
            if (IgniterOutputChannel is not ("DAC0" or "DAC1"))
                IgniterOutputChannel = "DAC0";
            if (!double.IsFinite(LoadCellFullScaleValue) || LoadCellFullScaleValue <= 0)
                LoadCellFullScaleValue = 500;
            if (LoadCellFullScaleUnit is not ("kg" or "N"))
                LoadCellFullScaleUnit = "kg";
            if (SamplingRateHz is < 10 or > 10000)
                SamplingRateHz = 1000;
            if (LiveGraphWindowSeconds is < 2 or > 300)
                LiveGraphWindowSeconds = 15;

            return this;
        }

        private static bool IsAnalogInputChannel(string? channel)
        {
            if (channel is null || !channel.StartsWith("AIN", StringComparison.Ordinal))
                return false;

            return int.TryParse(channel.AsSpan(3), out int inputNumber) && inputNumber is >= 0 and <= 13;
        }
    }
}
