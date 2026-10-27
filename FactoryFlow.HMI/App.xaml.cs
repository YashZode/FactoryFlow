using System.Configuration;
using System.Data;
using System.Windows;

namespace FactoryFlow.HMI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                MainWindow = new MainWindow();
                MainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("FactoryFlow could not start. Check its endpoint, tag-map and database configuration.\n\n" + ex.Message,
                    "FactoryFlow startup error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }

}
