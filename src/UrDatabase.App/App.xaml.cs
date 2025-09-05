using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace UrDatabase
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, err) => ShowCrash("AppDomain", err.ExceptionObject as Exception);
            DispatcherUnhandledException += (s, err) => { ShowCrash("Dispatcher", err.Exception); err.Handled = true; };
            TaskScheduler.UnobservedTaskException += (s, err) => { ShowCrash("TaskScheduler", err.Exception); err.SetObserved(); };

            base.OnStartup(e);

            try
            {
                var win = new Views.MainWindow();
                win.Show();
            }
            catch (Exception ex)
            {
                ShowCrash("Startup", ex);
                Shutdown(-1);
            }
        }

        private void ShowCrash(string kind, Exception? ex)
        {
            var msg = $"{kind} exception:\n{ex?.GetType().Name}: {ex?.Message}\n\n{ex?.StackTrace}";
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UrDatabase", "logs");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "startup.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] {msg}\n\n");
            }
            catch { }
            MessageBox.Show(msg, "UrDatabase crash", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
