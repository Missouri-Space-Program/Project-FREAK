using System;
using System.Diagnostics;
using LabJack;
using System.Threading;

namespace Project_FREAK.Controllers
{
    public class LabJackManager : IDisposable
    {
        private int _handle = -1; // Handle for the LabJack device
        private volatile bool _isDemo = true; // Indicates if the device is in demo mode
        private bool _isIgniterArmed = false; // Indicates if the igniter is armed
        private static LabJackManager _instance = null!; // Singleton instance
        private volatile bool _isReading; // Indicates if data is being read from the device
        private readonly object _deviceLock = new();
        public event Action<double, double, double, double> DataUpdated = delegate { }; // Event triggered when data is updated
        public event Action<double, double> LoadCellCalibrationSampleUpdated = delegate { };
        public event EventHandler? ConnectionStatusChanged;
        private SettingsManager.SettingsData _calibrationSettings = new();
        private string _activeIgniterChannel = "DAC0";
        private readonly DemoMotorSimulator _demoSimulator = new();
        private long _demoProfileStartTimestamp = Stopwatch.GetTimestamp();
        private Timer? _returnTimer; // Timer to return DAC0 to zero
        private LabJackManager()
        {
            InitalizeLabJack(); // Initialize the LabJack device
            StartReading(); // Start reading data from the device
        }

        // Singleton instance property
        public static LabJackManager Instance => _instance ??= new LabJackManager();

        // Initializes the LabJack device
        private void InitalizeLabJack()
        {
            try
            {
                LJM.OpenS("ANY", "ANY", "ANY", ref _handle);
                _isDemo = false;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"No LabJack device detected: {exception.Message}");
                _isDemo = true;
                if (_handle > 0)
                {
                    try { LJM.Close(_handle); } catch { }
                }
                _handle = -1;

                try
                {
                    LJM.OpenS("ANY", "ANY", "LJM_DEMO_MODE", ref _handle);
                }
                catch
                {
                    _handle = -1;
                }
                return;
            }

            ConfigureAnalogInputs(Volatile.Read(ref _calibrationSettings));
        }

        // Returns the handle of the LabJack device
        public int GetHandle()
        {
            return _handle;
        }

        // Checks if currently running in demo mode
        public bool IsDemo()
        {
            return _isDemo;
        }

        public void ApplyCalibration(SettingsManager.SettingsData settings)
        {
            var normalizedSettings = settings.Clone().NormalizeChannels();
            lock (_deviceLock)
            {
                Volatile.Write(ref _calibrationSettings, normalizedSettings);
                ConfigureAnalogInputs(normalizedSettings);
            }
        }

        private void ConfigureAnalogInputs(SettingsManager.SettingsData settings)
        {
            if (_isDemo || _handle <= 0)
                return;

            try
            {
                var channels = new[] { settings.PressureInputChannel, settings.LoadCellInputChannel, "AIN6" }
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var names = channels
                    .SelectMany(channel => new[] { $"{channel}_RESOLUTION_INDEX", $"{channel}_SETTLING_US" })
                    .ToArray();
                var values = new double[names.Length];
                int errorAddress = -1;
                LJM.eWriteNames(_handle, names.Length, names, values, ref errorAddress);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Unable to configure LabJack inputs: {exception.Message}");
            }
        }

