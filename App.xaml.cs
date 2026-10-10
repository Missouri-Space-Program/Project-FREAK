using System.Configuration;
using System.Data;
using System.Windows;
using LabJack;
using Project_FREAK.Controllers;
namespace Project_FREAK
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {

        public SettingsManager SettingsManager { get; } = new SettingsManager();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SettingsManager.LoadAppliedSettings(); // Load session settings instead of saved
            var labJackManager = LabJackManager.Instance;
            labJackManager.ApplyCalibration(SettingsManager.AppliedSettings);
            SettingsManager.AppliedSettingsChanged += (_, _) => labJackManager.ApplyCalibration(SettingsManager.AppliedSettings);
        }
        protected override void OnExit(ExitEventArgs e)
        {
            LabJackManager.Instance.CloseDevice(); //close device when done with app
            base.OnExit(e);
        }
    }

}
