using System;
using System.IO;
using System.Text.Json;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests
{
    public class SettingsManagerTests : IDisposable
    {
        readonly string cfg = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Assets", "CONFIG");

        public SettingsManagerTests()
        {
            if (Directory.Exists(cfg)) Directory.Delete(cfg, true);
        }

        public void Dispose()
        {
            if (Directory.Exists(cfg)) Directory.Delete(cfg, true);
        }

        [Fact]
        public void DefaultConstructor_NoFiles_AppliedSettingsHaveDefaults()
        {
            var mgr = new SettingsManager();
            Assert.False(mgr.AppliedSettings.DemoModeEnabled);
            Assert.StartsWith("rtsp://admin", mgr.AppliedSettings.RtspUrl);
            Assert.Equal("AIN0", mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal("AIN5", mgr.AppliedSettings.LoadCellInputChannel);
            Assert.Equal("DAC0", mgr.AppliedSettings.IgniterOutputChannel);
            Assert.Equal(500, mgr.AppliedSettings.LoadCellFullScaleValue);
            Assert.Equal("kg", mgr.AppliedSettings.LoadCellFullScaleUnit);
            Assert.Equal(500 * 9.80665, mgr.AppliedSettings.GetLoadCellFullScaleNewtons());
        }

        [Fact]
        public void Constructor_LoadsSavedSettings_WhenSavedSettingsExist()
        {
            Directory.CreateDirectory(cfg);
            var exp = new SettingsManager.SettingsData
            {
                DemoModeEnabled = true,
                RtspUrl = "rtsp://test",
                PressureInputChannel = "AIN2",
                LoadCellInputChannel = "AIN4",
                IgniterOutputChannel = "DAC1"
            };
            File.WriteAllText(
                Path.Combine(cfg, "saved_settings.json"),
                JsonSerializer.Serialize(exp)
            );

            var mgr = new SettingsManager();
            Assert.True(mgr.AppliedSettings.DemoModeEnabled);
            Assert.Equal("rtsp://test", mgr.AppliedSettings.RtspUrl);
            Assert.Equal("AIN2", mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal("AIN4", mgr.AppliedSettings.LoadCellInputChannel);
            Assert.Equal("DAC1", mgr.AppliedSettings.IgniterOutputChannel);
        }

        [Fact]
        public void UpdateAppliedSettings_InvalidChannelsFallBackToCurrentWiringDefaults()
        {
            var mgr = new SettingsManager();
            mgr.UpdateAppliedSettings(new SettingsManager.SettingsData
            {
                PressureInputChannel = "AIN99",
                LoadCellInputChannel = "not-an-input",
                IgniterOutputChannel = "AIN3"
            });

            Assert.Equal("AIN0", mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal("AIN5", mgr.AppliedSettings.LoadCellInputChannel);
            Assert.Equal("DAC0", mgr.AppliedSettings.IgniterOutputChannel);
        }

        [Fact]
        public void UpdateAppliedSettings_NormalizesInvalidLoadCellRangeAndConvertsNewtons()
        {
            var mgr = new SettingsManager();
            mgr.UpdateAppliedSettings(new SettingsManager.SettingsData
            {
                LoadCellFullScaleValue = double.NaN,
                LoadCellFullScaleUnit = "lb"
            });

            Assert.Equal(500, mgr.AppliedSettings.LoadCellFullScaleValue);
            Assert.Equal("kg", mgr.AppliedSettings.LoadCellFullScaleUnit);

            mgr.UpdateAppliedSettings(new SettingsManager.SettingsData
            {
                LoadCellFullScaleValue = 5000,
                LoadCellFullScaleUnit = "N"
            });

            Assert.Equal(5000, mgr.AppliedSettings.GetLoadCellFullScaleNewtons());
        }

        [Fact]
        public void UpdateAppliedSettings_RaisesEventAndWritesFiles()
        {
            Directory.CreateDirectory(cfg);
            var mgr = new SettingsManager();
            bool fired = false;
            mgr.AppliedSettingsChanged += (_, __) => fired = true;

            var ns = new SettingsManager.SettingsData
            {
                DemoModeEnabled = true,
                RtspUrl = "rtsp://new",
                PressureInputChannel = "AIN2",
                LoadCellInputChannel = "AIN4",
                IgniterOutputChannel = "DAC1",
                LoadCellFullScaleValue = 5000,
                LoadCellFullScaleUnit = "N",
                PressureFullScaleValue = 3000
            };
            mgr.UpdateAppliedSettings(ns);
            Assert.True(fired);
            Assert.Equal(5000, mgr.AppliedSettings.LoadCellFullScaleValue);
            Assert.Equal("N", mgr.AppliedSettings.LoadCellFullScaleUnit);
            Assert.Equal(3000, mgr.AppliedSettings.PressureFullScaleValue);

            var applied = JsonSerializer.Deserialize<SettingsManager.SettingsData>(
                File.ReadAllText(Path.Combine(cfg, "applied_settings.json"))
            )!;
            Assert.True(applied.DemoModeEnabled);
            Assert.Equal("AIN2", applied.PressureInputChannel);
            Assert.Equal("AIN4", applied.LoadCellInputChannel);
            Assert.Equal("DAC1", applied.IgniterOutputChannel);
            Assert.Equal(5000, applied.LoadCellFullScaleValue);
            Assert.Equal("N", applied.LoadCellFullScaleUnit);
            Assert.Equal(3000, applied.PressureFullScaleValue);

            mgr.SaveAppliedSettingsToDisk();
            var saved = JsonSerializer.Deserialize<SettingsManager.SettingsData>(
                File.ReadAllText(Path.Combine(cfg, "saved_settings.json"))
            )!;
            Assert.Equal("rtsp://new", saved.RtspUrl);
            Assert.Equal("AIN2", saved.PressureInputChannel);
            Assert.Equal("AIN4", saved.LoadCellInputChannel);
            Assert.Equal("DAC1", saved.IgniterOutputChannel);
            Assert.Equal(5000, saved.LoadCellFullScaleValue);
            Assert.Equal("N", saved.LoadCellFullScaleUnit);
            Assert.Equal(3000, saved.PressureFullScaleValue);
        }
    }
}
