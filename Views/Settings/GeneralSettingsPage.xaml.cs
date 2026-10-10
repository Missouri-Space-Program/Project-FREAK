using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Project_FREAK.Controllers;

namespace Project_FREAK.Views.Settings;

public partial class GeneralSettingsPage : Page
{
    private readonly SettingsManager _settingsManager;
    private readonly LabJackManager _labJackManager;
    private readonly NetworkConfigurationService _networkConfigService = new();
    private readonly Stopwatch _uiUpdateStopwatch = Stopwatch.StartNew();
    private readonly object _sampleLock = new();
    private SettingsManager.SettingsData _pendingSettings;
    private (double weightKg, double bridgeVoltage, double excitationVoltage)? _point1;
    private (double weightKg, double bridgeVoltage, double excitationVoltage)? _point2;
    private LoadCellCalibration? _calculatedCalibration;
    private double _latestBridgeVoltage;
    private double _latestExcitationVoltage;
    private double _latestLoadKg;
    private double _latestPressureVoltage;
    private bool _hasLiveSample;
    private bool _isInitializing = true;

    public GeneralSettingsPage()
    {
        InitializeComponent();
        _settingsManager = ((App)Application.Current).SettingsManager;
        _labJackManager = LabJackManager.Instance;
        _pendingSettings = _settingsManager.AppliedSettings.Clone();

        string[] inputChannels = Enumerable.Range(0, 14)
            .Where(channel => channel != 6)
            .Select(channel => $"AIN{channel}")
            .ToArray();
        PressureInputChannelComboBox.ItemsSource = inputChannels;
        LoadCellInputChannelComboBox.ItemsSource = inputChannels;
        IgniterOutputChannelComboBox.ItemsSource = new[] { "DAC0", "DAC1" };
        LoadCellFullScaleUnitComboBox.ItemsSource = new[] { "kg", "N" };
        PressureInputChannelComboBox.SelectedItem = _pendingSettings.PressureInputChannel;
        LoadCellInputChannelComboBox.SelectedItem = _pendingSettings.LoadCellInputChannel;
        IgniterOutputChannelComboBox.SelectedItem = _pendingSettings.IgniterOutputChannel;
        LoadCellFullScaleValueTextBox.Text = _pendingSettings.LoadCellFullScaleValue.ToString("G", CultureInfo.CurrentCulture);
        LoadCellFullScaleUnitComboBox.SelectedItem = _pendingSettings.LoadCellFullScaleUnit;
        UseRawDataForReviewCheckBox.DataContext = _pendingSettings;

        PressureZeroVoltageTextBox.Text = _pendingSettings.PressureZeroVoltage.ToString("G", CultureInfo.CurrentCulture);
        PressureFullScaleVoltageTextBox.Text = _pendingSettings.PressureFullScaleVoltage.ToString("G", CultureInfo.CurrentCulture);
        PressureFullScaleValueTextBox.Text = _pendingSettings.PressureFullScaleValue.ToString("G", CultureInfo.CurrentCulture);
        Point1WeightTextBox.Text = _pendingSettings.LoadCellPoint1WeightKg.ToString("G", CultureInfo.CurrentCulture);
        Point2WeightTextBox.Text = _pendingSettings.LoadCellPoint2WeightKg.ToString("G", CultureInfo.CurrentCulture);

        if (_pendingSettings.LoadCellCalibrationEnabled)
        {
            _point1 = (_pendingSettings.LoadCellPoint1WeightKg, _pendingSettings.LoadCellPoint1BridgeVoltage, _pendingSettings.LoadCellPoint1ExcitationVoltage);
            _point2 = (_pendingSettings.LoadCellPoint2WeightKg, _pendingSettings.LoadCellPoint2BridgeVoltage, _pendingSettings.LoadCellPoint2ExcitationVoltage);
            _calculatedCalibration = new LoadCellCalibration(
                _pendingSettings.LoadCellCalibrationSlope,
                _pendingSettings.LoadCellCalibrationOffset,
                _pendingSettings.LoadCellCalibrationExcitationVoltage);
            ShowCapturedPoint(Point1SampleText, _point1.Value);
            ShowCapturedPoint(Point2SampleText, _point2.Value);
            LoadCellCalibrationStatus.Text = "Saved calibration loaded.";
        }

        _isInitializing = false;
        _labJackManager.LoadCellCalibrationSampleUpdated += LoadCellSampleUpdated;
        _labJackManager.DataUpdated += SensorDataUpdated;
        _labJackManager.ConnectionStatusChanged += ConnectionStatusChanged;
        Unloaded += SettingsPage_Unloaded;
        UpdateDeviceStatus();
        UpdateLiveDisplays();
        RefreshNetworkAdapters();
    }

