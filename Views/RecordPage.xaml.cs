using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Project_FREAK.Controllers;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.IO;

namespace Project_FREAK.Views
{
    public partial class RecordPage : Page
    {
        private WebcamManager _webcamManager = null!;
        private GraphManager _graphManager = null!;
        private LabJackManager _labjackManager = null!;
        private DataRecorder _dataRecorder = null!; // Records data points
        private CountdownService _countdownService = null!;
        private readonly Stopwatch _stopwatch = new(); // Stopwatch to track elapsed time
        private readonly Stopwatch _graphUpdateStopwatch = new(); // Stopwatch to control graph update rate
        private readonly ConcurrentQueue<(double time, double thrust, double pressure, double thrustVoltage, double pressureVoltage)> _dataQueue = new(); // Queue to store data points
        private bool _isSaving = false;
        private bool _isRecording = false; // Flag to indicate if recording is in progress
        private bool _isVideoRecording;
        private bool _managersInitialized;
        private const int graphFPS = 30;
        private string? _currentSessionFolder;
        private string? _currentVideoPath;
        private string? _recordingFailureReason;

        public RecordPage()
        {
            InitializeComponent();
            Loaded += RecordPage_Loaded;
            Unloaded += RecordPage_Unloaded;
        }

        // Sets up the countdown timer interval
        private void SetupTimer()
        {
            _countdownService.InitializeTimer(TimeSpan.FromSeconds(1));
        }

        // Subscribes to settings changes
        private void SubscribeToSettingsChanges()
        {
            ((App)Application.Current).SettingsManager.AppliedSettingsChanged += SettingsChangedHandler;
        }

        // Handles the Loaded event of the page
        private async void RecordPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_managersInitialized)
                return;

            // Initialize managers asynchronously
            await InitializeManagersAsync();
            _managersInitialized = true;

