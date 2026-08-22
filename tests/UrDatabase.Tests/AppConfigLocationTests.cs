using System;
using System.IO;
using System.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Where configuration is read from, and where it is written.
    ///
    /// This matters because of what an installed app is. On macOS the executable lives inside
    /// <c>UrDatabase.app</c>, which is code signed: a file written next to it breaks the seal, and
    /// Gatekeeper then refuses to launch the app at all. Configuration therefore has to be read
    /// from — and created in — the same per-user directory that already holds the database, the
    /// poster cache and the logs.
    ///
    /// Every test here builds a fake install out of two temporary directories, so nothing touches
    /// a real one and the assertions hold identically on Windows and macOS.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class AppConfigLocationTests : IDisposable
    {
        private readonly string _root;
        private readonly string _appData;
        private readonly string _bundle;
        private readonly EnvironmentVariableScope _environment;

        public AppConfigLocationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-loc-" + Guid.NewGuid().ToString("N"));
            _appData = Path.Combine(_root, "appdata");
            _bundle = Path.Combine(_root, "bundle");

            Directory.CreateDirectory(_appData);
            Directory.CreateDirectory(_bundle);

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
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string UserConfig => Path.Combine(_appData, AppConfig.FileName);
        private string BundleConfig => Path.Combine(_bundle, AppConfig.FileName);
        private string BundleExample => Path.Combine(_bundle, AppConfig.ExampleFileName);

        private string Write(string path, string json)
        {
            File.WriteAllText(path, json);
            return path;
        }

        private static string ConfigNaming(string image) => $$"""{ "TmdbImageSize": "{{image}}" }""";

        /// <summary>Names and contents of a directory, for proving that nothing changed in it.</summary>
        private static (string Name, string Content)[] Snapshot(string directory) =>
            Directory.GetFiles(directory)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
                .ToArray();

        // ---------- precedence ----------

        [Fact]
        public void The_users_own_config_wins_over_the_shipped_example()
        {
            Write(UserConfig, ConfigNaming("w500"));
            Write(BundleExample, ConfigNaming("w185"));

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w500", config.TmdbImageSize);
            Assert.Equal(UserConfig, config.SourcePath);
        }

        [Fact]
        public void The_users_own_config_wins_over_one_beside_the_executable()
        {
            Write(UserConfig, ConfigNaming("w500"));
            Write(BundleConfig, ConfigNaming("w185"));
            Write(BundleExample, ConfigNaming("original"));

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w500", config.TmdbImageSize);
        }

        [Fact]
        public void A_config_beside_the_executable_is_read_when_the_user_has_none()
        {
            // Running from a build tree, which is how the app is developed.
            Write(BundleConfig, ConfigNaming("w185"));
            Write(BundleExample, ConfigNaming("original"));

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w185", config.TmdbImageSize);
            Assert.Equal(BundleConfig, config.SourcePath);
        }

        [Fact]
        public void The_shipped_example_supplies_the_values_when_nothing_else_exists()
        {
            Write(BundleExample, ConfigNaming("original"));

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("original", config.TmdbImageSize);
        }

        [Fact]
        public void A_missing_user_config_still_starts_cleanly_on_the_defaults()
        {
            // No file anywhere: no example, no local override, nothing in the user directory.
            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal(PlatformPaths.DefaultDatabasePath, config.DatabasePath);
            Assert.Equal(PlatformPaths.DefaultPosterCacheDir, config.PosterCacheDir);
            Assert.Equal("w342", config.TmdbImageSize);
            Assert.Equal("", config.TmdbApiKey);
            Assert.False(config.Jellyfin.IsConfigured);
        }

        [Fact]
        public void A_malformed_user_config_falls_through_to_the_next_candidate()
        {
            Write(UserConfig, "{ half a file");
            Write(BundleExample, ConfigNaming("w185"));

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w185", config.TmdbImageSize);
            Assert.Equal(BundleExample, config.SourcePath);
        }

        [Fact]
        public void An_explicit_path_beats_every_convention()
        {
            Write(UserConfig, ConfigNaming("w500"));
            var named = Write(Path.Combine(_root, "somewhere-else.json"), ConfigNaming("original"));

            var config = AppConfig.Load(named, _appData, _bundle);

            Assert.Equal("original", config.TmdbImageSize);
            Assert.Equal(named, config.SourcePath);
        }

        [Fact]
        public void Nothing_read_from_anywhere_leaves_the_source_unset()
        {
            var config = AppConfig.Load(Path.Combine(_root, "does-not-exist.json"), _appData, _bundle);

            Assert.Null(config.SourcePath);
        }

        [Fact]
        public void Environment_variables_still_layer_on_top_of_the_user_config()
        {
            Write(UserConfig, """{ "TmdbApiKey": "", "Jellyfin": { "ApiKey": "from-file" } }""");
            Environment.SetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable, "from-environment");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinUrlVariable, "media.invalid:8096");

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("from-environment", config.TmdbApiKey);
            Assert.Equal("http://media.invalid:8096", config.Jellyfin.ServerUrl);
        }

        // ---------- the first run ----------

        [Fact]
        public void A_first_run_creates_the_user_config_from_the_shipped_example()
        {
            var exampleJson = ConfigNaming("w185");
            Write(BundleExample, exampleJson);

            AppConfig.Load(null, _appData, _bundle);

            Assert.True(File.Exists(UserConfig), $"Expected a settings file at {UserConfig}");
            Assert.Equal(exampleJson, File.ReadAllText(UserConfig));
        }

        [Fact]
        public void A_first_run_creates_the_user_directory_it_needs()
        {
            var fresh = Path.Combine(_root, "never-used");
            Write(BundleExample, ConfigNaming("w185"));

            AppConfig.Load(null, fresh, _bundle);

            Assert.True(File.Exists(Path.Combine(fresh, AppConfig.FileName)));
        }

        [Fact]
        public void A_seeded_config_is_a_readable_file_that_configures_nothing()
        {
            // With no example to copy, the seed is generated. It still has to parse, and it still
            // has to leave every optional feature switched off.
            AppConfig.Load(null, _appData, _bundle);

            Assert.True(File.Exists(UserConfig));

            var seeded = AppConfig.Load(UserConfig);
            Assert.Equal("", seeded.TmdbApiKey);
            Assert.Equal("", seeded.OmdbApiKey);
            Assert.False(seeded.Jellyfin.IsConfigured);
            Assert.Equal(PlatformPaths.DefaultDatabasePath, seeded.DatabasePath);
        }

        [Fact]
        public void An_existing_user_config_is_never_overwritten()
        {
            var mine = ConfigNaming("w500");
            Write(UserConfig, mine);
            Write(BundleExample, ConfigNaming("w185"));

            AppConfig.Load(null, _appData, _bundle);

            Assert.Equal(mine, File.ReadAllText(UserConfig));
        }

        [Fact]
        public void No_user_config_is_seeded_over_the_top_of_one_beside_the_executable()
        {
            // Seeding here would shadow a developer's own settings with an empty file, and the
            // symptom — configuration that silently stops applying — is horrible to diagnose.
            Write(BundleConfig, ConfigNaming("w185"));
            Write(BundleExample, ConfigNaming("original"));

            AppConfig.Load(null, _appData, _bundle);

            Assert.False(File.Exists(UserConfig));
        }

        [Fact]
        public void A_config_written_beside_the_executable_after_the_first_run_is_still_read()
        {
            // The order the setup guide describes: run it, then write a local config. The seed
            // from the first run must not quietly outrank the file written second.
            Write(BundleExample, ConfigNaming("original"));
            AppConfig.Load(null, _appData, _bundle);
            Assert.True(File.Exists(UserConfig));

            Write(BundleConfig, ConfigNaming("w185"));
            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w185", config.TmdbImageSize);
            Assert.Equal(BundleConfig, config.SourcePath);
        }

        [Fact]
        public void A_generated_seed_does_not_outrank_a_config_beside_the_executable_either()
        {
            // Same again for the build with no example to copy, where the seed is generated.
            AppConfig.Load(null, _appData, _bundle);
            Assert.True(File.Exists(UserConfig));

            Write(BundleConfig, ConfigNaming("w185"));
            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w185", config.TmdbImageSize);
        }

        [Fact]
        public void A_user_config_that_has_been_edited_outranks_everything_again()
        {
            Write(BundleExample, ConfigNaming("original"));
            AppConfig.Load(null, _appData, _bundle);
            Write(BundleConfig, ConfigNaming("w185"));

            // One edit is all it takes to make it the user's own file rather than the app's copy.
            Write(UserConfig, ConfigNaming("w500"));
            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal("w500", config.TmdbImageSize);
            Assert.Equal(UserConfig, config.SourcePath);
        }

        [Fact]
        public void A_seed_is_still_read_when_there_is_nothing_beside_the_executable_to_prefer()
        {
            Write(BundleExample, ConfigNaming("original"));

            var config = AppConfig.Load(null, _appData, _bundle);

            Assert.Equal(UserConfig, config.SourcePath);
            Assert.Equal("original", config.TmdbImageSize);
        }

        [Fact]
        public void A_seeded_copy_is_recognised_as_recording_no_decision()
        {
            // The same rule keeps the setup screen appearing: a file the app put there itself
            // must not read as "this install has been configured".
            Write(BundleExample, ConfigNaming("original"));
            AppConfig.Load(null, _appData, _bundle);

            Assert.True(AppConfig.IsUntouchedTemplate(UserConfig, BundleExample));
        }

        [Fact]
        public void One_edit_makes_it_the_users_own_file()
        {
            Write(BundleExample, ConfigNaming("original"));
            AppConfig.Load(null, _appData, _bundle);
            Write(UserConfig, ConfigNaming("w500"));

            Assert.False(AppConfig.IsUntouchedTemplate(UserConfig, BundleExample));
        }

        [Fact]
        public void A_generated_seed_is_recognised_even_with_no_example_to_compare_against()
        {
            AppConfig.Load(null, _appData, _bundle);

            Assert.True(AppConfig.IsUntouchedTemplate(UserConfig, Path.Combine(_bundle, AppConfig.ExampleFileName)));
        }

        [Fact]
        public void A_file_that_is_not_there_is_not_an_untouched_template()
        {
            Assert.False(AppConfig.IsUntouchedTemplate(UserConfig, BundleExample));
        }

        [Fact]
        public void An_explicit_path_seeds_nothing()
        {
            var named = Write(Path.Combine(_root, "named.json"), ConfigNaming("w500"));

            AppConfig.Load(named, _appData, _bundle);

            Assert.False(File.Exists(UserConfig));
        }

        // ---------- the bundle is read-only ----------

        [Fact]
        public void Loading_never_writes_beside_the_executable()
        {
            Write(BundleExample, ConfigNaming("w185"));
            var before = Snapshot(_bundle);

            AppConfig.Load(null, _appData, _bundle);
            AppConfig.Load(null, _appData, _bundle);

            Assert.Equal(before, Snapshot(_bundle));
        }

        [Fact]
        public void Loading_never_writes_beside_the_executable_when_there_is_no_example_to_copy()
        {
            var before = Snapshot(_bundle);

            AppConfig.Load(null, _appData, _bundle);

            Assert.Empty(before);
            Assert.Empty(Snapshot(_bundle));
        }

        [Fact]
        public void The_seed_goes_to_the_user_directory_and_not_to_the_executables()
        {
            Write(BundleExample, ConfigNaming("w185"));

            var written = AppConfig.EnsureUserConfig(_appData, _bundle);

            Assert.Equal(UserConfig, written);
            Assert.False(File.Exists(BundleConfig));
        }

        [Fact]
        public void A_user_directory_that_cannot_be_created_is_not_a_failed_start()
        {
            // A file where the directory should be stands in for a read-only or sandboxed home.
            var blocked = Write(Path.Combine(_root, "blocked"), "not a directory");
            Write(BundleExample, ConfigNaming("w185"));

            var written = AppConfig.EnsureUserConfig(blocked, _bundle);
            var config = AppConfig.Load(null, blocked, _bundle);

            Assert.Null(written);
            Assert.Equal("w185", config.TmdbImageSize);
        }

        // ---------- the real defaults ----------

        [Fact]
        public void The_user_config_path_sits_beside_the_database_and_the_logs()
        {
            // All four belong in one writable place. If this ever diverges, a user editing the
            // file the app names would be editing a file it does not read.
            Assert.Equal(Path.Combine(PlatformPaths.AppDataRoot, AppConfig.FileName), ConfigStore.UserPath);
            Assert.Equal(PlatformPaths.AppDataRoot, Path.GetDirectoryName(PlatformPaths.DefaultDatabasePath));
            Assert.Equal(PlatformPaths.AppDataRoot, Path.GetDirectoryName(ConfigStore.UserPath));
        }

        [Fact]
        public void The_user_config_is_never_inside_the_application_bundle()
        {
            Assert.DoesNotContain(".app/Contents", ConfigStore.UserPath, StringComparison.Ordinal);
            Assert.False(
                ConfigStore.UserPath.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal),
                "Settings must not resolve to a location next to the executable.");
        }

        [Fact]
        public void The_candidates_are_ordered_user_then_executable_then_example()
        {
            var candidates = AppConfig.CandidatePaths(null, _appData, _bundle);

            Assert.Equal(new[] { UserConfig, BundleConfig, BundleExample }, candidates);
        }
    }
}
