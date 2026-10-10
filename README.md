# Project-FREAK

Project-FREAK is a Windows desktop application for monitoring and recording rocket motor test-stand data. It acquires load-cell and pressure-transducer readings through a LabJack, plots live measurements, optionally records a camera stream, and provides tools to review completed tests.

When no LabJack is detected, the application uses a simulated motor profile so the recording and review workflows can be exercised without test hardware.

## Architecture

The application is a .NET 8 WPF desktop app. `App` initializes settings and the LabJack service; `MainWindow` hosts the primary navigation frame. Pages coordinate user workflows, while the controller classes own hardware access, persistence, conversion, and analysis.

```text
App / MainWindow
	├── Mode selection, help, and settings views
	├── RecordPage
	│     ├── LabJackManager ── sensor acquisition / streaming
	│     ├── DemoMotorSimulator ── no-hardware data source
	│     ├── DataRecorder ── JSON time-series output
	│     ├── WebcamManager ── optional preview and MP4 recording
	│     └── GraphManager ── live ScottPlot displays
	└── ReviewDataPage
				├── ReviewDataLoader ── JSON and sensor-value conversion
				├── CubicSplineIntegrator / MotorDesignation ── run metrics
				└── ScottPlot and WPF media player ── plots and video

SettingsManager stores applied and saved settings. SensorCalibration converts sensor voltage to engineering units, and NetworkConfigurationService can configure a selected Windows network adapter for the test stand.

## Repository Layout

```text
App.xaml(.cs), MainWindow.xaml(.cs)   WPF startup and main navigation
Controllers/                          Hardware services, settings, recording, and analysis
Views/                                Record, review, help, mode selection, and settings pages
Assets/                               Fonts, logos, and LabJack library
Tests/Project_FREAK.Tests/            xUnit tests for controllers and data workflows
```

	|-- Mode selection, help, and settings views
	|-- RecordPage
	|     |-- LabJackManager -> sensor acquisition / streaming
	|     |-- DemoMotorSimulator -> no-hardware data source
	|     |-- DataRecorder -> JSON time-series output
	|     |-- WebcamManager -> optional preview and MP4 recording
	|     `-- GraphManager -> live ScottPlot displays
	`-- ReviewDataPage
				|-- ReviewDataLoader -> JSON and sensor-value conversion
				|-- CubicSplineIntegrator / MotorDesignation -> run metrics
				`-- ScottPlot and WPF media player -> plots and video

```powershell
dotnet restore Project-FREAK.sln
dotnet build Project-FREAK.sln
dotnet test Tests/Project_FREAK.Tests/Project_FREAK.Tests.csproj
```

Run the desktop application with:

```powershell
dotnet run --project Project-FREAK.csproj
```

## Recorded Data

Each test is written beneath `Documents/RocketTests/<timestamp>/`. A session contains a JSON file with time, thrust, pressure, and raw sensor-voltage arrays; an MP4 is included when camera recording is available. The JSON stores a relative video filename so the video can be found alongside its data file.

Review uses the stored engineering-unit values by default. Enabling raw-data review recalculates values from the recorded voltages and the currently applied calibration settings; load-cell excitation voltage is not recorded for each sample, so raw recalculation uses the calibration reference excitation.