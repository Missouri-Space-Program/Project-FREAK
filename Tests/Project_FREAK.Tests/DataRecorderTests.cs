using System.IO;
using System.Text.Json;
using Project_FREAK.Controllers;
using Xunit;

namespace Project_FREAK.Tests.Tests
{
    public class DataRecorderTests
    {
        [Fact]
        public void AddDataPoint_AppendsToAllLists()
        {
            var dr = new DataRecorder();
            dr.AddDataPoint(1.0, 2.0, 3.0, 4.0, 5.0);

            Assert.Single(dr.TimeData);
            Assert.Equal(1.0, dr.TimeData[0]);
            Assert.Equal(2.0, dr.ThrustData[0]);
            Assert.Equal(3.0, dr.PressureData[0]);
            Assert.Equal(4.0, dr.RawThrustVoltages[0]);
            Assert.Equal(5.0, dr.RawPressureVoltages[0]);
        }

        [Fact]
        public void ExportToJson_CreatesValidFile()
        {
            var dr = new DataRecorder();
            dr.AddDataPoint(0.5, 1.5, 2.5, 3.5, 4.5);

            var tmp = Path.GetTempFileName();
            dr.ExportToJson(tmp, "burn.mp4");

            using var doc = JsonDocument.Parse(File.ReadAllText(tmp));
            Assert.True(doc.RootElement.TryGetProperty("time_values_seconds", out _));
            Assert.True(doc.RootElement.TryGetProperty("thrust_values_N", out _));
            Assert.Equal("burn.mp4", doc.RootElement.GetProperty("video_path").GetString());
            Assert.Equal("complete", doc.RootElement.GetProperty("recording_status").GetString());
        }

        [Fact]
        public void ExportToJson_MarksFailedRecordingAndPreservesFailureReason()
        {
            var folder = Path.Combine(Path.GetTempPath(), $"dr_failed_{Guid.NewGuid()}");
            Directory.CreateDirectory(folder);
            try
            {
                var dr = new DataRecorder();
                dr.AddDataPoint(0.5, 1.5, 2.5, 3.5, 4.5);
                string path = Path.Combine(folder, "failed.json");
                dr.ExportToJson(path, recordingFailure: "LabJack stream read failed.");

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                Assert.Equal("failed", doc.RootElement.GetProperty("recording_status").GetString());
                Assert.Equal("LabJack stream read failed.", doc.RootElement.GetProperty("recording_error").GetString());
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void ExportToJson_WhenWriteFails_KeepsTemporaryDataForRetry()
        {
            var folder = Path.Combine(Path.GetTempPath(), $"dr_retry_{Guid.NewGuid()}");
            Directory.CreateDirectory(folder);
            try
            {
                var dr = new DataRecorder();
                dr.StartRecording(folder);
                dr.AddDataPoint(0.0, 1.0, 2.0, 3.0, 4.0);
                dr.StopRecording();

                var unwritablePath = Path.Combine(folder, "missing", "final.json");
                Assert.ThrowsAny<IOException>(() => dr.ExportToJson(unwritablePath));
                Assert.Single(Directory.GetFiles(folder, "temp_data_*.jsonl"));

                var finalJson = Path.Combine(folder, "final.json");
                dr.ExportToJson(finalJson);

                Assert.True(File.Exists(finalJson));
                Assert.Empty(Directory.GetFiles(folder, "temp_data_*.jsonl"));
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void AddDataPoints_AppendsBatchCorrectly()
        {
            var dr = new DataRecorder();
            var batch = new DataPointRecord[]
            {
                new(0.001, 10.0, 100.0, 1.2, 0.6),
                new(0.002, 20.0, 200.0, 1.3, 0.7)
            };

            dr.AddDataPoints(batch);

            Assert.Equal(2, dr.TimeData.Count);
            Assert.Equal(0.001, dr.TimeData[0]);
            Assert.Equal(0.002, dr.TimeData[1]);
            Assert.Equal(10.0, dr.ThrustData[0]);
            Assert.Equal(20.0, dr.ThrustData[1]);
        }

        [Fact]
        public void StartRecording_And_Export_FlushesBackgroundQueue()
        {
            var folder = Path.Combine(Path.GetTempPath(), $"dr_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(folder);
            try
            {
                var dr = new DataRecorder();
                dr.StartRecording(folder);

                for (int i = 0; i < 100; i++)
                {
                    dr.AddDataPoint(i * 0.001, i * 5.0, i * 2.0, 1.25, 0.5);
                }

                dr.StopRecording();

                var finalJson = Path.Combine(folder, "final.json");
                dr.ExportToJson(finalJson);

                using var doc = JsonDocument.Parse(File.ReadAllText(finalJson));
                var timeArr = doc.RootElement.GetProperty("time_values_seconds");
                Assert.Equal(100, timeArr.GetArrayLength());
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void HighSpeedStreamingBatch_ExportsAndLoadsInReviewDataLoader()
        {
            var folder = Path.Combine(Path.GetTempPath(), $"stream_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(folder);
            try
            {
                var dr = new DataRecorder();
                dr.StartRecording(folder);

                const int totalSamples = 1000; // 1 second at 1000 Hz
                const int batchSize = 50;

                for (int b = 0; b < totalSamples / batchSize; b++)
                {
                    var batch = new DataPointRecord[batchSize];
                    for (int i = 0; i < batchSize; i++)
                    {
                        int index = b * batchSize + i;
                        double t = index * 0.001;
                        double thrust = 500.0 * Math.Sin(t * Math.PI);
                        double pressure = 200.0 * Math.Sin(t * Math.PI);
                        batch[i] = new DataPointRecord(t, Math.Max(0, thrust), Math.Max(0, pressure), 1.25, 0.5);
                    }
                    dr.AddDataPoints(batch);
                }

                dr.StopRecording();

                var jsonPath = Path.Combine(folder, "data_1000hz.json");
                dr.ExportToJson(jsonPath);

                var dataset = ReviewDataLoader.Load(jsonPath);
                Assert.Equal(1000, dataset.TimeSeconds.Length);
                Assert.Equal(1000, dataset.ThrustNewtons.Length);
                Assert.Equal(1000, dataset.PressurePsi.Length);
                Assert.Equal(0.0, dataset.TimeSeconds[0]);
                Assert.Equal(0.999, dataset.TimeSeconds[^1], 3);

                double impulse = CubicSplineIntegrator.Integrate(dataset.TimeSeconds, dataset.ThrustNewtons);
                Assert.True(impulse > 0);
            }
            finally
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
        }
    }
}
