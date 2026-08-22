using System;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>
    /// Whether this launch is somebody's first, and so whether to offer the setup screen.
    ///
    /// The bar is deliberately high: setup appears only when the app has never been told
    /// anything and has nothing to show. An install that predates the setup screen has a
    /// configuration file or a catalogue or both, and must never be greeted by a wizard asking
    /// it to introduce itself — that would look like the app had lost their library.
    /// </summary>
    public static class FirstRun
    {
        /// <param name="setupCompleted">The flag the setup screen writes when it is answered.</param>
        /// <param name="hasConfigFile">
        /// Whether the user has a configuration file of their own. One that exists but has no
        /// flag was written by hand, before this feature existed, and is left alone.
        /// </param>
        /// <param name="hasDatabase">
        /// Whether a catalogue is already on disk. Someone who has scanned a library has
        /// finished setup in every sense that matters, whatever their config file says.
        /// </param>
        public static bool IsSetupNeeded(bool setupCompleted, bool hasConfigFile, bool hasDatabase)
        {
            if (setupCompleted) return false;
            if (hasConfigFile) return false;
            if (hasDatabase) return false;

            return true;
        }

        /// <summary>
        /// The same question answered from this machine. Never throws and answers "no" if it
        /// cannot tell: failing to show setup costs a user one trip to the Settings button,
        /// whereas failing to start costs them the app.
        /// </summary>
        public static bool IsSetupNeeded()
        {
            try
            {
                var config = AppConfig.Load();

                return IsSetupNeeded(
                    setupCompleted: config.SetupCompleted,
                    hasConfigFile: ConfigStore.IsConfigured,
                    hasDatabase: File.Exists(config.DatabasePath));
            }
            catch (Exception ex)
            {
                AppLog.Write("startup.log", $"could not decide whether setup is needed: {ex.Message}");
                return false;
            }
        }
    }
}
