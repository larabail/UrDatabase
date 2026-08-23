using System;
using System.IO;
using System.Runtime.CompilerServices;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Points everything this assembly writes at a temporary directory, before a single test runs.
    ///
    /// <c>~/Library/Application Support/UrDatabase</c> on macOS, <c>%APPDATA%\UrDatabase</c> on
    /// Windows, is somebody's install: their catalogue, their poster cache, and an
    /// <c>appsettings.json</c> carrying their Jellyfin password and both API keys. AGENTS.md says
    /// nothing under <c>tests/</c> may read or write it, after a harness destroyed a maintainer's
    /// credentials twice.
    ///
    /// The suite was obeying that everywhere it was easy to see — every test that opens a database
    /// or saves a config already passes an explicit temporary path — and quietly breaking it
    /// through <see cref="AppLog"/>. Every failure path in the app logs, testing a failure path
    /// means taking it, so a full run appended to the real install's <c>jellyfin.log</c>,
    /// <c>omdb.log</c>, <c>posters.log</c> and <c>startup.log</c>. Nothing said so and nothing
    /// failed, which is exactly how the two earlier accidents went unnoticed.
    ///
    /// A module initializer rather than a fixture, because it has to hold for tests that never
    /// asked for it. xUnit gives no hook that runs before every collection, a class fixture only
    /// covers the class that remembers to take it, and the failure mode of forgetting is silent.
    /// This runs when the assembly is loaded, ahead of any test, whatever anybody writes later.
    /// </summary>
    internal static class TestIsolation
    {
        /// <summary>The temporary root this run's diagnostics go to. Deleted when the run ends.</summary>
        internal static string LogDirectory { get; } = Path.Combine(
            Path.GetTempPath(), "urdb-tests-" + Guid.NewGuid().ToString("N"), "logs");

        [ModuleInitializer]
        internal static void RedirectDiagnostics()
        {
            AppLog.DirectoryOverride = LogDirectory;

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                try
                {
                    var root = Path.GetDirectoryName(LogDirectory);
                    if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
                }
                catch
                {
                    // A leftover directory under the system temp folder is not worth failing a run
                    // over, and the OS clears it out anyway.
                }
            };
        }
    }

    /// <summary>
    /// The rule's own advice, taken: isolation is asserted rather than assumed. A change that
    /// removed the redirection, or pointed it somewhere real, would otherwise show up as nothing
    /// at all until somebody noticed their logs growing.
    /// </summary>
    public class TestIsolationTests
    {
        [Fact]
        public void Diagnostics_are_redirected_away_from_the_real_install()
        {
            Assert.Equal(TestIsolation.LogDirectory, AppLog.TargetDirectory);
            Assert.NotEqual(PlatformPaths.LogDirectory, AppLog.TargetDirectory);
        }

        [Fact]
        public void And_the_directory_they_go_to_is_actually_under_the_temporary_root()
        {
            // The check the rule asks for by name. Setting HOME looks like isolation and is not:
            // on macOS .NET asks Foundation for the application data directory and Foundation asks
            // the OS, which answers with the real account's whatever the environment says. So the
            // path is compared against the temporary root rather than trusted to be under it.
            var temp = Path.GetFullPath(Path.GetTempPath());
            var target = Path.GetFullPath(AppLog.TargetDirectory);

            Assert.StartsWith(temp, target, StringComparison.Ordinal);
        }

        [Fact]
        public void Writing_a_line_lands_in_the_temporary_directory_and_nowhere_else()
        {
            AppLog.Write("isolation-probe.log", "written by the suite");

            var written = Path.Combine(TestIsolation.LogDirectory, "isolation-probe.log");

            Assert.True(File.Exists(written));
            Assert.False(File.Exists(Path.Combine(PlatformPaths.LogDirectory, "isolation-probe.log")));
        }
    }
}
