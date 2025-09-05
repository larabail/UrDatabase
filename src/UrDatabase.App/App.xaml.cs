using System;
using System.Windows;

namespace UrDatabase
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                var win = new Views.MainWindow();
                win.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Startup error: {ex.Message}", "UrDatabase", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }
    }
}