            _stopwatch.Start();
            SetupCameraEvents();
            await InitializeCamera();
        }

        private Task InitializeManagersAsync()
        {
            var settings = ((App)Application.Current).SettingsManager.AppliedSettings;
            _graphManager = new GraphManager(ThrustGraph, PressureGraph, settings);
            _labjackManager = LabJackManager.Instance; // Ensure LabJackManager is thread-safe
            if (_labjackManager.IsDemo())
            {
                ControlPanelTitle.Text = "Control Panel — DEMO SIMULATION";
                ControlPanelTitle.Foreground = Brushes.Orange;
            }
            _webcamManager = new WebcamManager(Dispatcher);
            if (_currentSessionFolder == null)
                _dataRecorder = new DataRecorder();
            _countdownService = new CountdownService(Dispatcher);

            // Subscribe to events
            _labjackManager.DataUpdated += UpdateGraphs;
            _labjackManager.BatchDataUpdated += OnBatchDataUpdated;
            _labjackManager.StreamingFailed += OnStreamingFailed;
            _countdownService.CountdownUpdated += UpdateCountdownDisplay;
            _countdownService.CountdownFinished += HandleCountdownCompletion;

            SetupTimer();
            SubscribeToSettingsChanges();
            _graphUpdateStopwatch.Start();

            return Task.CompletedTask; // Return completed task
        }

        // Initializes the camera
        private async Task InitializeCamera()
        {
            try
            {
                LoadingTextBlock.Visibility = Visibility.Visible; // Show loading text
                var settings = ((App)Application.Current).SettingsManager.AppliedSettings;
                await _webcamManager.InitializeAsync(_labjackManager.IsDemo(), settings.RtspUrl);
                LoadingTextBlock.Visibility = Visibility.Collapsed; // Hide only on success
            }
            catch (Exception ex)
            {
                LoadingTextBlock.Text = $"Camera Error: {ex.Message}"; // Show detailed error
                LoadingTextBlock.Visibility = Visibility.Visible;
            }
        }

        // Sets up camera events
        private void SetupCameraEvents()
        {
            _webcamManager.FrameReceived += bitmap => WebcamImage.Source = bitmap; // Update webcam image
            _webcamManager.CameraError += message =>
                Dispatcher.Invoke(() => LoadingTextBlock.Text = message); // Show camera error message

        }

        // Updates the graphs with new data points
        private void UpdateGraphs(double thrustVoltage, double thrust, double pressureVoltage, double pressure)
        {
            if (_isSaving || _labjackManager.IsStreaming) return; // Skip if saving or if handled by streaming batches

            var elapsedTime = _stopwatch.Elapsed.TotalSeconds; // Get elapsed time

            // Immediately record data points without waiting for graph intervals or blocking UI thread
            if (_isRecording)
            {
                _dataRecorder.AddDataPoint(elapsedTime, thrust, pressure, thrustVoltage, pressureVoltage);
            }

            // Store data point in queue for graph update
            _dataQueue.Enqueue((elapsedTime, thrust, pressure, thrustVoltage, pressureVoltage));

            // Only update graphs at specified frame rate using non-blocking dispatch and batching
            if (_graphUpdateStopwatch.ElapsedMilliseconds >= 1000 / graphFPS)
            {
                _graphUpdateStopwatch.Restart();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var points = new List<(double time, double thrust, double pressure)>();
                    while (_dataQueue.TryDequeue(out var dataPoint))
                    {
                        points.Add((dataPoint.time, dataPoint.thrust, dataPoint.pressure));
                    }
                    if (points.Count > 0)
                    {
                        _graphManager.AddDataPoints(points);
                    }
                }));
            }
        }

        private void OnBatchDataUpdated(DataPointRecord[] batch)
        {
            if (_isSaving || batch.Length == 0) return;

            if (_isRecording)
            {
                _dataRecorder.AddDataPoints(batch);
            }

            for (int i = 0; i < batch.Length; i++)
            {
                _dataQueue.Enqueue((batch[i].Time, batch[i].Thrust, batch[i].Pressure, batch[i].ThrustVoltage, batch[i].PressureVoltage));
            }

            if (_graphUpdateStopwatch.ElapsedMilliseconds >= 1000 / graphFPS)
            {
                _graphUpdateStopwatch.Restart();
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    var points = new List<(double time, double thrust, double pressure)>();
                    while (_dataQueue.TryDequeue(out var dataPoint))
                    {
                        points.Add((dataPoint.time, dataPoint.thrust, dataPoint.pressure));
                    }
                    if (points.Count > 0)
                    {
                        _graphManager.AddDataPoints(points);
                    }
                }));
            }
        }

        private void OnStreamingFailed(string message)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                _recordingFailureReason = message;
                LoadingTextBlock.Text = $"Sensor acquisition failed: {message}";
                LoadingTextBlock.Visibility = Visibility.Visible;

                if (_isRecording)
                {
                    bool saved = StopTestAndSave(showPrompt: false);
                    MessageBox.Show(
                        saved
                            ? $"Sensor acquisition failed. Recording stopped and partial data was exported as failed.\n\n{message}"
                            : $"Sensor acquisition failed. Recording stopped, but partial data is still pending export. Use Retry Save to try again.\n\n{message}",
                        "Recording failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }));
        }

        // Updates the countdown display
        private void UpdateCountdownDisplay(string timeText)
        {
            StartTestTextBlock.Text = timeText; // Update countdown text
        }

        // Handles the completion of the countdown
        private void HandleCountdownCompletion()
        {
            Dispatcher.Invoke(() =>
            {
                // Strict safety check: Never ignite or record if not armed or already recording
                if (!_labjackManager.GetArmedStatus() || _isRecording)
                {
                    CancelCountdownAndResetStartButton();
                    return;
                }

                StartTestTextBlock.Text = "Igniting Motor!"; // Update text to indicate motor ignition
                _labjackManager.IgniteMotor(); // Ignite the motor
                StartTestRecording(); // Start recording after ignition
            });
        }

        // Handles the click event of the Arm button
        private void ArmButton_Click(object sender, RoutedEventArgs e)
        {
            // If countdown is active, immediately cancel it and reset the Start button
            if (_countdownService.IsRunning)
            {
                CancelCountdownAndResetStartButton();
            }

            // If currently recording and user presses Arm/Disarm, stop recording and ensure data is saved
            if (_isRecording)
            {
                if (!StopTestAndSave(showPrompt: true))
                    return;
            }
            else if (_currentSessionFolder != null)
            {
                RetryPendingSave();
                return;
            }

            _labjackManager.ArmDisarmIgniter(); // Arm or disarm the igniter
            UpdateArmingUI(_labjackManager.GetArmedStatus()); // Update the UI to reflect the arming status
        }

        // Updates the UI to reflect the arming status
        private void UpdateArmingUI(bool isArmed)
        {
            ArmButton.Background = isArmed ? Brushes.Red : Brushes.Green;
            ArmTextBlock.Text = isArmed ? "Disarm" : "Arm"; // Update button text
            StartButton.Background = isArmed ? Brushes.Orange : Brushes.DarkGray; // Change start button color
            StartTestTextBlock.TextDecorations = isArmed ? null : TextDecorations.Strikethrough;
            if (!isArmed && !_isRecording)
            {
                StartTestTextBlock.Text = "Start";
            }
        }

        // Handles the click event of the Start Test button
        private void StartTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording && _currentSessionFolder != null)
            {
                RetryPendingSave();
                return;
            }

            if (_labjackManager.GetArmedStatus() && !_isRecording)
            {
                _countdownService.ToggleCountdown(5);
            }
            else if (_isRecording)
            {
                StopTestAndSave(showPrompt: true);
            }
        }

        /// <summary>
        /// Cancels any active countdown and restores the Start button text and appearance.
        /// </summary>
        private void CancelCountdownAndResetStartButton()
        {
            _countdownService.CancelCountdown();
            StartTestTextBlock.Text = "Start";
            StartButton.Background = _labjackManager.GetArmedStatus() ? Brushes.Orange : Brushes.DarkGray;
            StartTestTextBlock.TextDecorations = _labjackManager.GetArmedStatus() ? null : TextDecorations.Strikethrough;
        }

        /// <summary>
        /// Stops active recording, safely saves sensor data & video, and resets UI state.
        /// </summary>
        /// <param name="showPrompt">Whether to show a message box summarizing the save.</param>
        private bool StopTestAndSave(bool showPrompt)
        {
            if (!_isRecording && _currentSessionFolder == null)
            {
                return true;
            }

            _isRecording = false;
            ((App)Application.Current).SettingsManager.SetRecordingLock(false);
            _labjackManager.StopStreaming();

            if (_isVideoRecording)
            {
                try
                {
                    _webcamManager.StopRecording();
                }
                catch { /* Ignore camera shutdown errors during save */ }
            }
            _isVideoRecording = false;

            StartTestTextBlock.Text = "Start";
            StartButton.Background = _labjackManager.GetArmedStatus() ? Brushes.Orange : Brushes.DarkGray;

            // Stop data recorder to flush and close temporary stream before exporting
            _dataRecorder.StopRecording();

            if (_currentSessionFolder != null)
            {
                if (!SaveData(showPrompt))
                {
                    StartTestTextBlock.Text = "Retry Save";
                    return false;
                }
            }

            ClearSessionState();
            return true;
        }

        private void RetryPendingSave()
        {
            if (SaveData(showPrompt: true))
                ClearSessionState();
        }

        private void ClearSessionState()
        {
            _currentSessionFolder = null;
            _currentVideoPath = null;
            _recordingFailureReason = null;
            StartTestTextBlock.Text = "Start";
            StartButton.Background = _labjackManager.GetArmedStatus() ? Brushes.Orange : Brushes.DarkGray;
        }

        private bool SaveData(bool showPrompt = true)
        {
            if (_currentSessionFolder == null) return false;

            try
            {
                _isSaving = true;
                var jsonFileName = $"data_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
                var jsonPath = Path.Combine(_currentSessionFolder, jsonFileName);

                // The recorder keeps its temporary data until an export succeeds, so the user can retry a failed save.
                while (true)
                {
                    try
                    {
                        _dataRecorder.ExportToJson(jsonPath, GetRelativeVideoPath(), _recordingFailureReason);
                        break;
                    }
                    catch (Exception ex)
                    {
                        var retry = MessageBox.Show(
                            $"Error saving data: {ex.Message}\n\nThe recorded data is still kept in:\n{_currentSessionFolder}\n\nRetry saving?",
                            "Error", MessageBoxButton.YesNo, MessageBoxImage.Error);
                        if (retry != MessageBoxResult.Yes)
                        {
                            return false;
                        }
                    }
                }

                if (showPrompt)
                {
                    MessageBox.Show(GetSaveSummary(), "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return true;
            }
            finally
            {
                _isSaving = false;
            }
        }

        // Handles the Unloaded event of the page
        private void RecordPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // Immediately cancel any pending countdown
            _countdownService.CancelCountdown();

            // Ensure any active recording is safely saved if the user navigates away or closes the page
            bool saved = true;
            if (_isRecording || _currentSessionFolder != null)
            {
                saved = StopTestAndSave(showPrompt: false);
            }

            // Cleanup resources
            if (saved)
                _dataRecorder.Dispose();
            _stopwatch.Reset();
            _webcamManager.Dispose();
            _labjackManager.StopStreaming();
            _countdownService.Dispose();

            // Unsubscribe events
            ((App)Application.Current).SettingsManager.AppliedSettingsChanged -= SettingsChangedHandler;
            _labjackManager.DataUpdated -= UpdateGraphs;
            _labjackManager.BatchDataUpdated -= OnBatchDataUpdated;
            _labjackManager.StreamingFailed -= OnStreamingFailed;
            _managersInitialized = false;
        }

        // Handles settings changes
        private async void SettingsChangedHandler(object? sender, EventArgs e)
        {
            try
            {
                var settings = ((App)Application.Current).SettingsManager.AppliedSettings;
                // Show loading text and reset message
                Dispatcher.Invoke(() =>
                {
                    _graphManager.SetSensorRanges(settings);
                    LoadingTextBlock.Text = "Loading...";
                    LoadingTextBlock.Visibility = Visibility.Visible;
                });

                // Reinitialize webcam with new settings
                await _webcamManager.ReinitializeAsync(
                    _labjackManager.IsDemo(),
                    settings.RtspUrl
                );

                // Hide loading text on success
                Dispatcher.Invoke(() => LoadingTextBlock.Visibility = Visibility.Collapsed);
            }
            catch (Exception ex)
            {
                // Show error message
                Dispatcher.Invoke(() =>
                {
                    LoadingTextBlock.Text = $"Camera Error: {ex.Message}";
                    LoadingTextBlock.Visibility = Visibility.Visible;
                });
            }
        }

        private void StartTestRecording()
        {
            try
            {
                // Create timestamped folder
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                _currentSessionFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "RocketTests",
                    timestamp);

                Directory.CreateDirectory(_currentSessionFolder);

                _dataRecorder.StartRecording(_currentSessionFolder);
                _isRecording = true;
                _recordingFailureReason = null;
                ((App)Application.Current).SettingsManager.SetRecordingLock(true);
                var appliedSettings = ((App)Application.Current).SettingsManager.AppliedSettings;
                _labjackManager.StartStreaming(appliedSettings.SamplingRateHz);
                StartTestTextBlock.Text = "Stop Recording";
                StartButton.Background = Brushes.Red;

                var videoFileName = $"test_{timestamp}.mp4";
                _currentVideoPath = Path.Combine(_currentSessionFolder, videoFileName);
                try
                {
                    _webcamManager.StartRecording(_currentVideoPath);
                    _isVideoRecording = true;
                }
                catch (Exception exception)
                {
                    LoadingTextBlock.Text = $"Camera unavailable; sensor data is still recording. {exception.Message}";
                    LoadingTextBlock.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                _isRecording = false;
                ((App)Application.Current).SettingsManager.SetRecordingLock(false);
                _labjackManager.StopStreaming();
                _dataRecorder.StopRecording();
                MessageBox.Show($"Error starting sensor data recording: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetSaveSummary()
        {
            bool videoSaved = _currentVideoPath != null && File.Exists(_currentVideoPath) && new FileInfo(_currentVideoPath).Length > 0;
            return videoSaved
                ? $"Sensor data and video saved in:\n{_currentSessionFolder}"
                : $"Sensor data saved in:\n{_currentSessionFolder}\nNo camera video was recorded.";
        }

        private string? GetRelativeVideoPath()
        {
            return _currentVideoPath != null && File.Exists(_currentVideoPath) && new FileInfo(_currentVideoPath).Length > 0
                ? Path.GetFileName(_currentVideoPath)
                : null;
        }

        
    }
}