using System;
using System.Threading;
using ScottPlot.WPF;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests
{
    public class GraphManagerTests
    {
        private void RunInSta(Action action)
        {
            Exception ex = null!;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception e) { ex = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (ex != null) throw ex;
        }

        [Fact]
        public void Construct_DoesNotThrow()
        {
            RunInSta(() =>
            {
                var gm = new GraphManager(new WpfPlot(), new WpfPlot());
            });
        }

        [Fact]
        public void AddDataPoint_DoesNotThrow()
        {
            RunInSta(() =>
            {
                var gm = new GraphManager(new WpfPlot(), new WpfPlot());
                gm.AddDataPoint(0, 1, 2);
            });
        }

        [Fact]
        public void AddDataPoints_DoesNotThrow()
        {
            RunInSta(() =>
            {
                var gm = new GraphManager(new WpfPlot(), new WpfPlot());
                gm.AddDataPoints(new[] { (0.0, 1.0, 2.0), (0.1, 1.5, 2.5) });
            });
        }

        [Fact]
        public void ClearGraphs_DoesNotThrow()
        {
            RunInSta(() =>
            {
                var gm = new GraphManager(new WpfPlot(), new WpfPlot());
                gm.ClearGraphs();
            });
        }

        [Fact]
        public void SensorRanges_SetGraphLimitsAndAllowOverRangeReadings()
        {
            RunInSta(() =>
            {
                var thrustGraph = new WpfPlot();
                var pressureGraph = new WpfPlot();
                var settings = new SettingsManager.SettingsData
                {
                    LoadCellFullScaleValue = 5000,
                    LoadCellFullScaleUnit = "N",
                    PressureFullScaleValue = 250
                };
                var gm = new GraphManager(thrustGraph, pressureGraph, settings);

                Assert.Equal(5000, thrustGraph.Plot.Axes.GetLimits().Top);
                Assert.Equal(250, pressureGraph.Plot.Axes.GetLimits().Top);

                gm.AddDataPoint(0, 6000, 300);

                Assert.True(thrustGraph.Plot.Axes.GetLimits().Top > 6000);
                Assert.True(pressureGraph.Plot.Axes.GetLimits().Top > 300);
            });
        }
    }
}
