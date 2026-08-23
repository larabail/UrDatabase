using System;
using System.IO;
using System.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A key the app does not have must say so. Written from a real install of 0.3.1 that had
    /// <c>"Jellyfin": { "Url": ... }</c> in it — the field is <c>ServerUrl</c> — where the server
    /// was never contacted, the library was empty and nothing anywhere said why.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class ConfigDiagnosticsTests : IDisposable
    {
        private readonly string _dir;
        private readonly EnvironmentVariableScope _environment;

        public ConfigDiagnosticsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-diag-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            // A developer machine with a real server exported would otherwise configure Jellyfin
            // behind the test's back, and the assertion about a mistyped key would pass for the
            // wrong reason.
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
            var path = Path.Combine(_dir, AppConfig.FileName);
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void The_bug_report_verbatim_names_the_key_and_the_one_that_was_meant()
        {
            var path = WriteConfig(@"{
                ""Jellyfin"": { ""Url"": ""http://media-box:8096"", ""ApiKey"": ""not-a-real-key"" }
            }");

            var config = AppConfig.Load(path);

            // The symptom, first: the address went nowhere and no sync was ever attempted.
            Assert.Equal("", config.Jellyfin.ServerUrl);
            Assert.False(config.Jellyfin.IsConfigured);

            var unknown = Assert.Single(config.UnknownSettings);
            Assert.Equal("Jellyfin.Url", unknown.Key);
            Assert.Equal(new[] { "ServerUrl" }, unknown.Suggestions);

            var summary = ConfigDiagnostics.Summarize(config.UnknownSettings, config.SourcePath);
            Assert.NotNull(summary);
            Assert.Contains("Jellyfin.Url", summary);
            Assert.Contains("ServerUrl", summary);
            Assert.Contains(AppConfig.FileName, summary);
        }

        [Fact]
        public void A_fresh_install_with_no_configuration_file_warns_about_nothing()
        {
            var config = AppConfig.Load(Path.Combine(_dir, "does-not-exist.json"));

            Assert.Empty(config.UnknownSettings);
            Assert.Null(ConfigDiagnostics.Summarize(config.UnknownSettings, config.SourcePath));
        }

        [Fact]
        public void A_correct_configuration_warns_about_nothing()
        {
            var path = WriteConfig(@"{
                ""DatabasePath"": """",
                ""WatchFolders"": [],
                ""TmdbApiKey"": """",
                ""OmdbApiKey"": """",
                ""PosterCacheDir"": """",
                ""DownloadPosters"": false,
                ""TmdbImageSize"": ""w342"",
                ""SetupCompleted"": true,
                ""Jellyfin"": {
                    ""ServerUrl"": ""http://media-box:8096"",
                    ""Username"": ""someone"",
                    ""Password"": ""not-a-real-password"",
                    ""ApiKey"": """",
                    ""LibraryName"": """"
                }
            }");

            var config = AppConfig.Load(path);

            Assert.Empty(config.UnknownSettings);
            Assert.True(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void An_empty_configuration_warns_about_nothing()
        {
            Assert.Empty(AppConfig.Load(WriteConfig("{}")).UnknownSettings);
        }

        [Fact]
        public void Malformed_json_still_starts_on_defaults_and_claims_nothing_about_its_keys()
        {
            // Saying which keys a file that never parsed does not have would be a guess. The
            // wider silence around an unreadable file is issue #25 and is not answered here.
            var config = AppConfig.Load(WriteConfig(@"{ ""Jellyfin"": { ""Url"": "));

            Assert.Equal(PlatformPaths.DefaultDatabasePath, config.DatabasePath);
            Assert.Empty(config.UnknownSettings);
        }

        [Fact]
        public void A_key_the_deserialiser_does_read_is_not_reported()
        {
            // Case is ignored when loading, so a lower-case key works and is not a mistake.
            var config = AppConfig.Load(WriteConfig(@"{ ""tmdbapikey"": ""from-file"" }"));

            Assert.Equal("from-file", config.TmdbApiKey);
            Assert.Empty(config.UnknownSettings);
        }

        [Fact]
        public void A_key_written_in_another_convention_is_reported_with_the_real_spelling()
        {
            var config = AppConfig.Load(WriteConfig(@"{ ""tmdb_api_key"": ""from-file"" }"));

            var unknown = Assert.Single(config.UnknownSettings);
            Assert.Equal("tmdb_api_key", unknown.Key);
            Assert.Equal(new[] { "TmdbApiKey" }, unknown.Suggestions);
            Assert.Equal("", config.TmdbApiKey);
        }

        [Fact]
        public void A_near_miss_is_reported_with_the_key_it_nearly_is()
        {
            var config = AppConfig.Load(WriteConfig(@"{ ""WatchFolder"": [ ""/films"" ] }"));

            var unknown = Assert.Single(config.UnknownSettings);
            Assert.Equal(new[] { "WatchFolders" }, unknown.Suggestions);
        }

        [Fact]
        public void A_key_nothing_resembles_is_reported_without_a_guess()
        {
            var config = AppConfig.Load(WriteConfig(@"{ ""Bananas"": 3 }"));

            var unknown = Assert.Single(config.UnknownSettings);
            Assert.Equal("Bananas", unknown.Key);
            Assert.Empty(unknown.Suggestions);
            Assert.Contains("unknown setting", unknown.Describe());
        }

        [Fact]
        public void A_key_that_could_be_either_of_two_offers_both()
        {
            // "ApiKey" at the top level is exactly as much like TmdbApiKey as it is like
            // OmdbApiKey. Picking one would send somebody to change a key that was already right.
            var config = AppConfig.Load(WriteConfig(@"{ ""ApiKey"": ""not-a-real-key"" }"));

            var unknown = Assert.Single(config.UnknownSettings);
            Assert.Equal(new[] { "OmdbApiKey", "TmdbApiKey" }, unknown.Suggestions.OrderBy(x => x, StringComparer.Ordinal));
            Assert.Contains(" or ", unknown.Describe());
        }

        [Fact]
        public void A_computed_property_is_not_a_setting_and_is_reported_as_such()
        {
            // IsConfigured reads like something one could set, and setting it does nothing.
            var config = AppConfig.Load(WriteConfig(@"{ ""Jellyfin"": { ""IsConfigured"": true } }"));

            var unknown = Assert.Single(config.UnknownSettings);
            Assert.Equal("Jellyfin.IsConfigured", unknown.Key);
            Assert.False(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void Every_unrecognised_key_is_reported_and_the_summary_counts_them()
        {
            var config = AppConfig.Load(WriteConfig(@"{
                ""Url"": ""http://media-box:8096"",
                ""Bananas"": 3,
                ""Jellyfin"": { ""Host"": ""media-box"" }
            }"));

            Assert.Equal(
                new[] { "Bananas", "Jellyfin.Host", "Url" },
                config.UnknownSettings.Select(x => x.Key).OrderBy(x => x, StringComparer.Ordinal));

            var summary = ConfigDiagnostics.Summarize(config.UnknownSettings, config.SourcePath);
            Assert.NotNull(summary);
            Assert.Contains("3 settings", summary);
        }

        [Fact]
        public void The_shipped_example_maps_onto_the_model_with_nothing_left_over()
        {
            // The template is what a person copies, so a key misspelled in it would teach the
            // mistake. It is also the first thing to drift when a setting is added.
            var example = Path.Combine(AppContext.BaseDirectory, AppConfig.ExampleFileName);
            Assert.True(File.Exists(example), $"the shipped template is missing from {AppContext.BaseDirectory}");

            Assert.Empty(ConfigDiagnostics.Inspect(File.ReadAllText(example)));
        }

        [Fact]
        public void What_the_setup_screen_writes_maps_onto_the_model_with_nothing_left_over()
        {
            // Saving builds an explicit document rather than serialising the object, so a renamed
            // property would leave the file spelling the old name with nothing to complain to.
            Assert.Empty(ConfigDiagnostics.Inspect(ConfigStore.Serialize(new AppConfig())));
        }

        [Fact]
        public void A_document_that_is_not_an_object_is_not_a_pile_of_unknown_keys()
        {
            Assert.Empty(ConfigDiagnostics.Inspect("[ 1, 2, 3 ]"));
            Assert.Empty(ConfigDiagnostics.Inspect("null"));
            Assert.Empty(ConfigDiagnostics.Inspect(""));
            Assert.Empty(ConfigDiagnostics.Inspect(null));
        }

        [Fact]
        public void Comments_and_trailing_commas_are_read_the_way_the_app_reads_them()
        {
            // The README documents Jellyfin settings in jsonc. A file the app loads happily must
            // not be reported as unreadable, and its keys must still be checked.
            var unknown = ConfigDiagnostics.Inspect(@"{
                // the address of the server
                ""Jellyfin"": { ""Url"": ""http://media-box:8096"", },
            }");

            Assert.Equal("Jellyfin.Url", Assert.Single(unknown).Key);
        }

        [Fact]
        public void The_nearest_wrong_answer_in_the_same_object_is_not_suggested()
        {
            // Url has to reach ServerUrl and must not reach Username, which is the floor these
            // suggestions are set by.
            Assert.True(ConfigDiagnostics.Similarity("Url", "ServerUrl") > ConfigDiagnostics.Similarity("Url", "Username"));

            var suggestions = ConfigDiagnostics.Suggest(
                "Url",
                new[] { "ApiKey", "LibraryName", "Password", "ServerUrl", "Username" });

            Assert.Equal(new[] { "ServerUrl" }, suggestions);
        }
    }
}
