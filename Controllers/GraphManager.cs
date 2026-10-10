using ScottPlot;
using ScottPlot.Plottables;
using ScottPlot.WPF;
using System.Collections.Generic;

namespace Project_FREAK.Controllers
{
    public class GraphManager
    {
        private readonly DataStreamer _thrustStreamer;
        private readonly DataStreamer _pressureStreamer;
        private readonly WpfPlot _thrustGraph;
        private readonly WpfPlot _pressureGraph;
        private const int WindowSize = 1000; // Resolution of display buffer in data points
        private int _windowSeconds = 15; // Relative time window in seconds
        private double _lastAddedTime = -1;
        private double _thrustFullScaleNewtons;
        private double _pressureFullScalePsi;
        private double _thrustMinimum;
        private double _thrustMaximum;
        private double _pressureMinimum;
        private double _pressureMaximum;

        public int WindowSeconds => _windowSeconds;

        public GraphManager(WpfPlot thrustGraph, WpfPlot pressureGraph, SettingsManager.SettingsData? settings = null)
        {
            _thrustGraph = thrustGraph;
            _pressureGraph = pressureGraph;

            // Initialize the graphs with titles and labels
            _thrustStreamer = InitializeGraph(_thrustGraph, "Thrust Over Time", "Thrust (N)");
            _pressureStreamer = InitializeGraph(_pressureGraph, "Pressure Over Time", "Pressure (PSI)");
            SetSensorRanges(settings ?? new SettingsManager.SettingsData());
        }

        public void SetTimeWindow(int windowSeconds)
        {
            _windowSeconds = Math.Clamp(windowSeconds, 2, 300);
            double period = (double)_windowSeconds / WindowSize;

            _thrustStreamer.Period = period;
            _thrustStreamer.Data.OffsetX = -_windowSeconds;
            _pressureStreamer.Period = period;
            _pressureStreamer.Data.OffsetX = -_windowSeconds;

            _thrustGraph.Plot.Axes.SetLimitsX(-_windowSeconds, 0);
            _pressureGraph.Plot.Axes.SetLimitsX(-_windowSeconds, 0);

            RefreshGraphs();
        }

        public void SetSensorRanges(SettingsManager.SettingsData settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _thrustFullScaleNewtons = settings.GetLoadCellFullScaleNewtons();
            _pressureFullScalePsi = double.IsFinite(settings.PressureFullScaleValue) && settings.PressureFullScaleValue > 0
                ? settings.PressureFullScaleValue
                : 1600;

            SetTimeWindow(settings.LiveGraphWindowSeconds);
            UpdateAxisLimits();
        }

        // Initializes a graph with a title and y-axis label
        private DataStreamer InitializeGraph(WpfPlot graph, string title, string yLabel)
        {
            graph.Plot.Title(title);
            graph.Plot.Axes.Bottom.Label.Text = "Time (s)";
            graph.Plot.Axes.Left.Label.Text = yLabel;
            graph.Plot.Axes.ContinuouslyAutoscale = false;

            var streamer = graph.Plot.Add.DataStreamer(WindowSize);
            streamer.ViewScrollLeft();
            streamer.ManageAxisLimits = false;

            return streamer;
        }

        // Adds a data point to the thrust and pressure graphs
        public void AddDataPoint(double time, double thrust, double pressure)
        {
            _thrustStreamer.Add(thrust);
            _pressureStreamer.Add(pressure);
            TrackObservedRange(thrust, pressure);

            RefreshGraphs(); // Refresh the graphs to display the new data
        }

        // Adds multiple data points and refreshes the graphs once to minimize UI thread overhead
        public void AddDataPoints(IEnumerable<(double time, double thrust, double pressure)> points)
        {
            var list = points as IList<(double time, double thrust, double pressure)> ?? points.ToList();
            if (list.Count == 0) return;

            // Track observed range across ALL points so peaks and extremes are never missed
            for (int i = 0; i < list.Count; i++)
            {
                TrackObservedRange(list[i].thrust, list[i].pressure);
            }

            double period = (double)_windowSeconds / WindowSize;
            double latestTime = list[^1].time;

            if (_lastAddedTime < 0 || latestTime < _lastAddedTime)
            {
                _lastAddedTime = list[0].time;
            }

            // Guard against large pauses
            if (latestTime - _lastAddedTime > _windowSeconds)
            {
                _lastAddedTime = latestTime - _windowSeconds;
            }

            double elapsed = latestTime - _lastAddedTime;
            int steps = (int)(elapsed / period);

            if (steps <= 0 && _thrustStreamer.CountTotal == 0)
            {
                steps = 1;
            }

            if (steps > 0)
            {
                steps = Math.Min(steps, WindowSize);
                if (steps == 1)
                {
                    // Pick the point with greatest peak magnitude
                    double repThrust = list[^1].thrust;
                    double repPressure = list[^1].pressure;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (Math.Abs(list[i].thrust) > Math.Abs(repThrust)) repThrust = list[i].thrust;
                        if (Math.Abs(list[i].pressure) > Math.Abs(repPressure)) repPressure = list[i].pressure;
                    }
                    _thrustStreamer.Add(repThrust);
                    _pressureStreamer.Add(repPressure);
                    _lastAddedTime += period;
                }
                else
                {
                    double stepSpan = (double)(list.Count - 1) / (steps - 1);
                    for (int s = 0; s < steps; s++)
                    {
                        int idx = Math.Clamp((int)Math.Round(s * stepSpan), 0, list.Count - 1);
                        _thrustStreamer.Add(list[idx].thrust);
                        _pressureStreamer.Add(list[idx].pressure);
                    }
                    _lastAddedTime += steps * period;
                }
            }

            UpdateAxisLimits();
            RefreshGraphs();
        }

        // Refreshes the graphs to display the latest data
        public void RefreshGraphs()
        {
            _thrustGraph.Refresh();
            _pressureGraph.Refresh();
        }

        // Clears the data from the graphs
        public void ClearGraphs()
        {
            _thrustStreamer.Clear();
            _pressureStreamer.Clear();
            _thrustMinimum = _thrustMaximum = 0;
            _pressureMinimum = _pressureMaximum = 0;
            _lastAddedTime = -1;
            UpdateAxisLimits();
            RefreshGraphs();
        }

        private void TrackObservedRange(double thrust, double pressure)
        {
            _thrustMinimum = Math.Min(_thrustMinimum, thrust);
            _thrustMaximum = Math.Max(_thrustMaximum, thrust);
            _pressureMinimum = Math.Min(_pressureMinimum, pressure);
            _pressureMaximum = Math.Max(_pressureMaximum, pressure);
            UpdateAxisLimits();
        }

        private void UpdateAxisLimits()
        {
            _thrustGraph.Plot.Axes.SetLimitsX(-_windowSeconds, 0);
            _thrustGraph.Plot.Axes.SetLimitsY(Math.Min(0, _thrustMinimum), GetUpperLimit(_thrustFullScaleNewtons, _thrustMaximum));

            _pressureGraph.Plot.Axes.SetLimitsX(-_windowSeconds, 0);
            _pressureGraph.Plot.Axes.SetLimitsY(Math.Min(0, _pressureMinimum), GetUpperLimit(_pressureFullScalePsi, _pressureMaximum));
        }

        private static double GetUpperLimit(double fullScale, double observedMaximum) =>
            observedMaximum > fullScale ? observedMaximum * 1.05 : fullScale;
    }
}
