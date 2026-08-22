using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Jellyfin as seen through the configuration file. The important assertion is the negative
    /// one: an install that has never heard of a server has to keep behaving exactly as it did,
    /// because that is every existing install.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class JellyfinAppConfigTests : IDisposable
    {
        private readonly string _dir;
        private readonly EnvironmentVariableScope _environment;

        public JellyfinAppConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-jfcfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _environment = new EnvironmentVariableScope(
                PlatformPaths.TmdbApiKeyVariable,
                PlatformPaths.OmdbApiKeyVariable,
                PlatformPaths.JellyfinUrlVariable,
                PlatformPaths.JellyfinUsernameVariable,
                PlatformPaths.JellyfinPasswordVariable,
                PlatformPaths.JellyfinApiKeyVariable);
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
        public void A_configuration_that_never_mentions_jellyfin_leaves_it_switched_off()
        {
            var config = AppConfig.Load(WriteConfig("""{ "TmdbImageSize": "w342" }"""));

            Assert.NotNull(config.Jellyfin);
            Assert.False(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void A_missing_configuration_file_leaves_it_switched_off()
        {
            var config = AppConfig.Load(Path.Combine(_dir, "does-not-exist.json"));

            Assert.False(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void A_malformed_configuration_file_leaves_it_switched_off()
        {
            var config = AppConfig.Load(WriteConfig("{ not json at all "));

            Assert.False(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void A_server_and_an_account_are_read_and_normalised()
        {
            var config = AppConfig.Load(WriteConfig("""
                {
                  "Jellyfin": {
                    "ServerUrl": "media.invalid:8096/",
                    "Username": "viewer",
                    "Password": "hunter2"
                  }
                }
                """));

            Assert.True(config.Jellyfin.IsConfigured);
            Assert.True(config.Jellyfin.UsesUserAccount);
            Assert.Equal("http://media.invalid:8096", config.Jellyfin.ServerUrl);
            Assert.Equal("viewer", config.Jellyfin.Username);
        }

        [Fact]
        public void A_jellyfin_block_with_only_empty_strings_is_switched_off()
        {
            // The shape of the tracked example file. Copying it must not turn the feature on.
            var config = AppConfig.Load(WriteConfig("""
                {
                  "Jellyfin": { "ServerUrl": "", "Username": "", "Password": "", "ApiKey": "", "LibraryName": "" }
                }
                """));

            Assert.False(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void The_tracked_example_file_configures_no_server_and_carries_no_credential()
        {
            // It is committed to a public repository. It must never hold a working anything.
            var example = Path.Combine(AppContext.BaseDirectory, AppConfig.ExampleFileName);
            Assert.True(File.Exists(example), $"Expected the shipped example at {example}");

            var config = AppConfig.Load(example);

            Assert.False(config.Jellyfin.IsConfigured);
            Assert.Equal("", config.Jellyfin.ServerUrl);
            Assert.Equal("", config.Jellyfin.Username);
            Assert.Equal("", config.Jellyfin.Password);
            Assert.Equal("", config.Jellyfin.ApiKey);
        }
    }
}
