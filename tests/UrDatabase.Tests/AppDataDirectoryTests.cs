using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Pointing the whole install somewhere else.
    ///
    /// Until this existed there was no way to do it at all, and the consequence was not
    /// theoretical: every "launch it once and check it paints" opened somebody's real library,
    /// because the obvious precaution does not work. On macOS
    /// <c>GetFolderPath(SpecialFolder.ApplicationData)</c> asks Foundation rather than the
    /// environment, so setting <c>HOME</c> redirects <c>UserProfile</c> and leaves Application
    /// Support pointing at the real account — a harness written and checked on Linux therefore
    /// writes to the live install on a Mac. That has already cost a maintainer their API keys and
    /// their Jellyfin password.
    ///
    /// In the environment-variable collection because the variable is process-wide and xUnit runs
    /// collections in parallel: a test that set it while another read it would fail about one run
    /// in three and pass in isolation.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class AppDataDirectoryTests : IDisposable
    {
        private readonly string _root;

        public AppDataDirectoryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-appdata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void The_variable_moves_the_whole_install_and_not_merely_the_catalogue()
        {
            // One variable rather than one per path, because a half-redirected install is worse
            // than none: a scratch catalogue beside the real appsettings.json still reads
            // somebody's keys, and a scratch config beside the real logs still writes to them.
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, _root);

            Assert.Equal(_root, PlatformPaths.AppDataRoot);
            Assert.Equal(Path.Combine(_root, "movies.db"), PlatformPaths.DefaultDatabasePath);
            Assert.Equal(Path.Combine(_root, "posters"), PlatformPaths.DefaultPosterCacheDir);
            Assert.Equal(Path.Combine(_root, "logs"), PlatformPaths.LogDirectory);
            Assert.Equal(Path.Combine(_root, "appsettings.json"), ConfigStore.UserPath);
        }

        [Fact]
        public void Nothing_under_the_real_application_data_folder_is_named_once_it_is_set()
        {
            // The assertion the rule actually needs. Naming the scratch directory is not the same
            // as not naming the real one, and it is the second that keeps somebody's library safe.
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, _root);

            var real = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                PlatformPaths.AppFolderName);

            foreach (var path in new[]
                     {
                         PlatformPaths.AppDataRoot,
                         PlatformPaths.DefaultDatabasePath,
                         PlatformPaths.DefaultPosterCacheDir,
                         PlatformPaths.LogDirectory,
                         ConfigStore.UserPath
                     })
            {
                Assert.StartsWith(_root, path, StringComparison.Ordinal);
                Assert.DoesNotContain(real, path, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Setting_HOME_alone_is_not_enough_which_is_why_this_variable_exists()
        {
            // The trap, asserted rather than described. On macOS this is the whole point of the
            // change; on Linux GetFolderPath does follow HOME, so the assertion is narrowed to
            // the platform where it means something.
            if (!OperatingSystem.IsMacOS()) return;

            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable, "HOME");
            Environment.SetEnvironmentVariable("HOME", _root);

            Assert.False(
                PlatformPaths.AppDataRoot.StartsWith(_root, StringComparison.Ordinal),
                "HOME appears to redirect ApplicationData on this machine, which would make " +
                PlatformPaths.AppDataVariable + " unnecessary — check the reasoning before deleting it.");

            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, _root);
            Assert.StartsWith(_root, PlatformPaths.AppDataRoot, StringComparison.Ordinal);
        }

        [Fact]
        public void A_relative_path_is_resolved_rather_than_left_to_the_working_directory()
        {
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, "scratch-install");

            Assert.True(Path.IsPathRooted(PlatformPaths.AppDataRoot));
            Assert.EndsWith("scratch-install", PlatformPaths.AppDataRoot, StringComparison.Ordinal);
        }

        [Fact]
        public void A_home_relative_path_is_expanded_like_every_other_configured_path()
        {
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, "~/urdb-scratch");

            Assert.Equal(Path.Combine(PlatformPaths.HomeDirectory, "urdb-scratch"), PlatformPaths.AppDataRoot);
            Assert.DoesNotContain("~", PlatformPaths.AppDataRoot);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_unset_or_blank_variable_leaves_the_install_where_it_has_always_been(string? value)
        {
            // Blank is the same intention as unset. Honouring it would put the install at the
            // filesystem root or at the working directory, which nobody asked for and which would
            // silently strand an existing library.
            using var scope = new EnvironmentVariableScope(PlatformPaths.AppDataVariable);
            Environment.SetEnvironmentVariable(PlatformPaths.AppDataVariable, value);

            var expected = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                PlatformPaths.AppFolderName);

            Assert.Equal(expected, PlatformPaths.AppDataRoot);
        }
    }
}