    private void LoadCellSampleUpdated(double bridgeVoltage, double excitationVoltage)
    {
        lock (_sampleLock)
        {
            _latestBridgeVoltage = bridgeVoltage;
            _latestExcitationVoltage = excitationVoltage;
            _hasLiveSample = true;
        }
    }

    private void SensorDataUpdated(double thrustVoltage, double calibratedLoad, double pressureVoltage, double pressure)
    {
        if (_uiUpdateStopwatch.ElapsedMilliseconds < 200)
            return;

        _uiUpdateStopwatch.Restart();
        Dispatcher.BeginInvoke(() =>
        {
            lock (_sampleLock)
            {
                _latestLoadKg = calibratedLoad / 9.81;
                _latestPressureVoltage = pressureVoltage;
                LoadCellLiveText.Text = $"{thrustVoltage:F5} V  |  {_latestLoadKg:F3} kg ({calibratedLoad:F2} N)";
                PressureLiveText.Text = $"{pressureVoltage:F4} V  |  {pressure:F2} PSI";
                CalibrationLoadCellLiveText.Text = $"Bridge signal: {_latestBridgeVoltage:G6} V  |  Excitation: {_latestExcitationVoltage:G5} V";
                UpdateLiveDisplays();
            }
        });
    }

    private void ConnectionStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(UpdateDeviceStatus);
    }

    private void UpdateDeviceStatus()
    {
        if (_labJackManager.IsDemo())
        {
            DeviceStatusText.Text = "SIMULATION — no LabJack detected";
            DeviceStatusText.Foreground = Brushes.Orange;
            SimulationNoticeText.Visibility = Visibility.Visible;
        }
        else
        {
            DeviceStatusText.Text = "LabJack connected";
            DeviceStatusText.Foreground = Brushes.LimeGreen;
            SimulationNoticeText.Visibility = Visibility.Collapsed;
        }
    }

    private void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceStatusText.Text = "Reconnecting…";
        _labJackManager.ReconnectDevice();
        UpdateDeviceStatus();
    }

    private void RefreshNetworkAdapters()
    {
        var adapters = _networkConfigService.GetAvailableAdapters();
        NetworkAdaptersComboBox.ItemsSource = adapters;

        if (adapters.Count > 0)
        {
            var preselected = adapters.FirstOrDefault(a => a.IsTestStandConfigured)
                              ?? adapters.FirstOrDefault(a => a.InterfaceType == NetworkInterfaceType.Ethernet && a.IsConnected)
                              ?? adapters.FirstOrDefault(a => a.IsConnected)
                              ?? adapters[0];

            NetworkAdaptersComboBox.SelectedItem = preselected;
            UpdateNetworkStatusForAdapter(preselected);
        }
        else
        {
            NetworkStatusText.Text = "No network adapters detected.";
            NetworkStatusText.Foreground = Brushes.Orange;
        }
    }

    private void UpdateNetworkStatusForAdapter(NetworkConfigurationService.AdapterInfo? adapter)
    {
        if (adapter == null)
        {
            NetworkStatusText.Text = string.Empty;
            return;
        }

        if (adapter.IsTestStandConfigured)
        {
            NetworkStatusText.Text = $"Configured for test stand: {adapter.CurrentIPv4} ({adapter.Name})";
            NetworkStatusText.Foreground = Brushes.LimeGreen;
        }
        else if (adapter.IsConnected)
        {
            var mode = adapter.IsDhcpEnabled ? "DHCP" : "Static";
            NetworkStatusText.Text = $"Current: {adapter.CurrentIPv4 ?? "No IP"} ({mode}) on {adapter.Name}. Click below to set test stand IP.";
            NetworkStatusText.Foreground = Brushes.White;
        }
        else
        {
            NetworkStatusText.Text = $"{adapter.Name} is disconnected. Connect Ethernet cable to the test stand.";
            NetworkStatusText.Foreground = Brushes.Orange;
        }
    }

    private void NetworkAdaptersComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkAdaptersComboBox.SelectedItem is NetworkConfigurationService.AdapterInfo adapter)
        {
            UpdateNetworkStatusForAdapter(adapter);
        }
    }

    private void RefreshAdaptersButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetworkAdapters();
    }

    private async void ConfigureStaticIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkAdaptersComboBox.SelectedItem is not NetworkConfigurationService.AdapterInfo adapter)
        {
            NetworkStatusText.Text = "Please select a network adapter to configure.";
            NetworkStatusText.Foreground = Brushes.Orange;
            return;
        }

        SetNetworkControlsEnabled(false);
        NetworkStatusText.Text = $"Configuring {adapter.Name} to {NetworkConfigurationService.DefaultTestStandIp}... Please accept the administrator prompt.";
        NetworkStatusText.Foreground = Brushes.Orange;

        try
        {
            var result = await _networkConfigService.ConfigureStaticTestStandIpAsync(adapter.Name);
            if (result.Success)
            {
                NetworkStatusText.Text = result.Message;
                NetworkStatusText.Foreground = Brushes.LimeGreen;
                RefreshNetworkAdapters();

                await Task.Delay(1000);
                _labJackManager.ReconnectDevice();
                UpdateDeviceStatus();
            }
            else
            {
                NetworkStatusText.Text = result.Message;
                NetworkStatusText.Foreground = result.CancelledByUser ? Brushes.Orange : Brushes.OrangeRed;
            }
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"Error: {ex.Message}";
            NetworkStatusText.Foreground = Brushes.OrangeRed;
        }
        finally
        {
            SetNetworkControlsEnabled(true);
        }
    }

    private async void RestoreDhcpButton_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkAdaptersComboBox.SelectedItem is not NetworkConfigurationService.AdapterInfo adapter)
        {
            NetworkStatusText.Text = "Please select a network adapter to restore.";
            NetworkStatusText.Foreground = Brushes.Orange;
            return;
        }

        SetNetworkControlsEnabled(false);
        NetworkStatusText.Text = $"Restoring {adapter.Name} to automatic DHCP... Please accept the administrator prompt.";
        NetworkStatusText.Foreground = Brushes.Orange;

        try
        {
            var result = await _networkConfigService.ConfigureDhcpAsync(adapter.Name);
            if (result.Success)
            {
                NetworkStatusText.Text = result.Message;
                NetworkStatusText.Foreground = Brushes.LimeGreen;
                RefreshNetworkAdapters();
            }
            else
            {
                NetworkStatusText.Text = result.Message;
                NetworkStatusText.Foreground = result.CancelledByUser ? Brushes.Orange : Brushes.OrangeRed;
            }
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"Error: {ex.Message}";
            NetworkStatusText.Foreground = Brushes.OrangeRed;
        }
        finally
        {
            SetNetworkControlsEnabled(true);
        }
    }

    private void SetNetworkControlsEnabled(bool enabled)
    {
        ConfigureStaticIpButton.IsEnabled = enabled;
        RestoreDhcpButton.IsEnabled = enabled;
        RefreshAdaptersButton.IsEnabled = enabled;
        NetworkAdaptersComboBox.IsEnabled = enabled;
    }

    private void CapturePoint1_Click(object sender, RoutedEventArgs e) => CapturePoint(Point1WeightTextBox, true);

    private void CapturePoint2_Click(object sender, RoutedEventArgs e) => CapturePoint(Point2WeightTextBox, false);

    private void CapturePoint(TextBox weightTextBox, bool isFirstPoint)
    {
        if (!TryReadNonNegative(weightTextBox.Text, out double weightKg))
        {
            LoadCellCalibrationStatus.Text = "Enter a valid known mass in kilograms before capturing.";
            return;
        }

        (double bridgeVoltage, double excitationVoltage) sample;
        lock (_sampleLock)
        {
            if (!_hasLiveSample || _latestExcitationVoltage <= 0)
            {
                LoadCellCalibrationStatus.Text = "No valid live sensor sample is available yet.";
                return;
            }
            sample = (_latestBridgeVoltage, _latestExcitationVoltage);
        }

        // Keep the known mass paired with the live bridge and excitation readings captured together.
        var point = (weightKg, sample.bridgeVoltage, sample.excitationVoltage);
        if (isFirstPoint)
        {
            _point1 = point;
            ShowCapturedPoint(Point1SampleText, point);
        }
        else
        {
            _point2 = point;
            ShowCapturedPoint(Point2SampleText, point);
        }

        _calculatedCalibration = null;
        LoadCellCalibrationStatus.Text = "Sample captured. Capture the other known load, then calculate.";
    }

    private static void ShowCapturedPoint(TextBlock target, (double weightKg, double bridgeVoltage, double excitationVoltage) point)
    {
        target.Text = $"Captured {point.weightKg:G6} kg at {point.bridgeVoltage:G6} V bridge signal and {point.excitationVoltage:G5} V excitation.";
    }

    private void CalculateLoadCellCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_point1 is null || _point2 is null)
        {
            LoadCellCalibrationStatus.Text = "Capture both known load conditions before calculating.";
            return;
        }

        try
        {
            // Store the fitted coefficients and source samples as pending settings; Apply or Save activates them.
            _calculatedCalibration = SensorCalibration.FitLoadCell(
                _point1.Value.weightKg,
                _point1.Value.bridgeVoltage,
                _point1.Value.excitationVoltage,
                _point2.Value.weightKg,
                _point2.Value.bridgeVoltage,
                _point2.Value.excitationVoltage);

            _pendingSettings.LoadCellCalibrationEnabled = true;
            _pendingSettings.LoadCellCalibrationSlope = _calculatedCalibration.Value.Slope;
            _pendingSettings.LoadCellCalibrationOffset = _calculatedCalibration.Value.Offset;
            _pendingSettings.LoadCellCalibrationExcitationVoltage = _calculatedCalibration.Value.ExcitationReferenceVoltage;
            _pendingSettings.LoadCellPoint1WeightKg = _point1.Value.weightKg;
            _pendingSettings.LoadCellPoint1BridgeVoltage = _point1.Value.bridgeVoltage;
            _pendingSettings.LoadCellPoint1ExcitationVoltage = _point1.Value.excitationVoltage;
            _pendingSettings.LoadCellPoint2WeightKg = _point2.Value.weightKg;
            _pendingSettings.LoadCellPoint2BridgeVoltage = _point2.Value.bridgeVoltage;
            _pendingSettings.LoadCellPoint2ExcitationVoltage = _point2.Value.excitationVoltage;
            LoadCellCalibrationStatus.Text = $"Calibration calculated (slope {_calculatedCalibration.Value.Slope:G6}, offset {_calculatedCalibration.Value.Offset:G6}). Apply to use it live.";
            UpdateLiveDisplays();
        }
        catch (ArgumentException exception)
        {
            LoadCellCalibrationStatus.Text = exception.Message;
        }
    }

    private void PressureScale_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing)
            return;

        if (TryReadPressureScale(out _, out _, out _))
        {
            PressureValidationText.Text = string.Empty;
            UpdateLiveDisplays();
        }
        else
        {
            PressureValidationText.Text = "Enter a zero voltage, a higher full-scale voltage, and a positive full-scale pressure.";
        }
    }

    private void UpdateLiveDisplays()
    {
        double loadKg = _latestLoadKg;
        lock (_sampleLock)
        {
            if (_calculatedCalibration is { } calibration && _hasLiveSample && _latestExcitationVoltage > 0)
                loadKg = SensorCalibration.LoadKilograms(calibration, _latestBridgeVoltage, _latestExcitationVoltage);
        }
        LoadCellResultText.Text = $"Current calibrated load: {loadKg:F3} kg ({loadKg * 9.81:F2} N)";

        if (TryReadPressureScale(out double zeroVoltage, out double fullScaleVoltage, out double fullScaleValue))
        {
            double pressure = SensorCalibration.ScalePressure(_latestPressureVoltage, zeroVoltage, fullScaleVoltage, fullScaleValue);
            PressureCalibrationLiveText.Text = $"Current pressure: {pressure:F2} PSI  |  Input: {_latestPressureVoltage:F4} V";
        }
    }

    private static bool TryReadNonNegative(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
               double.IsFinite(value) && value >= 0;
    }

    private static bool TryReadPositive(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
               double.IsFinite(value) && value > 0;
    }

    private bool TryReadPressureScale(out double zeroVoltage, out double fullScaleVoltage, out double fullScaleValue)
    {
        zeroVoltage = 0;
        fullScaleVoltage = 0;
        fullScaleValue = 0;
        bool parsed = double.TryParse(PressureZeroVoltageTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out zeroVoltage) &&
                      double.TryParse(PressureFullScaleVoltageTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out fullScaleVoltage) &&
                      double.TryParse(PressureFullScaleValueTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out fullScaleValue);
        return parsed && double.IsFinite(zeroVoltage) && double.IsFinite(fullScaleVoltage) && double.IsFinite(fullScaleValue) &&
               fullScaleVoltage > zeroVoltage && fullScaleValue > 0;
    }

    private bool UpdatePendingSettings()
    {
        if (!TryReadPositive(LoadCellFullScaleValueTextBox.Text, out double loadCellFullScaleValue) ||
            LoadCellFullScaleUnitComboBox.SelectedItem is not string loadCellFullScaleUnit)
        {
            LoadCellCapacityValidationText.Text = "Enter a positive rated capacity and choose kg or N.";
            return false;
        }

        LoadCellCapacityValidationText.Text = string.Empty;
        if (!TryReadPressureScale(out double zeroVoltage, out double fullScaleVoltage, out double fullScaleValue))
        {
            PressureValidationText.Text = "Enter a zero voltage, a higher full-scale voltage, and a positive full-scale pressure.";
            return false;
        }

        string? pressureChannel = PressureInputChannelComboBox.SelectedItem as string;
        string? loadCellChannel = LoadCellInputChannelComboBox.SelectedItem as string;
        string? igniterChannel = IgniterOutputChannelComboBox.SelectedItem as string;
        if (pressureChannel is null || loadCellChannel is null || igniterChannel is null)
        {
            SettingsStatusText.Text = "Select a valid channel for each sensor and the igniter.";
            return false;
        }
        if (pressureChannel == loadCellChannel)
        {
            SettingsStatusText.Text = "Pressure and load-cell sensors must use different AIN channels.";
            return false;
        }

        _pendingSettings.PressureZeroVoltage = zeroVoltage;
        _pendingSettings.PressureFullScaleVoltage = fullScaleVoltage;
        _pendingSettings.PressureFullScaleValue = fullScaleValue;
        _pendingSettings.LoadCellFullScaleValue = loadCellFullScaleValue;
        _pendingSettings.LoadCellFullScaleUnit = loadCellFullScaleUnit;
        _pendingSettings.PressureInputChannel = pressureChannel;
        _pendingSettings.LoadCellInputChannel = loadCellChannel;
        _pendingSettings.IgniterOutputChannel = igniterChannel;
        _pendingSettings.UseRawDataForReview = UseRawDataForReviewCheckBox.IsChecked == true;
        return true;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdatePendingSettings())
            return;

        _settingsManager.UpdateAppliedSettings(_pendingSettings.Clone());
        SettingsStatusText.Text = "Settings applied to the current session.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdatePendingSettings())
            return;

        _settingsManager.UpdateAppliedSettings(_pendingSettings.Clone());
        _settingsManager.SaveAppliedSettingsToDisk();
        SettingsStatusText.Text = "Settings applied and saved for future sessions.";
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _labJackManager.LoadCellCalibrationSampleUpdated -= LoadCellSampleUpdated;
        _labJackManager.DataUpdated -= SensorDataUpdated;
        _labJackManager.ConnectionStatusChanged -= ConnectionStatusChanged;
    }
}
