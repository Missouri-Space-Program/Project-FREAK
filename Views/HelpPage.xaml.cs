using System.Windows;
using System.Windows.Controls;

namespace Project_FREAK.Views;

public partial class HelpPage : Page
{
    public HelpPage()
    {
        InitializeComponent();
    }

    private void BackToStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.NavigateToPage(new ModeSelectionPage());
    }
}