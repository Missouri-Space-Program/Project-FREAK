using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Project_FREAK.Controllers;

namespace Project_FREAK.Views;

public partial class RaspExportDialog : Window
{
    private static RaspMotorParameters? s_lastUsedParameters;

    private readonly ReviewDataset _dataset;
    private readonly string? _loadedJsonPath;
    private readonly double _graphStartSeconds;
    private readonly double _graphEndSeconds;
    private bool _suppressStatsUpdate;
    private bool _userCustomizedMotorName;

    public RaspMotorParameters? ResultParameters { get; private set; }
    public string? ExportedFilePath { get; private set; }

    public RaspExportDialog(
        ReviewDataset dataset,
        double initialStartSeconds,
        double initialEndSeconds,
        string? loadedJsonPath = null)
    {
        InitializeComponent();

        _dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        _graphStartSeconds = initialStartSeconds;
        _graphEndSeconds = initialEndSeconds;
        _loadedJsonPath = loadedJsonPath;

        MotorNameTextBox.TextChanged += (s, e) =>
        {
            if (!_suppressStatsUpdate)
                _userCustomizedMotorName = true;
        };

        PopulateInitialValues();
    }

    private void PopulateInitialValues()
    {
        _suppressStatsUpdate = true;

        IntervalStartTextBox.Text = _graphStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        IntervalEndTextBox.Text = _graphEndSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        var last = s_lastUsedParameters;
        if (last is not null)
        {
            DiameterTextBox.Text = last.DiameterMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
            LengthTextBox.Text = last.LengthMillimeters.ToString("0.##", CultureInfo.InvariantCulture);
            PropellantWeightTextBox.Text = last.PropellantWeightKilograms.ToString("0.#####", CultureInfo.InvariantCulture);
            TotalWeightTextBox.Text = last.TotalWeightKilograms.ToString("0.#####", CultureInfo.InvariantCulture);
            ManufacturerTextBox.Text = string.IsNullOrWhiteSpace(last.Manufacturer) ? "MSP" : last.Manufacturer;
            DelaysTextBox.Text = string.IsNullOrWhiteSpace(last.Delays) ? "P" : last.Delays;
            if (!string.IsNullOrWhiteSpace(last.Comments))
                CommentsTextBox.Text = last.Comments;

            SelectResolutionTag(last.MaxDataPoints);
        }
        else
        {
            DiameterTextBox.Text = "29";
            LengthTextBox.Text = "124";
            PropellantWeightTextBox.Text = "0.050";
            TotalWeightTextBox.Text = "0.100";
            ManufacturerTextBox.Text = "MSP";
            DelaysTextBox.Text = "P";
            SelectResolutionTag(500);
        }

        _suppressStatsUpdate = false;
        UpdateIntervalStatistics();
    }

