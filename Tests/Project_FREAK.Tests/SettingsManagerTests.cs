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
                IgniterOutputChannel = "DAC1"
            };
            mgr.UpdateAppliedSettings(ns);
            Assert.True(fired);

            var applied = JsonSerializer.Deserialize<SettingsManager.SettingsData>(
                File.ReadAllText(Path.Combine(cfg, "applied_settings.json"))
            )!;
            Assert.True(applied.DemoModeEnabled);
            Assert.Equal("AIN2", applied.PressureInputChannel);
            Assert.Equal("AIN4", applied.LoadCellInputChannel);
            Assert.Equal("DAC1", applied.IgniterOutputChannel);

            mgr.SaveAppliedSettingsToDisk();
            var saved = JsonSerializer.Deserialize<SettingsManager.SettingsData>(
                File.ReadAllText(Path.Combine(cfg, "saved_settings.json"))
            )!;
            Assert.Equal("rtsp://new", saved.RtspUrl);
            Assert.Equal("AIN2", saved.PressureInputChannel);
            Assert.Equal("AIN4", saved.LoadCellInputChannel);
            Assert.Equal("DAC1", saved.IgniterOutputChannel);
        }
    }
}
