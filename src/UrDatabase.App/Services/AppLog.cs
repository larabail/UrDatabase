using System;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>Best-effort diagnostics written under the per-user app data folder on any OS.</summary>
    public static class AppLog
    {
        /// <summary>
        /// Somewhere other than the per-user data directory to write to. Null in the app, always,
        /// so nothing about how a real install logs changes.
        ///
        /// It exists for the test suite, which until now had no way of not writing here. Every
        /// failure path in this codebase logs, testing a failure path means taking it, and so
        /// running the tests appended to the diagnostics of whatever real install happened to be
        /// on the machine — the one directory AGENTS.md says nothing under <c>tests/</c> may read
        /// or write, after a harness destroyed a maintainer's API keys twice.
        ///
        /// Logging was the quiet remainder of that rule: not a harness, not obviously a write, and
        /// spread across every service rather than sitting anywhere anybody would think to look.
        /// The seam is here rather than in each test because a caller cannot opt out of a static,
        /// and an isolation step that has to be remembered per test is one that gets forgotten.
        /// </summary>
        internal static string? DirectoryOverride;

        /// <summary>Where a line actually goes.</summary>
        internal static string TargetDirectory => DirectoryOverride ?? PlatformPaths.LogDirectory;

        public static void Write(string fileName, string message)
        {
            try
            {
                var directory = TargetDirectory;

                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.AppendAllText(path, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the reason the app fails.
            }
        }
    }
}
