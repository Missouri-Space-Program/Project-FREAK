using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Project_FREAK.Controllers;

namespace Project_FREAK.Views;

public partial class ReviewDataPage : Page
{
    private readonly DispatcherTimer _videoTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ReviewDataset? _dataset;
    private string? _loadedJsonPath;
    private string? _videoPath;
    private bool _updatingVideoSlider;
    private bool _videoEnded;
    private double _intervalStartSeconds;
    private double _intervalEndSeconds;
    private ScottPlot.Plottables.VerticalLine? _thrustStartLine;
    private ScottPlot.Plottables.VerticalLine? _thrustEndLine;
    private ScottPlot.Plottables.VerticalLine? _pressureStartLine;
    private ScottPlot.Plottables.VerticalLine? _pressureEndLine;
    private IntervalEndpoint? _draggingIntervalEndpoint;

    private enum IntervalEndpoint
    {
        Start,
        End
    }

    public ReviewDataPage()
    {
        InitializeComponent();
        _videoTimer.Tick += VideoTimer_Tick;
        ((App)Application.Current).SettingsManager.AppliedSettingsChanged += SettingsChangedHandler;
        Unloaded += ReviewDataPage_Unloaded;
    }

    private void OpenJsonButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Project FREAK test data",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            LoadDataset(dialog.FileName);
    }

    private void OpenVideoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose test video",
            Filter = "MP4 videos (*.mp4)|*.mp4|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            LoadVideo(dialog.FileName);
    }

    private void LoadDataset(string jsonPath)
    {
        try
        {
            _loadedJsonPath = jsonPath;
            var settings = ((App)Application.Current).SettingsManager.AppliedSettings;
            _dataset = ReviewDataLoader.Load(jsonPath, settings);
            ExportEngButton.IsEnabled = true;
            ConfigureIntervalSelection();
            RenderGraphs();
            UpdateStatistics();
            ReviewStatusText.Text = $"Loaded {Path.GetFileName(jsonPath)} ({_dataset.TimeSeconds.Length} samples).";
            ConversionNoticeText.Text = _dataset.ConversionDescription;
            LoadVideo(_dataset.VideoPath);
        }
        catch (Exception exception)
        {
            ExportEngButton.IsEnabled = false;
            ReviewStatusText.Text = $"Unable to load data: {exception.Message}";
            ConversionNoticeText.Text = string.Empty;
        }
    }

    private void ExportEngButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset is null)
            return;

        var dialog = new RaspExportDialog(_dataset, _intervalStartSeconds, _intervalEndSeconds, _loadedJsonPath)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ExportedFilePath))
        {
            ConversionNoticeText.Text = $"Successfully exported RASP motor data to {Path.GetFileName(dialog.ExportedFilePath)}.";
        }
    }

    private void SettingsChangedHandler(object? sender, EventArgs e)
    {
        if (_loadedJsonPath is null)
            return;

        Dispatcher.BeginInvoke(() => LoadDataset(_loadedJsonPath));
    }

    private void ConfigureIntervalSelection()
    {
        if (_dataset is null)
            return;

        _intervalStartSeconds = _dataset.TimeSeconds[0];
        _intervalEndSeconds = _dataset.TimeSeconds[^1];
    }

    private void RenderGraphs()
    {
        if (_dataset is null)
            return;

        ThrustPlot.Plot.Clear();
        var thrustScatter = ThrustPlot.Plot.Add.Scatter(_dataset.TimeSeconds, _dataset.ThrustNewtons);
        if (_dataset.TimeSeconds.Length > 2000)
            thrustScatter.MarkerSize = 0;
        ThrustPlot.Plot.Title("Thrust over time");
        ThrustPlot.Plot.Axes.Bottom.Label.Text = "Time (s)";
        ThrustPlot.Plot.Axes.Left.Label.Text = "Thrust (N)";
        ThrustPlot.Plot.Axes.AutoScale();
        var thrustLimits = GetYAxisLimits(_dataset.ThrustNewtons.Min(), _dataset.ThrustNewtons.Max());
        ThrustPlot.Plot.Axes.SetLimitsY(thrustLimits.Minimum, thrustLimits.Maximum);
        _thrustStartLine = AddIntervalLine(ThrustPlot, _intervalStartSeconds, ScottPlot.Colors.Green);
        _thrustEndLine = AddIntervalLine(ThrustPlot, _intervalEndSeconds, ScottPlot.Colors.Red);
        ThrustPlot.Refresh();

        PressurePlot.Plot.Clear();
        var pressureScatter = PressurePlot.Plot.Add.Scatter(_dataset.TimeSeconds, _dataset.PressurePsi);
        if (_dataset.TimeSeconds.Length > 2000)
            pressureScatter.MarkerSize = 0;
        PressurePlot.Plot.Title("Pressure over time");
        PressurePlot.Plot.Axes.Bottom.Label.Text = "Time (s)";
        PressurePlot.Plot.Axes.Left.Label.Text = "Pressure (PSI)";
        PressurePlot.Plot.Axes.AutoScale();
        var pressureLimits = GetYAxisLimits(_dataset.PressurePsi.Min(), _dataset.PressurePsi.Max());
        PressurePlot.Plot.Axes.SetLimitsY(pressureLimits.Minimum, pressureLimits.Maximum);
        _pressureStartLine = AddIntervalLine(PressurePlot, _intervalStartSeconds, ScottPlot.Colors.Green);
        _pressureEndLine = AddIntervalLine(PressurePlot, _intervalEndSeconds, ScottPlot.Colors.Red);
        PressurePlot.Refresh();
    }

    internal static (double Minimum, double Maximum) GetYAxisLimits(double observedMinimum, double observedMaximum)
    {
        double minimum = Math.Min(0, observedMinimum);
        if (minimum < 0)
            minimum *= 1.05;

        double maximum = observedMaximum > 0
            ? observedMaximum * 1.05
            : observedMinimum == 0 ? 1 : 0;

        return (minimum, maximum);
    }

    private static ScottPlot.Plottables.VerticalLine AddIntervalLine(
        ScottPlot.WPF.WpfPlot plot,
        double time,
        ScottPlot.Color color)
    {
        var line = plot.Plot.Add.VerticalLine(time);
        line.LineColor = color;
        line.LineWidth = 2;
        return line;
    }

    private void Plot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dataset is null || sender is not ScottPlot.WPF.WpfPlot plot || _dataset.TimeSeconds.Length < 2)
            return;

        double time = plot.Plot.GetCoordinates(plot.GetPlotPixelPosition(e)).X;
        double tolerance = Math.Max((_dataset.TimeSeconds[^1] - _dataset.TimeSeconds[0]) * 0.02, 0.01);
        if (Math.Abs(time - _intervalStartSeconds) <= tolerance)
            _draggingIntervalEndpoint = IntervalEndpoint.Start;
        else if (Math.Abs(time - _intervalEndSeconds) <= tolerance)
            _draggingIntervalEndpoint = IntervalEndpoint.End;
        else
            return;

        plot.CaptureMouse();
        e.Handled = true;
    }

    private void Plot_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingIntervalEndpoint is null || _dataset is null || sender is not ScottPlot.WPF.WpfPlot plot)
            return;

        double time = plot.Plot.GetCoordinates(plot.GetPlotPixelPosition(e)).X;
        UpdateIntervalEndpoint(time);
        e.Handled = true;
    }

    private void Plot_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingIntervalEndpoint is null || sender is not ScottPlot.WPF.WpfPlot plot)
            return;

        _draggingIntervalEndpoint = null;
        plot.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateIntervalEndpoint(double time)
    {
        if (_dataset is null)
            return;

        time = Math.Clamp(time, _dataset.TimeSeconds[0], _dataset.TimeSeconds[^1]);
        if (_draggingIntervalEndpoint == IntervalEndpoint.Start)
        {
            _intervalStartSeconds = time;
            if (time > _intervalEndSeconds)
                _intervalEndSeconds = time;
        }
        else
        {
            _intervalEndSeconds = time;
            if (time < _intervalStartSeconds)
                _intervalStartSeconds = time;
        }

        if (_thrustStartLine is not null)
            _thrustStartLine.Position = _intervalStartSeconds;
        if (_thrustEndLine is not null)
            _thrustEndLine.Position = _intervalEndSeconds;
        if (_pressureStartLine is not null)
            _pressureStartLine.Position = _intervalStartSeconds;
        if (_pressureEndLine is not null)
            _pressureEndLine.Position = _intervalEndSeconds;

        ThrustPlot.Refresh();
        PressurePlot.Refresh();
        UpdateStatistics();
    }

    private void UpdateStatistics()
    {
        if (_dataset is null)
            return;

        double[] time = _dataset.TimeSeconds;
        double[] thrust = _dataset.ThrustNewtons;
        double[] pressure = _dataset.PressurePsi;
        OverallStatsText.Text = FormatStatistics("Entire recording", time, thrust, pressure);

        double start = _intervalStartSeconds;
        double end = _intervalEndSeconds;
        if (time.Length < 2 || !double.IsFinite(start) || !double.IsFinite(end) ||
            start >= end || start < time[0] || end > time[^1])
        {
            IntervalStatsText.Text = "Selected interval\nNo samples in this interval.";
            return;
        }

        int selectedSampleCount = time.Count(sampleTime => sampleTime >= start && sampleTime <= end);
        IntervalStatsText.Text = FormatStatistics("Selected interval", time, thrust, pressure, selectedSampleCount, start, end);
    }

    private static string FormatStatistics(
        string heading,
        IReadOnlyList<double> time,
        IReadOnlyList<double> thrust,
        IReadOnlyList<double> pressure,
        int? sampleCount = null,
        double? intervalStartSeconds = null,
        double? intervalEndSeconds = null)
    {
        double duration = intervalStartSeconds.HasValue && intervalEndSeconds.HasValue
            ? intervalEndSeconds.Value - intervalStartSeconds.Value
            : time.Count > 1 ? time[^1] - time[0] : 0;
        IReadOnlyList<double> statsTime = time;
        IReadOnlyList<double> statsThrust = thrust;
        IReadOnlyList<double> statsPressure = pressure;
        if (intervalStartSeconds.HasValue && intervalEndSeconds.HasValue)
        {
            (double[] selectedTime, double[] selectedThrust) = CubicSplineIntegrator.ClipInterval(
                time, thrust, intervalStartSeconds.Value, intervalEndSeconds.Value);
            (_, double[] selectedPressure) = CubicSplineIntegrator.ClipInterval(
                time, pressure, intervalStartSeconds.Value, intervalEndSeconds.Value);
            statsTime = selectedTime;
            statsThrust = selectedThrust;
            statsPressure = selectedPressure;
        }

        double thrustMax = double.NegativeInfinity;
        for (int i = 0; i < statsThrust.Count; i++)
        {
            double v = statsThrust[i];
            if (v > thrustMax) thrustMax = v;
        }
        double maximumThrust = statsThrust.Count > 0 ? thrustMax : 0;
        (double totalImpulse, double averageThrust) = intervalStartSeconds.HasValue && intervalEndSeconds.HasValue
            ? CubicSplineIntegrator.IntegrateAndAverage(time, thrust, intervalStartSeconds.Value, intervalEndSeconds.Value)
            : CubicSplineIntegrator.IntegrateAndAverage(time, thrust);

        double pressureMax = double.NegativeInfinity;
        for (int i = 0; i < statsPressure.Count; i++)
        {
            double v = statsPressure[i];
            if (v > pressureMax) pressureMax = v;
        }
        double maximumPressure = statsPressure.Count > 0 ? pressureMax : 0;
        var (_, averagePressure) = intervalStartSeconds.HasValue && intervalEndSeconds.HasValue
            ? CubicSplineIntegrator.IntegrateAndAverage(time, pressure, intervalStartSeconds.Value, intervalEndSeconds.Value)
            : CubicSplineIntegrator.IntegrateAndAverage(time, pressure);

        string motorDesignation = MotorDesignation.Format(totalImpulse, averageThrust);

        return $"{heading}\nSamples: {sampleCount ?? time.Count}    Duration: {duration:F2} s\n" +
               $"Average thrust: {averageThrust:F2} N    Peak thrust: {maximumThrust:F2} N\n" +
               $"Average pressure: {averagePressure:F2} PSI    Peak pressure: {maximumPressure:F2} PSI\n" +
               $"Total impulse: {totalImpulse:F2} N·s\n" +
               $"Motor Designation: {motorDesignation}";
    }

    private void LoadVideo(string? path)
    {
        _videoTimer.Stop();
        _videoEnded = false;
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        _videoPath = path;
        PlayPauseButton.IsEnabled = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        VideoSeekSlider.IsEnabled = false;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            VideoStatusText.Text = "No matching video found. Use Choose Video to browse.";
            return;
        }

        VideoPlayer.Source = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        VideoStatusText.Text = Path.GetFileName(path);
        PlayPauseButton.Content = "Play";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_videoPath) || !File.Exists(_videoPath))
            return;

        if (_videoTimer.IsEnabled)
        {
            VideoPlayer.Pause();
            _videoTimer.Stop();
            PlayPauseButton.Content = "Play";
        }
        else
        {
            if (_videoEnded)
            {
                VideoPlayer.Position = TimeSpan.Zero;
                _videoEnded = false;
            }

            VideoPlayer.Play();
            _videoTimer.Start();
            PlayPauseButton.Content = "Pause";
        }
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            _updatingVideoSlider = true;
            VideoSeekSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            VideoSeekSlider.Value = 0;
            VideoSeekSlider.IsEnabled = true;
            _updatingVideoSlider = false;
        }
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoPlayer.Pause();
        _videoTimer.Stop();
        _videoEnded = true;
        if (VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            _updatingVideoSlider = true;
            VideoSeekSlider.Value = VideoSeekSlider.Maximum;
            _updatingVideoSlider = false;
        }
        PlayPauseButton.Content = "Play";
    }

    private void VideoSeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingVideoSlider && VideoPlayer.Source is not null && VideoSeekSlider.IsEnabled)
        {
            TimeSpan seekPosition = TimeSpan.FromSeconds(VideoSeekSlider.Value);
            if (_videoEnded && VideoPlayer.NaturalDuration.HasTimeSpan && seekPosition < VideoPlayer.NaturalDuration.TimeSpan)
                _videoEnded = false;
            VideoPlayer.Position = seekPosition;
        }
    }

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (VideoPlayer.Source is null)
            return;

        _updatingVideoSlider = true;
        VideoSeekSlider.Value = Math.Min(VideoPlayer.Position.TotalSeconds, VideoSeekSlider.Maximum);
        _updatingVideoSlider = false;
    }

    private void ReviewDataPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _videoTimer.Stop();
        VideoPlayer.Stop();
        ((App)Application.Current).SettingsManager.AppliedSettingsChanged -= SettingsChangedHandler;
    }
}
