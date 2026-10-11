using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Project_FREAK.Views;

public partial class ModeSelectionPage : Page
{
    public ModeSelectionPage()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        VersionText.Text = $"Version {version}";
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToPage(new RecordPage());
    }

    private void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToPage(new ReviewDataPage());
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.OpenSettingsWindow();
    }

    private void HelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToPage(new HelpPage());
    }
}