        public void ReconnectDevice()
        {
            lock (_deviceLock)
            {
                if (_handle > 0)
                {
                    try { LJM.Close(_handle); }
                    catch (Exception exception) { Console.WriteLine($"Error closing LabJack before reconnect: {exception.Message}"); }
                }

                _handle = -1;
                _isDemo = true;
                _returnTimer?.Dispose();
                _returnTimer = null;
                InitalizeLabJack();
                ConnectionStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Returns the armed status of the igniter
        public bool GetArmedStatus() => _isIgniterArmed;

        // Arms or disarms the igniter
        public void ArmDisarmIgniter() => _isIgniterArmed = !_isIgniterArmed;

        // Ignites the motor by writing 5V to the igniter wire
        public void IgniteMotor()
        {
            lock (_deviceLock)
            {
                if (_isDemo)
                {
                    Interlocked.Exchange(ref _demoProfileStartTimestamp, Stopwatch.GetTimestamp());
                    return;
                }

                _activeIgniterChannel = Volatile.Read(ref _calibrationSettings).IgniterOutputChannel;
                LJM.eWriteName(_handle, _activeIgniterChannel, 5.0);
                // After 2.5 seconds, return the selected output channel to zero.
                _returnTimer = new Timer(ReturnToZero, null, 2500, Timeout.Infinite);
            }
        }

        // Returns DAC0 to zero voltage
        private void ReturnToZero(object? state)
        {
            lock (_deviceLock)
            {
                if (!_isDemo && _handle > 0)
                    LJM.eWriteName(_handle, _activeIgniterChannel, 0.0);
            }
            _returnTimer?.Dispose();
            _returnTimer = null;
        }

        // Starts reading data from the LabJack device
        private void StartReading()
        {
            _isReading = true;
            // Spin up a thread from the thread pool to read data from LabJack
            Task.Run(() =>
            {
                while (_isReading)
                {
                    ReadData();
                    Thread.Sleep(50);
                }
            });
        }

        // Reads data from the LabJack device
        private void ReadData()
        {
            try
            {
                double thrustVoltage = 0, pressureVoltage = 0;
                double Vexc = 0;
                var settings = Volatile.Read(ref _calibrationSettings);
                bool isDemo;
                lock (_deviceLock)
                {
                    isDemo = _isDemo;
                    if (!isDemo)
                    {
                        if (_handle <= 0)
                            return;

                        LJM.eReadName(_handle, settings.LoadCellInputChannel, ref thrustVoltage);
                        LJM.eReadName(_handle, settings.PressureInputChannel, ref pressureVoltage);
                        Vexc = 0;
                        LJM.eReadName(_handle, "AIN6", ref Vexc);
                    }
                }

                if (isDemo)
                {
                    double elapsedSeconds = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _demoProfileStartTimestamp)) / (double)Stopwatch.Frequency;
                    var reading = _demoSimulator.Sample(
                        elapsedSeconds,
                        settings.GetLoadCellFullScaleNewtons(),
                        settings.PressureFullScaleValue);
                    double loadKg = reading.ThrustNewtons / 9.80665;
                    bool useCalibratedDemoSignal = settings.LoadCellCalibrationEnabled &&
                        double.IsFinite(settings.LoadCellCalibrationSlope) &&
                        Math.Abs(settings.LoadCellCalibrationSlope) > 1e-12 &&
                        double.IsFinite(settings.LoadCellCalibrationOffset) &&
                        double.IsFinite(settings.LoadCellCalibrationExcitationVoltage) &&
                        settings.LoadCellCalibrationExcitationVoltage > 0;
                    Vexc = useCalibratedDemoSignal ? settings.LoadCellCalibrationExcitationVoltage : 2.5;
                    double bridgeVoltage = useCalibratedDemoSignal
                        ? (loadKg - settings.LoadCellCalibrationOffset) / settings.LoadCellCalibrationSlope
                        : (loadKg + 3.8069375) / 100387.5;
                    thrustVoltage = 1.25 + bridgeVoltage * 201;
                    pressureVoltage = settings.PressureZeroVoltage +
                        reading.PressurePsi / settings.PressureFullScaleValue *
                        (settings.PressureFullScaleVoltage - settings.PressureZeroVoltage);
                }

                double loadAdjVoltage = (thrustVoltage - 1.25) / 201;
                LoadCellCalibrationSampleUpdated?.Invoke(loadAdjVoltage, Vexc);
                double calibratedLoadKg;
                if (settings.LoadCellCalibrationEnabled &&
                    double.IsFinite(settings.LoadCellCalibrationSlope) &&
                    double.IsFinite(settings.LoadCellCalibrationOffset) &&
                    settings.LoadCellCalibrationExcitationVoltage > 0)
                {
                    var calibration = new LoadCellCalibration(
                        settings.LoadCellCalibrationSlope,
                        settings.LoadCellCalibrationOffset,
                        settings.LoadCellCalibrationExcitationVoltage);
                    calibratedLoadKg = SensorCalibration.LoadKilograms(calibration, loadAdjVoltage, Vexc);
                }
                else
                {
                    calibratedLoadKg = 100387.5 * loadAdjVoltage - 3.8069375;
                }
                double calibratedLoad = calibratedLoadKg * 9.80665; // Convert kg to N using standard gravity

                double pressure = SensorCalibration.ScalePressure(
                    pressureVoltage,
                    settings.PressureZeroVoltage,
                    settings.PressureFullScaleVoltage,
                    settings.PressureFullScaleValue);
                DataUpdated?.Invoke(thrustVoltage, calibratedLoad, pressureVoltage, pressure);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Error reading values: {exception.Message}");
            }
        }

        // Closes the LabJack device
        public void CloseDevice()
        {
            _isReading = false;
            lock (_deviceLock)
            {
                int handle = _handle;
                _handle = -1;
                _instance = null!;
                if (handle > 0)
                    LJM.Close(handle);
            }
        }

        // Disposes the LabJackManager and closes the device
        public void Dispose()
        {
            CloseDevice();
            GC.SuppressFinalize(this);
        }
    }
    }
