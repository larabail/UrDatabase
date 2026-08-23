using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class PlatformPathsTests
    {
        [Fact]
        public void App_data_paths_live_under_the_user_application_data_folder()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UrDatabase");

            Assert.Equal(root, PlatformPaths.AppDataRoot);
            Assert.Equal(Path.Combine(root, "movies.db"), PlatformPaths.DefaultDatabasePath);
            Assert.Equal(Path.Combine(root, "posters"), PlatformPaths.DefaultPosterCacheDir);
            Assert.Equal(Path.Combine(root, "logs"), PlatformPaths.LogDirectory);
        }

        [Fact]
        public void Default_paths_are_absolute_and_contain_no_windows_tokens()
        {
            Assert.True(Path.IsPathRooted(PlatformPaths.DefaultDatabasePath));
            Assert.True(Path.IsPathRooted(PlatformPaths.DefaultPosterCacheDir));
            Assert.True(Path.IsPathRooted(PlatformPaths.DefaultWatchFolder));
            Assert.DoesNotContain("%", PlatformPaths.DefaultDatabasePath);
            Assert.DoesNotContain("%", PlatformPaths.DefaultWatchFolder);
        }

        [Fact]
        public void Default_watch_folder_is_the_platform_movie_folder_and_never_a_hardcoded_drive()
        {
            var folder = PlatformPaths.DefaultWatchFolder;

            Assert.DoesNotContain("D:", folder);

            if (OperatingSystem.IsMacOS())
                Assert.Equal(Path.Combine(PlatformPaths.HomeDirectory, "Movies"), folder);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Expand_returns_empty_for_blank_input(string? input)
        {
            Assert.Equal(string.Empty, PlatformPaths.Expand(input));
        }

        [Fact]
        public void Expand_resolves_appdata_and_normalises_windows_separators()
        {
            var expanded = PlatformPaths.Expand(@"%APPDATA%\UrDatabase\movies.db");

            Assert.Equal(PlatformPaths.DefaultDatabasePath, expanded);
            if (!OperatingSystem.IsWindows())
                Assert.DoesNotContain('\\', expanded);
        }

        [Fact]
        public void Expand_resolves_userprofile()
        {
            var expanded = PlatformPaths.Expand(@"%USERPROFILE%\Videos");

            Assert.Equal(Path.Combine(PlatformPaths.HomeDirectory, "Videos"), expanded);
        }

        [Fact]
        public void Expand_resolves_a_leading_tilde()
        {
            var expanded = PlatformPaths.Expand("~/Movies");

            Assert.Equal(PlatformPaths.HomeDirectory + "/Movies", expanded.Replace(Path.DirectorySeparatorChar, '/'));
        }

        [Fact]
        public void Expand_leaves_a_plain_unix_path_untouched()
        {
            Assert.Equal("/Volumes/Media/Movies", PlatformPaths.Expand("/Volumes/Media/Movies"));
        }

        [Fact]
        public void Expand_does_not_mangle_an_unrecognised_token()
        {
            // No such variable: the value must survive rather than become empty.
            var expanded = PlatformPaths.Expand("/media/%NOT_A_REAL_VARIABLE%/movies");

            Assert.Contains("movies", expanded);
        }

        [Fact]
        public void A_downloaded_release_lands_in_the_users_own_downloads_folder()
        {
            // Which is what "download" means to everything else on the machine, and somewhere they
            // already clear out — unlike the app's data directory, which nothing here ever empties.
            var folder = PlatformPaths.ResolveUpdateFolder("/home/someone", "/data/UrDatabase", _ => true);

            Assert.Equal(Path.Combine("/home/someone", "Downloads"), folder);
        }

        [Fact]
        public void A_machine_with_no_downloads_folder_gets_one_inside_the_app_data_directory()
        {
            var folder = PlatformPaths.ResolveUpdateFolder("/home/someone", "/data/UrDatabase", _ => false);

            Assert.Equal(Path.Combine("/data/UrDatabase", "updates"), folder);
        }

        [Fact]
        public void The_update_folder_is_absolute_and_carries_no_windows_tokens()
        {
            Assert.True(Path.IsPathRooted(PlatformPaths.DefaultUpdateFolder));
            Assert.DoesNotContain("%", PlatformPaths.DefaultUpdateFolder);
        }
    }
}
