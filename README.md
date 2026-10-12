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
```

SettingsManager stores applied and saved settings. SensorCalibration converts sensor voltage to engineering units, and NetworkConfigurationService can configure a selected Windows network adapter for the test stand.

## Repository Layout

```text
App.xaml(.cs), MainWindow.xaml(.cs)   WPF startup and main navigation
Controllers/                          Hardware services, settings, recording, and analysis
Views/                                Record, review, help, mode selection, and settings pages
Assets/                               Fonts, logos, and LabJack library
Tests/Project_FREAK.Tests/            xUnit tests for controllers and data workflows
```

## Build and Test

```powershell
dotnet restore Project-FREAK.sln
dotnet build Project-FREAK.sln
dotnet test Tests/Project_FREAK.Tests/Project_FREAK.Tests.csproj
```

Run the desktop application with:

```powershell
dotnet run --project Project-FREAK.csproj
```

## Building a New Version

Run these steps in PowerShell from the repository root for every release.

### Prerequisites

- .NET 8 SDK for Windows.
- NSIS 3, installed with `winget install --id NSIS.NSIS --exact`. The build expects `C:\Program Files (x86)\NSIS\makensis.exe`.
- The LabJack files in `installer\redist`: `LabJackBasic_2025-02-12.exe` and the `LabJacka64` folder.

### 1. Change the version number

Set the same version in both files:

| File | Setting | Used for |
|---|---|---|
| `Project-FREAK.csproj` | `<Version>2.0.0</Version>` | Home screen version text and exe file version |
| `installer\Project-FREAK.nsi` | `!define APP_VERSION   "2.0.0"` | Installer filename, Add/Remove Programs version, installer version info |

Use numbers in `major.minor.patch` form, such as `2.1.0`. Do not add a suffix such as `-beta`. The installer version info needs four numeric parts, so suffixes are not supported.

### 2. Run the tests

```powershell
dotnet test Tests\Project_FREAK.Tests\Project_FREAK.Tests.csproj
```

The summary must show `Failed: 0` before you build the installer.

### 3. Build the installer

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

`-ExecutionPolicy Bypass` is needed because local `.ps1` scripts are blocked by default. The script:

1. Publishes a self-contained 64-bit Release build to `publish\win-x64`.
2. Checks that the LabJack files exist in `installer\redist`.
3. Compiles `installer\Project-FREAK.nsi` with NSIS.

The build takes several minutes. The installer is written to `installer\output\Project-FREAK-Setup-<version>.exe`.

Script options:

- `-SkipPublish` reuses the existing `publish\win-x64` output. Use it only if the code has not changed since the last publish.
- `-NsisPath "C:\path\to\makensis.exe"` uses an NSIS install in another location.

### 4. Check the build

Confirm the exe version:

```powershell
(Get-Item publish\win-x64\Project-FREAK.exe).VersionInfo.FileVersion
```

It should print the new version with `.0` added, for example `2.1.0.0`.

Install `installer\output\Project-FREAK-Setup-<version>.exe` on a clean Windows machine or VM and check:

- The home screen shows `Version <version>`.
- The desktop shortcut and taskbar show the app logo.
- Installing over the previous version upgrades it.

### 5. Share the installer

Share the `Project-FREAK-Setup-<version>.exe` file. The installer is not code-signed, so Windows may show an unknown publisher warning. Installer filenames include the version, so builds for different versions do not overwrite each other.

Installing a new version removes the previously installed version first. Recorded data in `Documents\RocketTests` is kept.

### Troubleshooting

| Problem | Fix |
|---|---|
| `makensis not found` | Install NSIS 3, or pass `-NsisPath`. |
| `Missing redistributable` | Restore the file named in the message under `installer\redist`. |
| `dotnet publish failed` | Fix the compile error shown above the message, then run the script again. |
| CS2001 `.g.cs could not be found` when running tests | Run `dotnet build Project-FREAK.csproj -c Debug --no-incremental`, then run the tests again. |

## Recorded Data

Each test is written beneath `Documents/RocketTests/<timestamp>/`. A session contains a JSON file with time, thrust, pressure, and raw sensor-voltage arrays, including LJTick amplifier output in volts as `load_cell_amplifier_output_voltages_v`; an MP4 is included when camera recording is available. The JSON stores a relative video filename so the video can be found alongside its data file.

Review uses the stored engineering-unit values by default. New recordings also store each load-cell excitation sample in `load_cell_excitation_voltages_v`; raw-data review uses those per-sample values with the currently applied calibration settings. Older JSON files using the legacy `load_cell_voltages_mv` key remain supported, and files without per-sample excitation use the calibration reference excitation instead.