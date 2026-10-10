using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using ScottPlot;

namespace Project_FREAK.Controllers
{
    public class DataRecorder
    {
        // Lists to store recorded data
        public List<double> TimeData { get; } = new();
        public List<double> ThrustData { get; } = new();
        public List<double> PressureData { get; } = new();
        public List<double> RawThrustVoltages { get; } = new();
        public List<double> RawPressureVoltages { get; } = new();

        private StreamWriter? _dataWriter;
        private string? _tempDataPath;

        private record DataPoint(
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
            TimeData.Add(time);
            ThrustData.Add(thrust);
            PressureData.Add(pressure);
            RawThrustVoltages.Add(thrustVoltage);
            RawPressureVoltages.Add(pressureVoltage);

            _dataWriter?.WriteLine(JsonSerializer.Serialize(new DataPoint(time, thrust, pressure, thrustVoltage, pressureVoltage)));
            _dataWriter?.Flush(); // Ensure data is written immediately
        }

        // Exports the collected data to final JSON format
        public void ExportToJson(string finalPath, string? videoPath = null)
        {
            try
            {
                // Add retry logic for file access
                const int maxRetries = 5;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        var dataPoints = _tempDataPath != null && File.Exists(_tempDataPath)
                            ? File.ReadAllLines(_tempDataPath)
                                .Select(line => JsonSerializer.Deserialize<DataPoint>(line)!)
                                .ToList()
                            : Enumerable.Range(0, TimeData.Count)
                                .Select(index => new DataPoint(
                                    TimeData[index],
                                    ThrustData[index],
                                    PressureData[index],
                                    RawThrustVoltages[index],
                                    RawPressureVoltages[index]))
                                .ToList();

                        // Deserialize and organize data
                        var data = new
                        {
                            time_values_seconds = dataPoints.Select(point => point.time).ToList(),
                            thrust_values_N = dataPoints.Select(point => point.thrust).ToList(),
                            pressure_values_PSI = dataPoints.Select(point => point.pressure).ToList(),
                            load_cell_voltages_mv = dataPoints.Select(point => point.thrustVoltage).ToList(),
                            pressure_transducer_voltages_v = dataPoints.Select(point => point.pressureVoltage).ToList(),
                            video_path = videoPath
                        };

                        // Write final JSON
                        File.WriteAllText(finalPath, JsonSerializer.Serialize(data,
                            new JsonSerializerOptions { WriteIndented = true }));
                        // ... rest of export logic ...
                        break;
                    }
                    catch (IOException) when (i < maxRetries - 1)
                    {
                        Thread.Sleep(50); // Wait for file lock to release
                    }
                }
            }
            finally
            {
                if (_dataWriter == null && _tempDataPath != null)
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

        // Creates a temporary data file and initializes the writer
        public void StartRecording(string sessionFolder)
        {
            StopRecording(); // Ensure any previous writer is closed
            TimeData.Clear();
            ThrustData.Clear();
            PressureData.Clear();
            RawThrustVoltages.Clear();
            RawPressureVoltages.Clear();
            _tempDataPath = Path.Combine(sessionFolder, $"temp_data_{Guid.NewGuid()}.jsonl");
            _dataWriter = new StreamWriter(_tempDataPath, append: false)
            {
                AutoFlush = true // Ensure automatic flushing
            };
        }

        public void Dispose()
        {
            StopRecording();
        }

        public void StopRecording()
        {
            _dataWriter?.Flush();
            _dataWriter?.Dispose();
            _dataWriter = null;
        }
    }
}