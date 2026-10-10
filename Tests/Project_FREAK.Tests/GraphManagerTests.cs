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

        [Fact]
        public void AddDataPoints_LargeBatch_UpdatesObservedLimitsAndDecimates()
        {
            RunInSta(() =>
            {
                var thrustGraph = new WpfPlot();
                var pressureGraph = new WpfPlot();
                var settings = new SettingsManager.SettingsData
                {
                    LoadCellFullScaleValue = 1000,
                    LoadCellFullScaleUnit = "N",
                    PressureFullScaleValue = 500
                };
                var gm = new GraphManager(thrustGraph, pressureGraph, settings);

                var points = new (double, double, double)[100];
                for (int i = 0; i < 100; i++)
                {
                    points[i] = (i * 0.001, i == 45 ? 2500.0 : 100.0, i == 70 ? 900.0 : 50.0);
                }

                gm.AddDataPoints(points);

                Assert.True(thrustGraph.Plot.Axes.GetLimits().Top >= 2500);
                Assert.True(pressureGraph.Plot.Axes.GetLimits().Top >= 900);
            });
        }

        [Fact]
        public void DataStreamer_RelativeTimeWindowProperties_ExistAndWork()
        {
            RunInSta(() =>
            {
                var plot = new WpfPlot();
                var streamer = plot.Plot.Add.DataStreamer(500);
                streamer.ViewScrollLeft();
                streamer.Period = 15.0 / 500.0;
                streamer.Data.OffsetX = -15.0;
                streamer.ManageAxisLimits = false;

                plot.Plot.Axes.SetLimitsX(-15.0, 0.0);

                for (int i = 0; i < 10; i++)
                {
                    streamer.Add(i * 10.0);
                }

                var limits = streamer.GetAxisLimits();
                Assert.Equal(-15.0, limits.Left);
                Assert.Equal(0.0, limits.Right, 6);

                var plotLimits = plot.Plot.Axes.GetLimits();
                Assert.Equal(-15.0, plotLimits.Left);
                Assert.Equal(0.0, plotLimits.Right);

                // Now change to 30s window on the fly
                streamer.Period = 30.0 / 500.0;
                streamer.Data.OffsetX = -30.0;
                plot.Plot.Axes.SetLimitsX(-30.0, 0.0);

                var limits30 = streamer.GetAxisLimits();
                Assert.Equal(-30.0, limits30.Left);
                Assert.Equal(0.0, limits30.Right, 6);

                var xTicks = plot.Plot.Axes.Bottom.TickGenerator.Ticks;
                Assert.NotNull(xTicks);
            });
        }

        [Fact]
        public void GraphManager_ConfiguresRelativeTimeWindow_FromSettings()
        {
            RunInSta(() =>
            {
                var thrustGraph = new WpfPlot();
                var pressureGraph = new WpfPlot();
                var settings = new SettingsManager.SettingsData
                {
                    LiveGraphWindowSeconds = 15
                };
                var gm = new GraphManager(thrustGraph, pressureGraph, settings);

                Assert.Equal(15, gm.WindowSeconds);
                Assert.Equal("Time (s)", thrustGraph.Plot.Axes.Bottom.Label.Text);
                Assert.Equal("Time (s)", pressureGraph.Plot.Axes.Bottom.Label.Text);
                Assert.Equal(-15, thrustGraph.Plot.Axes.GetLimits().Left);
                Assert.Equal(0, thrustGraph.Plot.Axes.GetLimits().Right);

                // Update settings to 30s window
                settings.LiveGraphWindowSeconds = 30;
                gm.SetSensorRanges(settings);

                Assert.Equal(30, gm.WindowSeconds);
                Assert.Equal(-30, thrustGraph.Plot.Axes.GetLimits().Left);
                Assert.Equal(0, thrustGraph.Plot.Axes.GetLimits().Right);
            });
        }
    }
}
