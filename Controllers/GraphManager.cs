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
        private const int WindowSize = 2500; // Size of the window for displaying data

        public GraphManager(WpfPlot thrustGraph, WpfPlot pressureGraph)
        {
            _thrustGraph = thrustGraph;
            _pressureGraph = pressureGraph;

            // Initialize the graphs with titles and labels
            _thrustStreamer = InitializeGraph(_thrustGraph, "Thrust Over Time", "Thrust (N)");
            _pressureStreamer = InitializeGraph(_pressureGraph, "Pressure Over Time", "Pressure (PSI)");
        }

        // Initializes a graph with a title and y-axis label
        private DataStreamer InitializeGraph(WpfPlot graph, string title, string yLabel)
        {
            graph.Plot.Title(title);
            graph.Plot.Axes.Bottom.Label.Text = "Data Points";
            graph.Plot.Axes.Left.Label.Text = yLabel;
            //graph.Plot.Axes.AutoScaleY(); // Enable auto-scaling for the y-axis
            graph.Plot.Axes.ContinuouslyAutoscale = false;

            var streamer = graph.Plot.Add.DataStreamer(WindowSize);
            streamer.ViewScrollLeft();
            streamer.ManageAxisLimits = true;

            return streamer;
        }

        // Adds a data point to the thrust and pressure graphs
        public void AddDataPoint(double time, double thrust, double pressure)
        {
            _thrustStreamer.Add(thrust);
            _pressureStreamer.Add(pressure);

            RefreshGraphs(); // Refresh the graphs to display the new data
        }

        // Adds multiple data points and refreshes the graphs once to minimize UI thread overhead
        public void AddDataPoints(IEnumerable<(double time, double thrust, double pressure)> points)
        {
            foreach (var (_, thrust, pressure) in points)
            {
                _thrustStreamer.Add(thrust);
                _pressureStreamer.Add(pressure);
            }

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
            RefreshGraphs();
        }
    }
}
