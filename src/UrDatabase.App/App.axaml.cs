using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
                    if (FirstRun.IsSetupNeeded())
                        ShowSetupThenLibrary(desktop);
                    else
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

        /// <summary>
        /// Setup opens first on a fresh install, with the library window built only once it
        /// closes — the library reads its configuration in its constructor, so building it first
        /// and reconfiguring it afterwards would mean showing the user a window that was already
        /// wrong.
        ///
        /// Shutdown is held explicitly across the handover. Avalonia closes the application when
        /// the last window goes, and for the moment between setup closing and the library opening
        /// there is no window at all.
        /// </summary>
        private static void ShowSetupThenLibrary(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var previousShutdownMode = desktop.ShutdownMode;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var setup = new Views.SetupWindow(firstRun: true);

            setup.Closed += (_, __) =>
            {
                try
                {
                    var main = new Views.MainWindow();
                    desktop.MainWindow = main;
                    desktop.ShutdownMode = previousShutdownMode;
                    main.Show();
                }
                catch (Exception ex)
                {
                    Record("Startup", ex);
                    desktop.Shutdown(-1);
                }
            };

            desktop.MainWindow = setup;
        }

        private static void Record(string kind, Exception? ex)
        {
            var message = $"{kind} exception:{Environment.NewLine}{ex?.GetType().Name}: {ex?.Message}{Environment.NewLine}{Environment.NewLine}{ex?.StackTrace}";
            AppLog.Write("startup.log", message);
        }
    }
}
