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

    private static readonly string SavedSettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Assets", "CONFIG", "saved_settings.json");

    private static readonly string AppliedSettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "Assets", "CONFIG", "applied_settings.json");

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

    public SettingsManager()
    {
        LoadSavedSettings();
        LoadAppliedSettings();
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

    // Updates the applied settings and saves them to disk
    public void UpdateAppliedSettings(SettingsData newSettings)
    {
        AppliedSettings = newSettings.Clone().NormalizeChannels();
        AppliedSettingsChanged?.Invoke(this, EventArgs.Empty); // Trigger the AppliedSettingsChanged event

        // Save applied settings to session file
        try
        {
            var json = JsonSerializer.Serialize(AppliedSettings);
            Directory.CreateDirectory(Path.GetDirectoryName(AppliedSettingsPath)!); // Ensure the directory exists
            File.WriteAllText(AppliedSettingsPath, json);
        }
        catch { /* Handle errors */ }
    }

    // Loads the applied settings from disk
    public void LoadAppliedSettings()
    {
        try
        {
            if (File.Exists(AppliedSettingsPath))
            {
                var json = File.ReadAllText(AppliedSettingsPath);
                var settings = JsonSerializer.Deserialize<SettingsData>(json);
                AppliedSettings = (settings ?? new SettingsData()).NormalizeChannels(); // Set AppliedSettings to the loaded settings or a new instance if null
            }
        }
        catch { /* Handle errors */ }
    }

    // Class representing the settings data
    public class SettingsData
    {
        public bool DemoModeEnabled { get; set; } // Indicates if demo mode is enabled
        public bool UseRawDataForReview { get; set; } // Recalculate review data from raw sensor voltages instead of stored engineering units
        public string RtspUrl { get; set; } = "rtsp://admin:MSPMOTORTEST2025@192.168.20.4:554/cam/realmonitor?channel=1&subtype=0"; // Default RTSP URL
        public bool LoadCellCalibrationEnabled { get; set; }
        public double LoadCellCalibrationSlope { get; set; }
        public double LoadCellCalibrationOffset { get; set; }
        public double LoadCellCalibrationExcitationVoltage { get; set; }
        public double LoadCellPoint1WeightKg { get; set; }
        public double LoadCellPoint1BridgeVoltage { get; set; }
        public double LoadCellPoint1ExcitationVoltage { get; set; }
        public double LoadCellPoint2WeightKg { get; set; }
        public double LoadCellPoint2BridgeVoltage { get; set; }
        public double LoadCellPoint2ExcitationVoltage { get; set; }
        public double PressureZeroVoltage { get; set; } = 0.5;
        public double PressureFullScaleVoltage { get; set; } = 4.5;
        public double PressureFullScaleValue { get; set; } = 1600;
        public string PressureInputChannel { get; set; } = "AIN0";
        public string LoadCellInputChannel { get; set; } = "AIN5";
        public string IgniterOutputChannel { get; set; } = "DAC0";

        public SettingsData Clone() => (SettingsData)MemberwiseClone();

        public SettingsData NormalizeChannels()
        {
            if (!IsAnalogInputChannel(PressureInputChannel))
                PressureInputChannel = "AIN0";
            if (!IsAnalogInputChannel(LoadCellInputChannel))
                LoadCellInputChannel = "AIN5";
            if (IgniterOutputChannel is not ("DAC0" or "DAC1"))
                IgniterOutputChannel = "DAC0";

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