    private void SelectResolutionTag(int? maxPoints)
    {
        string targetTag = maxPoints switch
        {
            null or <= 0 => "all",
            <= 32 => "32",
            <= 100 => "100",
            _ => "500"
        };

        foreach (ComboBoxItem item in ResolutionComboBox.Items)
        {
            if (item.Tag is string tag && string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase))
            {
                ResolutionComboBox.SelectedItem = item;
                break;
            }
        }
    }

    private void IntervalInputs_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressStatsUpdate)
            return;

        UpdateIntervalStatistics();
    }

    private void UpdateIntervalStatistics()
    {
        if (!double.TryParse(IntervalStartTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
            !double.TryParse(IntervalEndTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double end) ||
            start >= end)
        {
            IntervalStatsTextBlock.Text = "Enter valid numeric interval values with End > Start.";
            IntervalStatsTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        var stats = RaspMotorExporter.CalculateIntervalStatistics(
            _dataset.TimeSeconds,
            _dataset.ThrustNewtons,
            start,
            end);

        IntervalStatsTextBlock.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#9FE3A2")!;
        IntervalStatsTextBlock.Text =
            $"Duration: {stats.DurationSeconds:F3} s   |   " +
            $"Samples: {stats.SampleCount}   |   " +
            $"Total Impulse: {stats.TotalImpulseNewtonSeconds:F2} N·s   |   " +
            $"Avg Thrust: {stats.AverageThrustNewtons:F2} N   |   " +
            $"Peak: {stats.PeakThrustNewtons:F2} N\n" +
            $"Calculated Designation: {stats.MotorDesignation}";

        if (!_userCustomizedMotorName && !string.IsNullOrWhiteSpace(stats.MotorDesignation) && !stats.MotorDesignation.StartsWith("Unclassified", StringComparison.OrdinalIgnoreCase))
        {
            MotorNameTextBox.Text = stats.MotorDesignation;
        }
    }

    private void UseGraphIntervalButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressStatsUpdate = true;
        IntervalStartTextBox.Text = _graphStartSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        IntervalEndTextBox.Text = _graphEndSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        _suppressStatsUpdate = false;
        UpdateIntervalStatistics();
    }

    private void UseFullRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataset.TimeSeconds.Length == 0)
            return;

        _suppressStatsUpdate = true;
        IntervalStartTextBox.Text = _dataset.TimeSeconds[0].ToString("0.###", CultureInfo.InvariantCulture);
        IntervalEndTextBox.Text = _dataset.TimeSeconds[^1].ToString("0.###", CultureInfo.InvariantCulture);
        _suppressStatsUpdate = false;
        UpdateIntervalStatistics();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        // Parse and validate interval
        if (!double.TryParse(IntervalStartTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double start))
        {
            ShowError("Interval Start time must be a valid number.");
            IntervalStartTextBox.Focus();
            return;
        }

        if (!double.TryParse(IntervalEndTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
        {
            ShowError("Interval End time must be a valid number.");
            IntervalEndTextBox.Focus();
            return;
        }

        // Parse physical specifications
        if (!double.TryParse(DiameterTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double diameter) || diameter <= 0)
        {
            ShowError("Casing diameter must be a positive number in millimeters (e.g. 29 or 38).");
            DiameterTextBox.Focus();
            return;
        }

        if (!double.TryParse(LengthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double length) || length <= 0)
        {
            ShowError("Casing length must be a positive number in millimeters (e.g. 124).");
            LengthTextBox.Focus();
            return;
        }

        if (!double.TryParse(PropellantWeightTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double propellantWeight) || propellantWeight <= 0)
        {
            ShowError("Propellant mass must be a positive number in kilograms (e.g. 0.050 for 50 grams).");
            PropellantWeightTextBox.Focus();
            return;
        }

        if (!double.TryParse(TotalWeightTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double totalWeight) || totalWeight <= 0)
        {
            ShowError("Total motor mass must be a positive number in kilograms (e.g. 0.100 for 100 grams).");
            TotalWeightTextBox.Focus();
            return;
        }

        if (totalWeight < propellantWeight)
        {
            ShowError("Total motor loaded mass cannot be less than the propellant mass.");
            TotalWeightTextBox.Focus();
            return;
        }

        string motorName = MotorNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(motorName))
        {
            ShowError("Motor Name / Designation cannot be empty.");
            MotorNameTextBox.Focus();
            return;
        }

        string manufacturer = ManufacturerTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(manufacturer))
        {
            ShowError("Manufacturer code cannot be empty.");
            ManufacturerTextBox.Focus();
            return;
        }

        string delays = DelaysTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(delays))
        {
            delays = "P";
        }

        int? maxPoints = null;
        if (ResolutionComboBox.SelectedItem is ComboBoxItem selectedItem &&
            selectedItem.Tag is string tag &&
            int.TryParse(tag, out int parsedMax) &&
            parsedMax > 0)
        {
            maxPoints = parsedMax;
        }

        var parameters = new RaspMotorParameters
        {
            MotorName = motorName,
            DiameterMillimeters = diameter,
            LengthMillimeters = length,
            Delays = delays,
            PropellantWeightKilograms = propellantWeight,
            TotalWeightKilograms = totalWeight,
            Manufacturer = manufacturer,
            Comments = CommentsTextBox.Text.Trim(),
            IntervalStartSeconds = start,
            IntervalEndSeconds = end,
            MaxDataPoints = maxPoints
        };

        double minTime = _dataset.TimeSeconds.Length > 0 ? _dataset.TimeSeconds[0] : 0;
        double maxTime = _dataset.TimeSeconds.Length > 0 ? _dataset.TimeSeconds[^1] : 0;
        if (!RaspMotorExporter.ValidateParameters(parameters, minTime, maxTime, out var errors))
        {
            ShowError(string.Join("\n", errors));
            return;
        }

        // Prompt for destination file
        string defaultFileName = $"{parameters.MotorName.Replace(" ", "-")}.eng";
        var saveDialog = new SaveFileDialog
        {
            Title = "Save RASP Motor Data",
            Filter = "RASP Motor Data (*.eng)|*.eng|All files (*.*)|*.*",
            FileName = defaultFileName,
            DefaultExt = ".eng"
        };

        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            RaspMotorExporter.ExportToFile(
                saveDialog.FileName,
                _dataset,
                parameters,
                _loadedJsonPath);

            s_lastUsedParameters = parameters.Clone();
            ResultParameters = parameters;
            ExportedFilePath = saveDialog.FileName;

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to export file: {ex.Message}");
        }
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorBorder.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorBorder.Visibility = Visibility.Collapsed;
        ErrorTextBlock.Text = string.Empty;
    }
}
