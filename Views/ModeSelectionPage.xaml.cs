using System.Windows;
using System.Windows.Controls;

namespace Project_FREAK.Views;

public partial class ModeSelectionPage : Page
{
    public ModeSelectionPage()
    {
        InitializeComponent();
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
}
