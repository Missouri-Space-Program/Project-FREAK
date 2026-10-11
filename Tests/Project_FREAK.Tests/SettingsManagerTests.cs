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
            Path.GetTempPath(),
            "Project-FREAK.Tests", Guid.NewGuid().ToString("N"));

        SettingsManager CreateManager() => new SettingsManager(Path.Combine(cfg, "saved_settings.json"));

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
            var mgr = CreateManager();
            Assert.False(mgr.AppliedSettings.DemoModeEnabled);
            Assert.StartsWith("rtsp://admin", mgr.AppliedSettings.RtspUrl);
            Assert.Equal("AIN0", mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal("AIN5", mgr.AppliedSettings.LoadCellInputChannel);
            Assert.Equal("DAC0", mgr.AppliedSettings.IgniterOutputChannel);
            Assert.Equal(500, mgr.AppliedSettings.LoadCellFullScaleValue);
            Assert.Equal("kg", mgr.AppliedSettings.LoadCellFullScaleUnit);
            Assert.Equal(1000, mgr.AppliedSettings.SamplingRateHz);
            Assert.Equal(15, mgr.AppliedSettings.LiveGraphWindowSeconds);
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

            var mgr = CreateManager();
            Assert.True(mgr.AppliedSettings.DemoModeEnabled);
            Assert.Equal("rtsp://test", mgr.AppliedSettings.RtspUrl);
            Assert.Equal("AIN2", mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal("AIN4", mgr.AppliedSettings.LoadCellInputChannel);
            Assert.Equal("DAC1", mgr.AppliedSettings.IgniterOutputChannel);
        }

        [Fact]
        public void Constructor_IgnoresLegacyAppliedSettingsFile()
        {
            Directory.CreateDirectory(cfg);
            File.WriteAllText(
                Path.Combine(cfg, "saved_settings.json"),
                JsonSerializer.Serialize(new SettingsManager.SettingsData { RtspUrl = "rtsp://saved" })
            );
            File.WriteAllText(
                Path.Combine(cfg, "applied_settings.json"),
                JsonSerializer.Serialize(new SettingsManager.SettingsData { RtspUrl = "rtsp://applied" })
            );

            var mgr = CreateManager();

            Assert.Equal("rtsp://saved", mgr.AppliedSettings.RtspUrl);
        }

        [Fact]
        public void UpdateAppliedSettings_InvalidChannelsFallBackToCurrentWiringDefaults()
        {
            var mgr = CreateManager();
            mgr.UpdateAppliedSettings(new SettingsManager.SettingsData
            {
                PressureInputChannel = "AIN99",
                LoadCellInputChannel = "not-an-input",
                IgniterOutputChannel = "AIN3",
                SamplingRateHz = -50,
                LiveGraphWindowSeconds = 999
            });

            Assert.Equal("AIN0", mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal("AIN5", mgr.AppliedSettings.LoadCellInputChannel);
            Assert.Equal("DAC0", mgr.AppliedSettings.IgniterOutputChannel);
            Assert.Equal(1000, mgr.AppliedSettings.SamplingRateHz);
            Assert.Equal(15, mgr.AppliedSettings.LiveGraphWindowSeconds);
        }

        [Theory]
        [InlineData("AIN6", "AIN0", "AIN0", "AIN5")]
        [InlineData("AIN0", "AIN0", "AIN0", "AIN5")]
        [InlineData("AIN5", "AIN6", "AIN5", "AIN0")]
        [InlineData("AIN5", "AIN5", "AIN5", "AIN0")]
        public void UpdateAppliedSettings_ReservesExcitationAndSeparatesSensorChannels(
            string pressureChannel,
            string loadCellChannel,
            string expectedPressureChannel,
            string expectedLoadCellChannel)
        {
            var mgr = CreateManager();
            mgr.UpdateAppliedSettings(new SettingsManager.SettingsData
            {
                PressureInputChannel = pressureChannel,
                LoadCellInputChannel = loadCellChannel
            });

            Assert.Equal(expectedPressureChannel, mgr.AppliedSettings.PressureInputChannel);
            Assert.Equal(expectedLoadCellChannel, mgr.AppliedSettings.LoadCellInputChannel);
        }

        [Fact]
        public void UpdateAppliedSettings_NormalizesInvalidLoadCellRangeAndConvertsNewtons()
        {
            var mgr = CreateManager();
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
        public void UpdateAppliedSettings_RaisesEventWithoutWritingSessionFile()
        {
            Directory.CreateDirectory(cfg);
            var mgr = CreateManager();
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

            Assert.False(File.Exists(Path.Combine(cfg, "applied_settings.json")));

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
