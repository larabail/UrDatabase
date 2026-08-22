using System;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>Best-effort diagnostics written under the per-user app data folder on any OS.</summary>
    public static class AppLog
    {
        public static void Write(string fileName, string message)
        {
            try
            {
                Directory.CreateDirectory(PlatformPaths.LogDirectory);
                var path = Path.Combine(PlatformPaths.LogDirectory, fileName);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the reason the app fails.
            }
        }
    }
}
