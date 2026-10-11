using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Project_FREAK.Controllers
{
    public readonly record struct DataPointRecord(
        double Time,
        double Thrust,
        double Pressure,
        double ThrustVoltage,
        double PressureVoltage
    );

    public class DataRecorder : IDisposable
    {
        // Lists to store recorded data
        public List<double> TimeData { get; } = new();
        public List<double> ThrustData { get; } = new();
        public List<double> PressureData { get; } = new();
        public List<double> RawThrustVoltages { get; } = new();
        public List<double> RawPressureVoltages { get; } = new();

        private readonly object _syncLock = new();
        private Channel<DataPointRecord>? _channel;
        private Task? _writerTask;
        private string? _tempDataPath;

        private record LegacyDataPoint(
            double time,
            double thrust,
            double pressure,
            double thrustVoltage,
            double pressureVoltage
        );

        // Adds a data point to the lists
        public void AddDataPoint(double time, double thrust, double pressure,
                               double thrustVoltage, double pressureVoltage)
        {
            var record = new DataPointRecord(time, thrust, pressure, thrustVoltage, pressureVoltage);
            lock (_syncLock)
            {
                TimeData.Add(time);
                ThrustData.Add(thrust);
                PressureData.Add(pressure);
                RawThrustVoltages.Add(thrustVoltage);
                RawPressureVoltages.Add(pressureVoltage);

                _channel?.Writer.TryWrite(record);
            }
        }

        // Adds a batch of data points to the lists and streaming channel
        public void AddDataPoints(ReadOnlySpan<DataPointRecord> points)
        {
            if (points.IsEmpty) return;

            lock (_syncLock)
            {
                int newCapacity = TimeData.Count + points.Length;
                if (TimeData.Capacity < newCapacity)
                {
                    TimeData.Capacity = newCapacity;
                    ThrustData.Capacity = newCapacity;
                    PressureData.Capacity = newCapacity;
                    RawThrustVoltages.Capacity = newCapacity;
                    RawPressureVoltages.Capacity = newCapacity;
                }

                for (int i = 0; i < points.Length; i++)
                {
                    var p = points[i];
                    TimeData.Add(p.Time);
                    ThrustData.Add(p.Thrust);
                    PressureData.Add(p.Pressure);
                    RawThrustVoltages.Add(p.ThrustVoltage);
                    RawPressureVoltages.Add(p.PressureVoltage);

                    _channel?.Writer.TryWrite(p);
                }
            }
        }

        // Exports the collected data to final JSON format
        public void ExportToJson(string finalPath, string? videoPath = null, string? recordingFailure = null)
        {
            bool exported = false;
            try
            {
                const int maxRetries = 5;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        List<double> times;
                        List<double> thrusts;
                        List<double> pressures;
                        List<double> rawThrusts;
                        List<double> rawPressures;

                        lock (_syncLock)
                        {
                            if (TimeData.Count > 0)
                            {
                                times = new List<double>(TimeData);
                                thrusts = new List<double>(ThrustData);
                                pressures = new List<double>(PressureData);
                                rawThrusts = new List<double>(RawThrustVoltages);
                                rawPressures = new List<double>(RawPressureVoltages);
                            }
                            else if (_tempDataPath != null && File.Exists(_tempDataPath))
                            {
                                times = new();
                                thrusts = new();
                                pressures = new();
                                rawThrusts = new();
                                rawPressures = new();

                                using var reader = new StreamReader(_tempDataPath, Encoding.UTF8);
                                string? line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (string.IsNullOrWhiteSpace(line)) continue;
                                    var dp = JsonSerializer.Deserialize<LegacyDataPoint>(line);
                                    if (dp != null)
                                    {
                                        times.Add(dp.time);
                                        thrusts.Add(dp.thrust);
                                        pressures.Add(dp.pressure);
                                        rawThrusts.Add(dp.thrustVoltage);
                                        rawPressures.Add(dp.pressureVoltage);
                                    }
                                }
                            }
                            else
                            {
                                times = new();
                                thrusts = new();
                                pressures = new();
                                rawThrusts = new();
                                rawPressures = new();
                            }
                        }

                        // Stream directly to output file using Utf8JsonWriter to minimize allocations
                        var jsonWriterOptions = new JsonWriterOptions { Indented = true };
                        using (var fileStream = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                        using (var writer = new Utf8JsonWriter(fileStream, jsonWriterOptions))
                        {
                            writer.WriteStartObject();

                            writer.WriteStartArray("time_values_seconds");
                            for (int idx = 0; idx < times.Count; idx++) writer.WriteNumberValue(times[idx]);
                            writer.WriteEndArray();

                            writer.WriteStartArray("thrust_values_N");
                            for (int idx = 0; idx < thrusts.Count; idx++) writer.WriteNumberValue(thrusts[idx]);
                            writer.WriteEndArray();

                            writer.WriteStartArray("pressure_values_PSI");
                            for (int idx = 0; idx < pressures.Count; idx++) writer.WriteNumberValue(pressures[idx]);
                            writer.WriteEndArray();

                            writer.WriteStartArray("load_cell_voltages_mv");
                            for (int idx = 0; idx < rawThrusts.Count; idx++) writer.WriteNumberValue(rawThrusts[idx]);
                            writer.WriteEndArray();

                            writer.WriteStartArray("pressure_transducer_voltages_v");
                            for (int idx = 0; idx < rawPressures.Count; idx++) writer.WriteNumberValue(rawPressures[idx]);
                            writer.WriteEndArray();

                            if (videoPath != null)
                                writer.WriteString("video_path", videoPath);
                            else
                                writer.WriteNull("video_path");

                            writer.WriteString("recording_status", string.IsNullOrWhiteSpace(recordingFailure) ? "complete" : "failed");
                            if (!string.IsNullOrWhiteSpace(recordingFailure))
                                writer.WriteString("recording_error", recordingFailure);

                            writer.WriteEndObject();
                            writer.Flush();
                        }

                        exported = true;
                        break;
                    }
                    catch (IOException) when (i < maxRetries - 1)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
            finally
            {
                // Keep the temporary data when the export failed so it can be retried.
                lock (_syncLock)
                {
                    if (exported && _writerTask == null && _channel == null && _tempDataPath != null)
                    {
                        try
                        {
                            File.Delete(_tempDataPath);
                            _tempDataPath = null;
                        }
                        catch { /* Ignore cleanup errors */ }
                    }
                }
            }
        }

        // Creates a temporary data file and initializes the streaming channel and background writer
        public void StartRecording(string sessionFolder)
        {
            lock (_syncLock)
            {
                StopRecording(); // Ensure any previous writer is closed
                TimeData.Clear();
                ThrustData.Clear();
                PressureData.Clear();
                RawThrustVoltages.Clear();
                RawPressureVoltages.Clear();

                _tempDataPath = Path.Combine(sessionFolder, $"temp_data_{Guid.NewGuid()}.jsonl");
                _channel = Channel.CreateUnbounded<DataPointRecord>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
                });

                var reader = _channel.Reader;
                var tempPath = _tempDataPath;
                _writerTask = Task.Run(() => ProcessWriteQueueAsync(reader, tempPath));
            }
        }

        private static async Task ProcessWriteQueueAsync(ChannelReader<DataPointRecord> reader, string tempPath)
        {
            try
            {
                using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
                using var writer = new StreamWriter(stream, Encoding.UTF8, 65536);

                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        writer.Write("{\"time\":");
                        writer.Write(item.Time.ToString("R", CultureInfo.InvariantCulture));
                        writer.Write(",\"thrust\":");
                        writer.Write(item.Thrust.ToString("R", CultureInfo.InvariantCulture));
                        writer.Write(",\"pressure\":");
                        writer.Write(item.Pressure.ToString("R", CultureInfo.InvariantCulture));
                        writer.Write(",\"thrustVoltage\":");
                        writer.Write(item.ThrustVoltage.ToString("R", CultureInfo.InvariantCulture));
                        writer.Write(",\"pressureVoltage\":");
                        writer.Write(item.PressureVoltage.ToString("R", CultureInfo.InvariantCulture));
                        writer.WriteLine('}');
                    }
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error writing temporary test data: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopRecording();
        }

        public void StopRecording()
        {
            Channel<DataPointRecord>? channel;
            Task? writerTask;

            lock (_syncLock)
            {
                channel = _channel;
                _channel = null;
                writerTask = _writerTask;
                _writerTask = null;
            }

            if (channel != null)
            {
                channel.Writer.TryComplete();
            }

            if (writerTask != null)
            {
                try
                {
                    writerTask.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping recorder writer: {ex.Message}");
                }
            }
        }
    }
}