using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using UrDatabase.Services;

namespace UrDatabase
{
    public partial class App : Avalonia.Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, err) => Record("AppDomain", err.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, err) => { Record("TaskScheduler", err.Exception); err.SetObserved(); };

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                try
                {
                    desktop.MainWindow = new Views.MainWindow();
                }
                catch (Exception ex)
                {
                    Record("Startup", ex);
                    desktop.Shutdown(-1);
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static void Record(string kind, Exception? ex)
        {
            var message = $"{kind} exception:{Environment.NewLine}{ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}{Environment.NewLine}{ex?.StackTrace}";
            AppLog.Write("startup.log", message);
        }
    }
}
