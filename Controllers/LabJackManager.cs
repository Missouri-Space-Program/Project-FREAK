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
        private volatile bool _isStreaming; // Indicates if hardware or demo streaming is active
        private int _idleReadGeneration; // Incremented each time idle reads start so older idle loops exit
        private Task? _streamingTask;
        private CancellationTokenSource? _streamingCts;
        private readonly object _deviceLock = new();
        private readonly object _streamLifecycleLock = new();
        public event Action<double, double, double, double> DataUpdated = delegate { }; // Event triggered when data is updated
        public event Action<DataPointRecord[]> BatchDataUpdated = delegate { }; // Event triggered when a streaming batch is available
        public event Action<double, double> LoadCellCalibrationSampleUpdated = delegate { };
        public event Action<string>? StreamingFailed;
        public event EventHandler? ConnectionStatusChanged;
        public bool IsStreaming => _isStreaming;
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
            lock (_streamLifecycleLock)
            {
                StopStreamingInternal();
                ReconnectDeviceCore();
            }
        }

        private void ReconnectDeviceCore()
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
                // StartStreaming turns off idle reads, so restore them here. Without this the UI stops receiving data.
                if (!_isStreaming) StartReading();
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

        public void StartStreaming(int? targetScanRateHz = null)
        {
            lock (_streamLifecycleLock)
            {
                lock (_deviceLock)
                {
                    if (_isStreaming) return;

                    // Stop idle reading
                    _isReading = false;

                    var settings = Volatile.Read(ref _calibrationSettings);
                    int rateHz = targetScanRateHz ?? settings.SamplingRateHz;
                    if (rateHz < 10) rateHz = 1000;

                    _isStreaming = true;
                    _streamingCts = new CancellationTokenSource();
                    var token = _streamingCts.Token;
                    bool useDemo = _isDemo || _handle <= 0;

                    _streamingTask = Task.Run(() =>
                    {
                        try
                        {
                            if (useDemo)
                                RunDemoStreamingLoop(rateHz, token);
                            else
                                RunHardwareStreamingLoop(rateHz, token);
                        }
                        catch (Exception exception)
                        {
                            if (!token.IsCancellationRequested)
                                StreamingFailed?.Invoke($"Sensor acquisition failed: {exception.Message}");
                        }
                        finally
                        {
                            EndStreamingAfterUnexpectedExit(token);
                        }
                    }, token);
                }
            }
        }

        public void StopStreaming()
        {
            StopStreamingInternal();

            lock (_deviceLock)
            {
                // Resume idle polling if not disposed and no new stream has started
                if (_instance != null && !_isStreaming && (_handle > 0 || _isDemo))
                {
                    StartReading();
                }
            }
        }

        private void StopStreamingInternal()
        {
            lock (_streamLifecycleLock)
            {
                StopStreamingCore();
            }
        }

        private void StopStreamingCore()
        {
            Task? taskToWait;
            CancellationTokenSource? ctsToDispose;
            lock (_deviceLock)
            {
                if (_streamingTask == null) return;
                _isStreaming = false;
                ctsToDispose = _streamingCts;
                taskToWait = _streamingTask;
                _streamingCts = null;
                ctsToDispose?.Cancel();
            }

            // Native stream reads are not cancellable; wait outside _deviceLock before reusing the handle.
            try
            {
                taskToWait.GetAwaiter().GetResult();
            }
            catch { }

            ctsToDispose?.Dispose();
            lock (_deviceLock)
            {
                if (ReferenceEquals(_streamingTask, taskToWait))
                    _streamingTask = null;
            }
        }

        // Leave idle polling stopped after a failed stream so a recording cannot silently change time bases.
        private void EndStreamingAfterUnexpectedExit(CancellationToken token)
        {
            lock (_deviceLock)
            {
                if (token.IsCancellationRequested) return;

                _isStreaming = false;
                _streamingCts?.Dispose();
                _streamingCts = null;
                _streamingTask = null;
            }
        }

        private void RunHardwareStreamingLoop(int rateHz, CancellationToken token)
        {
            int handle;
            SettingsManager.SettingsData settings;
            lock (_deviceLock)
            {
                handle = _handle;
                settings = Volatile.Read(ref _calibrationSettings);
            }

            if (handle <= 0)
            {
                StreamingFailed?.Invoke("The LabJack device is no longer available.");
                return;
            }

            int[] aScanList = new int[3];
            int dummyType = 0;
            int err1 = (int)LJM.NameToAddress(settings.LoadCellInputChannel, ref aScanList[0], ref dummyType);
            int err2 = (int)LJM.NameToAddress(settings.PressureInputChannel, ref aScanList[1], ref dummyType);
            int err3 = (int)LJM.NameToAddress("AIN6", ref aScanList[2], ref dummyType);
            if (err1 != 0 || err2 != 0 || err3 != 0)
            {
                aScanList[0] = ParseAinAddress(settings.LoadCellInputChannel, 10);
                aScanList[1] = ParseAinAddress(settings.PressureInputChannel, 0);
                aScanList[2] = 12; // AIN6
            }

            double scanRate = rateHz;
            int scansPerRead = Math.Clamp(rateHz / 20, 10, 200);
            int startErr = (int)LJM.eStreamStart(handle, scansPerRead, 3, aScanList, ref scanRate);
            if (startErr != 0)
            {
                StreamingFailed?.Invoke($"Failed to start LabJack hardware stream: {startErr}.");
                return;
            }

            double actualScanRate = scanRate > 0 ? scanRate : rateHz;
            double[] aData = new double[scansPerRead * 3];
            int deviceScanBacklog = 0;
            int ljmScanBacklog = 0;
            long scanIndex = 0;

            try
            {
                while (_isStreaming && !token.IsCancellationRequested)
                {
                    int readErr = (int)LJM.eStreamRead(handle, aData, ref deviceScanBacklog, ref ljmScanBacklog);
                    if (readErr != 0)
                    {
                        if (_isStreaming)
                        {
                            Console.WriteLine($"LabJack eStreamRead error: {readErr}");
                            StreamingFailed?.Invoke($"LabJack stream read failed: {readErr}.");
                        }
                        break;
                    }

                    var currentSettings = Volatile.Read(ref _calibrationSettings);
                    var batch = new DataPointRecord[scansPerRead];
                    double lastVexc = 2.5;

                    for (int i = 0; i < scansPerRead; i++)
                    {
                        double thrustVoltage = aData[i * 3 + 0];
                        double pressureVoltage = aData[i * 3 + 1];
                        double Vexc = aData[i * 3 + 2];
                        lastVexc = Vexc;

                        double time = scanIndex / actualScanRate;
                        scanIndex++;

                        ComputeScaledSensors(currentSettings, thrustVoltage, pressureVoltage, Vexc,
                            out double calibratedLoad, out double pressure);

                        batch[i] = new DataPointRecord(time, calibratedLoad, pressure, thrustVoltage, pressureVoltage, Vexc);
                    }

                    var last = batch[^1];
                    LoadCellCalibrationSampleUpdated?.Invoke((last.ThrustVoltage - 1.25) / 201, lastVexc);
                    DataUpdated?.Invoke(last.ThrustVoltage, last.Thrust, last.PressureVoltage, last.Pressure);
                    BatchDataUpdated?.Invoke(batch);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in hardware streaming loop: {ex.Message}");
                if (_isStreaming && !token.IsCancellationRequested)
                    StreamingFailed?.Invoke($"LabJack stream failed: {ex.Message}");
            }
            finally
            {
                try { LJM.eStreamStop(handle); } catch { }
            }
        }

        private void RunDemoStreamingLoop(int rateHz, CancellationToken token)
        {
            double scanRate = rateHz;
            int scansPerRead = Math.Clamp(rateHz / 20, 10, 200);
            double scanIntervalSeconds = 1.0 / scanRate;
            var stopwatch = Stopwatch.StartNew();
            long scanIndex = 0;

            try
            {
                while (_isStreaming && !token.IsCancellationRequested)
                {
                    var settings = Volatile.Read(ref _calibrationSettings);
                    var batch = new DataPointRecord[scansPerRead];
                    double demoStartTs = Interlocked.Read(ref _demoProfileStartTimestamp);
                    double lastVexc = 2.5;

                    for (int i = 0; i < scansPerRead; i++)
                    {
                        double time = scanIndex * scanIntervalSeconds;
                        scanIndex++;

                        double profileElapsed = (Stopwatch.GetTimestamp() - demoStartTs) / (double)Stopwatch.Frequency + (i * scanIntervalSeconds);
                        var reading = _demoSimulator.Sample(
                            profileElapsed,
                            settings.GetLoadCellFullScaleNewtons(),
                            settings.PressureFullScaleValue);

                        double loadKg = reading.ThrustNewtons / SensorCalibration.StandardGravityMetersPerSecondSquared;
                        bool useCalibratedDemoSignal = settings.LoadCellCalibrationEnabled &&
                            double.IsFinite(settings.LoadCellCalibrationSlope) &&
                            Math.Abs(settings.LoadCellCalibrationSlope) > 1e-12 &&
                            double.IsFinite(settings.LoadCellCalibrationOffset) &&
                            double.IsFinite(settings.LoadCellCalibrationExcitationVoltage) &&
                            settings.LoadCellCalibrationExcitationVoltage > 0;
                        double Vexc = useCalibratedDemoSignal ? settings.LoadCellCalibrationExcitationVoltage : 2.5;
                        lastVexc = Vexc;
                        double bridgeVoltage = useCalibratedDemoSignal
                            ? (loadKg - settings.LoadCellCalibrationOffset) / settings.LoadCellCalibrationSlope
                            : (loadKg + 3.8069375) / 100387.5;
                        double thrustVoltage = 1.25 + bridgeVoltage * 201;
                        double pressureVoltage = settings.PressureZeroVoltage +
                            reading.PressurePsi / settings.PressureFullScaleValue *
                            (settings.PressureFullScaleVoltage - settings.PressureZeroVoltage);

                        ComputeScaledSensors(settings, thrustVoltage, pressureVoltage, Vexc,
                            out double calibratedLoad, out double pressure);

                        batch[i] = new DataPointRecord(time, calibratedLoad, pressure, thrustVoltage, pressureVoltage, Vexc);
                    }

                    var last = batch[^1];
                    LoadCellCalibrationSampleUpdated?.Invoke((last.ThrustVoltage - 1.25) / 201, lastVexc);
                    DataUpdated?.Invoke(last.ThrustVoltage, last.Thrust, last.PressureVoltage, last.Pressure);
                    BatchDataUpdated?.Invoke(batch);

                    double targetMs = (scanIndex / scanRate) * 1000.0;
                    double currentMs = stopwatch.Elapsed.TotalMilliseconds;
                    int delayMs = (int)(targetMs - currentMs);
                    if (delayMs > 1)
                    {
                        Thread.Sleep(delayMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in demo streaming loop: {ex.Message}");
                if (_isStreaming && !token.IsCancellationRequested)
                    StreamingFailed?.Invoke($"Demo data stream failed: {ex.Message}");
            }
        }

        private static void ComputeScaledSensors(
            SettingsManager.SettingsData settings,
            double thrustVoltage,
            double pressureVoltage,
            double Vexc,
            out double calibratedLoad,
            out double pressure)
        {
            double loadAdjVoltage = (thrustVoltage - 1.25) / 201;
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
            calibratedLoad = calibratedLoadKg * SensorCalibration.StandardGravityMetersPerSecondSquared;

            pressure = SensorCalibration.ScalePressure(
                pressureVoltage,
                settings.PressureZeroVoltage,
                settings.PressureFullScaleVoltage,
                settings.PressureFullScaleValue);
        }

        private static int ParseAinAddress(string channel, int fallback)
        {
            if (channel.StartsWith("AIN", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(channel.AsSpan(3), out int ainNum))
            {
                return ainNum * 2;
            }
            return fallback;
        }

        // Starts reading data from the LabJack device
        private void StartReading()
        {
            // Each call retires any earlier idle loop, so overlapping calls cannot run two loops.
            int generation = Interlocked.Increment(ref _idleReadGeneration);
            _isReading = true;
            // Spin up a thread from the thread pool to read data from LabJack
            Task.Run(() =>
            {
                while (_isReading && generation == Volatile.Read(ref _idleReadGeneration))
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
                    double loadKg = reading.ThrustNewtons / SensorCalibration.StandardGravityMetersPerSecondSquared;
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

                ComputeScaledSensors(settings, thrustVoltage, pressureVoltage, Vexc,
                    out double calibratedLoad, out double pressure);
                LoadCellCalibrationSampleUpdated?.Invoke((thrustVoltage - 1.25) / 201, Vexc);
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
            lock (_streamLifecycleLock)
            {
                StopStreaming();
                CloseDeviceCore();
            }
        }

        private void CloseDeviceCore()
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
