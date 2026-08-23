using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    [Collection(EnvironmentVariables.CollectionName)]
    public class AppConfigTests : IDisposable
    {
        private readonly string _dir;
        private readonly EnvironmentVariableScope _environment;

        public AppConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _environment = new EnvironmentVariableScope(
                PlatformPaths.TmdbApiKeyVariable,
                PlatformPaths.OmdbApiKeyVariable);
        }

        public void Dispose()
        {
            _environment.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string WriteConfig(string json)
        {
            var path = Path.Combine(_dir, "appsettings.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void Load_falls_back_to_defaults_when_the_file_is_missing()
        {
            var config = AppConfig.Load(Path.Combine(_dir, "does-not-exist.json"));

            Assert.Equal(PlatformPaths.DefaultDatabasePath, config.DatabasePath);
            Assert.Equal(PlatformPaths.DefaultPosterCacheDir, config.PosterCacheDir);
            Assert.Equal(PlatformPaths.DefaultDownloadFolder, config.DownloadFolder);
            Assert.Equal("w342", config.TmdbImageSize);
            Assert.Equal("", config.TmdbApiKey);
        }

        [Fact]
        public void The_download_folder_is_read_and_expanded_like_every_other_path()
        {
            var path = WriteConfig(@"{ ""DownloadFolder"": ""~/Films/FromServer"" }");

            var config = AppConfig.Load(path);

            Assert.Equal(
                Path.Combine(PlatformPaths.HomeDirectory, "Films", "FromServer"),
                config.DownloadFolder);
        }

        [Fact]
        public void A_blank_download_folder_takes_the_platform_default()
        {
            var path = WriteConfig(@"{ ""DownloadFolder"": ""   "" }");

            var config = AppConfig.Load(path);

            Assert.Equal(PlatformPaths.DefaultDownloadFolder, config.DownloadFolder);
        }

        /// <summary>
        /// Downloads land inside a folder the app would scan anyway, so the two halves of the
        /// library agree: a scan finds what a download wrote, rather than treating it as a
        /// stranger.
        /// </summary>
        [Fact]
        public void The_default_download_folder_sits_under_the_default_watch_folder()
        {
            Assert.StartsWith(
                PlatformPaths.DefaultWatchFolder,
                PlatformPaths.DefaultDownloadFolder,
                StringComparison.Ordinal);

            Assert.NotEqual(PlatformPaths.DefaultWatchFolder, PlatformPaths.DefaultDownloadFolder);
        }

        [Fact]
        public void Load_falls_back_to_defaults_when_the_file_is_malformed()
        {
            var path = WriteConfig("{ this is not json ");

            var config = AppConfig.Load(path);

            Assert.Equal(PlatformPaths.DefaultDatabasePath, config.DatabasePath);
        }

        [Fact]
        public void Load_reads_values_from_the_file()
        {
            var dbPath = Path.Combine(_dir, "movies.db");
            var path = WriteConfig($@"{{
                ""DatabasePath"": ""{dbPath.Replace("\\", "\\\\")}"",
                ""TmdbApiKey"": ""from-file"",
                ""TmdbImageSize"": ""w500"",
                ""DownloadPosters"": true
            }}");

            var config = AppConfig.Load(path);

            Assert.Equal(dbPath, config.DatabasePath);
            Assert.Equal("from-file", config.TmdbApiKey);
            Assert.Equal("w500", config.TmdbImageSize);
            Assert.True(config.DownloadPosters);
        }

        [Fact]
        public void Api_key_falls_back_to_the_environment_variable_when_the_file_value_is_empty()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable, "from-environment");
            var path = WriteConfig(@"{ ""TmdbApiKey"": """" }");

            var config = AppConfig.Load(path);

            Assert.Equal("from-environment", config.TmdbApiKey);
        }

        [Fact]
        public void Api_key_in_the_file_wins_over_the_environment_variable()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable, "from-environment");
            var path = WriteConfig(@"{ ""TmdbApiKey"": ""from-file"" }");

            var config = AppConfig.Load(path);

            Assert.Equal("from-file", config.TmdbApiKey);
        }

        [Fact]
        public void Api_key_is_empty_when_neither_the_file_nor_the_environment_supplies_one()
        {
            var config = AppConfig.Load(WriteConfig("{}"));

            Assert.Equal("", config.TmdbApiKey);
        }

        [Fact]
        public void Windows_style_appdata_paths_are_expanded_for_the_current_platform()
        {
            var path = WriteConfig(@"{
                ""DatabasePath"": ""%APPDATA%\\UrDatabase\\movies.db"",
                ""PosterCacheDir"": ""%APPDATA%\\UrDatabase\\posters""
            }");

            var config = AppConfig.Load(path);

            Assert.DoesNotContain("%APPDATA%", config.DatabasePath);
            Assert.Equal(PlatformPaths.DefaultDatabasePath, config.DatabasePath);
            Assert.Equal(PlatformPaths.DefaultPosterCacheDir, config.PosterCacheDir);
            Assert.DoesNotContain('\\', config.DatabasePath.Replace(Path.DirectorySeparatorChar, '/'));
        }

        [Fact]
        public void Watch_folders_default_to_the_platform_movie_folder_when_none_are_configured()
        {
            var config = AppConfig.Load(WriteConfig(@"{ ""WatchFolders"": [] }"));

            Assert.Equal(new[] { PlatformPaths.DefaultWatchFolder }, config.WatchFolders);
        }

        [Fact]
        public void An_answered_setup_that_named_no_folder_is_taken_at_its_word()
        {
            // Somebody who chose a Jellyfin server and unticked films on this computer must not
            // then find their home movie folder scanned into the library anyway.
            var config = AppConfig.Load(WriteConfig(@"{ ""WatchFolders"": [], ""SetupCompleted"": true }"));

            Assert.Empty(config.WatchFolders);
        }

        [Fact]
        public void Watch_folders_are_expanded_and_blank_entries_dropped()
        {
            var path = WriteConfig(@"{ ""WatchFolders"": [ ""%USERPROFILE%\\Movies"", """" ] }");

            var config = AppConfig.Load(path);

            var only = Assert.Single(config.WatchFolders);
            Assert.DoesNotContain("%USERPROFILE%", only);
            Assert.Equal(Path.Combine(PlatformPaths.HomeDirectory, "Movies"), only);
        }

        [Fact]
        public void Blank_paths_fall_back_to_platform_defaults()
        {
            var path = WriteConfig(@"{ ""DatabasePath"": """", ""PosterCacheDir"": """", ""TmdbImageSize"": """" }");

            var config = AppConfig.Load(path);

            Assert.Equal(PlatformPaths.DefaultDatabasePath, config.DatabasePath);
            Assert.Equal(PlatformPaths.DefaultPosterCacheDir, config.PosterCacheDir);
            Assert.Equal("w342", config.TmdbImageSize);
        }
    }
}
