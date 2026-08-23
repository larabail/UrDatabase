using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using UrDatabase.Services;

namespace UrDatabase
{
    public partial class App : Avalonia.Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            ApplyAccent();
        }

        /// <summary>
        /// Replaces Fluent's accent — which is the operating system's, and on macOS is
        /// #007AFF — with the app's own brass, before any window is built.
        /// </summary>
        /// <remarks>
        /// Fluent derives every selected, checked and focused state from seven accent
        /// resources rather than holding a colour per state, so overriding those seven at
        /// application scope is what turns the whole theme warm: a checked toggle, a
        /// selected row, the highlight behind selected text and a focus ring all follow.
        /// They have to be <see cref="Color"/> values rather than brushes, because that is
        /// what Fluent's own brushes bind their <c>Color</c> to.
        ///
        /// The base is read back out of Tokens.axaml rather than written here, so the
        /// accent has exactly one home. Nothing is hardcoded as a fallback: a silent
        /// default is how the blue survived being "fixed" in the first place, and a missing
        /// token is a bug that should be found by the test that asserts it is present, not
        /// papered over at runtime.
        /// </remarks>
        private void ApplyAccent()
        {
            if (!Resources.TryGetResource(AccentBaseKey, null, out var token) || token is not Color brass)
            {
                AppLog.Write("startup.log", $"{AccentBaseKey} is missing from Tokens.axaml, so Fluent kept the system accent.");
                return;
            }

            foreach (var (key, hex) in AccentPalette.Ramp(brass.ToString()))
                Resources[key] = Color.Parse(hex);
        }

        /// <summary>The token in Tokens.axaml the whole accent ramp is derived from.</summary>
        internal const string AccentBaseKey = "BrassColor";

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
