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
        private const int EntriesPerColumn = 2; // Each display column adds a minimum and a maximum
        private readonly List<(double time, double thrust, double pressure)> _pendingPoints = new(); // Points waiting for their column to complete
        private double _lastThrust; // Most recent plotted values, held across empty columns
        private double _lastPressure;
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

        // Adds multiple data points. Each display column adds the minimum and maximum of its time span,
        // so brief peaks stay visible when a batch is reduced to the display resolution.
        public void AddDataPoints(IEnumerable<(double time, double thrust, double pressure)> points)
        {
            var list = points as IList<(double time, double thrust, double pressure)> ?? points.ToList();
            if (list.Count == 0) return;

            // Track observed range across ALL points so peaks and extremes are never missed
            for (int i = 0; i < list.Count; i++)
            {
                TrackObservedRange(list[i].thrust, list[i].pressure);
            }

            double latestTime = list[^1].time;
            if (_lastAddedTime < 0 || latestTime < _lastAddedTime)
            {
                // First data, or the time base restarted: begin a new column sequence at this batch
                _pendingPoints.Clear();
                _lastAddedTime = list[0].time;
            }
            _pendingPoints.AddRange(list);

            // Guard against large pauses by skipping ahead to the visible window
            if (latestTime - _lastAddedTime > _windowSeconds)
            {
                _lastAddedTime = latestTime - _windowSeconds;
            }
            _pendingPoints.RemoveAll(point => point.time < _lastAddedTime);

            // A column is added only after its whole time span has arrived, so no points are split or dropped between batches
            double period = (double)_windowSeconds / WindowSize;
            double columnSpan = EntriesPerColumn * period;
            int columns = (int)Math.Clamp((latestTime - _lastAddedTime) / columnSpan, 0, WindowSize / EntriesPerColumn);

            int next = 0; // Index of the first pending point not yet consumed
            for (int column = 0; column < columns; column++)
            {
                double columnEnd = _lastAddedTime + (column + 1) * columnSpan;
                int first = next;
                while (next < _pendingPoints.Count && _pendingPoints[next].time < columnEnd)
                {
                    next++;
                }

                if (next > first)
                {
                    double minThrust = double.PositiveInfinity, maxThrust = double.NegativeInfinity;
                    double minPressure = double.PositiveInfinity, maxPressure = double.NegativeInfinity;
                    for (int i = first; i < next; i++)
                    {
                        minThrust = Math.Min(minThrust, _pendingPoints[i].thrust);
                        maxThrust = Math.Max(maxThrust, _pendingPoints[i].thrust);
                        minPressure = Math.Min(minPressure, _pendingPoints[i].pressure);
                        maxPressure = Math.Max(maxPressure, _pendingPoints[i].pressure);
                    }

                    _thrustStreamer.Add(minThrust);
                    _thrustStreamer.Add(maxThrust);
                    _pressureStreamer.Add(minPressure);
                    _pressureStreamer.Add(maxPressure);

                    _lastThrust = _pendingPoints[next - 1].thrust;
                    _lastPressure = _pendingPoints[next - 1].pressure;
                }
                else
                {
                    // No samples in this span: hold the last value so the time axis stays aligned
                    for (int i = 0; i < EntriesPerColumn; i++)
                    {
                        _thrustStreamer.Add(_lastThrust);
                        _pressureStreamer.Add(_lastPressure);
                    }
                }
            }

            _lastAddedTime += columns * columnSpan;
            _pendingPoints.RemoveRange(0, next);

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
            _pendingPoints.Clear();
            _lastThrust = 0;
            _lastPressure = 0;
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
