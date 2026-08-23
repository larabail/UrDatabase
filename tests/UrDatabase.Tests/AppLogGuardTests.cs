using System;
using System.IO;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The guard that keeps this suite out of somebody's real install.
    /// </summary>
    /// <remarks>
    /// <c>AppLog.Redirect</c> was the mechanism and it was not enough on its own: twelve test
    /// classes reached code that logged, none of them was a test about logging, and every full run
    /// of the suite on every contributor's machine appended a twelve-byte <c>Arrival (2016)</c> to
    /// the owner's real <c>jellyfin.log</c>. Redirecting those twelve fixes today; this fixes the
    /// thirteenth, which will be written by somebody who has never read AGENTS.md and is only
    /// adding a log line to a service.
    ///
    /// In the environment-variable collection because the assertions need
    /// <c>PlatformPaths.LogDirectory</c> to point at a scratch root, and that is done with
    /// <c>URDATABASE_DATA_DIR</c>, which is process-wide. Pointing it somewhere is not a
    /// convenience: asserting "nothing was created" against the real directory would mean asking
    /// the filesystem about a path AGENTS.md forbids this suite from touching, and would pass
    /// vacuously on a machine where a maintainer's log happens to exist already.
    /// </remarks>
    [Collection(EnvironmentVariables.CollectionName)]
    public class AppLogGuardTests : IDisposable
    {
        private readonly string _root;

        public AppLogGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>
        /// The one that fails if somebody deletes <c>LogIsolation</c>, which is otherwise a file
        /// nothing references and which an editor will offer to remove as dead code.
        /// </summary>
        [Fact]
        public void The_suite_is_shut_out_of_the_real_log_directory()
        {
            Assert.True(
                AppLog.IsRealDirectoryForbidden,
                "Nothing armed the guard, so a test that logs is appending to whoever ran it. " +
                "LogIsolation.Arm is what turns it on — see AGENTS.md.");
        }

        [Fact]
        public void An_unredirected_write_is_refused_and_leaves_nothing_behind()
        {
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, _root);

            Assert.Throws<UnredirectedLogWriteException>(
                () => AppLog.Write("guard.log", "this line must not reach a disk"));

            // The refusal has to happen before the filesystem is touched, not merely before the
            // file is appended to: a directory conjured into somebody's install is still a write.
            Assert.False(Directory.Exists(PlatformPaths.LogDirectory));
        }

        /// <summary>
        /// Whoever meets this will have added a log line to a service and will have no idea why a
        /// test they did not write has started failing, so the exception has to carry the fix.
        /// </summary>
        [Fact]
        public void The_refusal_names_the_log_the_line_and_the_remedy()
        {
            var ex = Assert.Throws<UnredirectedLogWriteException>(
                () => AppLog.Write("jellyfin.log", "upload short: 3 of 12 bytes"));

            Assert.Equal("jellyfin.log", ex.FileName);
            Assert.Equal("upload short: 3 of 12 bytes", ex.LogMessage);
            Assert.Contains("AppLog.Redirect", ex.Message, StringComparison.Ordinal);
            Assert.Contains("upload short: 3 of 12 bytes", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The guard is not a ban on logging in tests. A redirected write behaves exactly as it
        /// always did, which is what every fixed test class now relies on.
        /// </summary>
        [Fact]
        public void A_redirected_write_is_allowed_and_still_never_throws()
        {
            var dir = Path.Combine(_root, "redirected");

            using (AppLog.Redirect(dir))
            {
                AppLog.Write("guard.log", "a line");

                // Still swallowed rather than thrown, guard or no guard: the app must not fail
                // because of a log, and a redirect is not a licence to start throwing.
                AppLog.Write(new string('x', 4096), "unwritable");
            }

            Assert.Contains("a line", File.ReadAllText(Path.Combine(dir, "guard.log")));
        }

        /// <summary>
        /// The writes most likely to go unnoticed are the ones on a thread nobody is waiting on —
        /// a poster fetch draining after a window closed, a progress report — so the flag is a
        /// plain static rather than an async-local like the redirect it complements.
        /// </summary>
        [Fact]
        public async Task A_write_from_a_background_task_is_refused_too()
        {
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, _root);

            await Assert.ThrowsAsync<UnredirectedLogWriteException>(
                () => Task.Run(() => AppLog.Write("guard.log", "from a thread nobody awaits")));

            Assert.False(Directory.Exists(PlatformPaths.LogDirectory));
        }

        /// <summary>
        /// A test class that redirects for one test and then adds a second one outside the scope
        /// is the exact shape of the regression, so the guard has to come back on by itself.
        /// </summary>
        [Fact]
        public void The_guard_returns_the_moment_a_redirect_ends()
        {
            using (AppLog.Redirect(Path.Combine(_root, "briefly")))
            {
                AppLog.Write("guard.log", "fine while the scope is open");
            }

            Assert.Throws<UnredirectedLogWriteException>(
                () => AppLog.Write("guard.log", "and refused again afterwards"));
        }
    }
}
