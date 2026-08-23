using System;
using System.IO;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Writing configuration back out, which the setup screen is the first thing to need.
    ///
    /// The tests that matter most here are the ones about what must *not* end up in the file: a
    /// key from the environment, or one compiled into an official build, would otherwise be
    /// copied onto the user's disk the first time they pressed Save, under their own name and
    /// with nobody aware it needed rotating.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class ConfigStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly EnvironmentVariableScope _environment;

        public ConfigStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-store-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            _environment = new EnvironmentVariableScope(
                PlatformPaths.TmdbApiKeyVariable,
                PlatformPaths.OmdbApiKeyVariable,
                PlatformPaths.UrActorApiKeyVariable,
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

        private string Path_(string name) => Path.Combine(_dir, name);

        // ---------- round trip ----------

        [Fact]
        public void What_is_saved_is_what_loads_back()
        {
            var path = Path_("appsettings.json");

            ConfigStore.Save(new AppConfig
            {
                WatchFolders = new[] { _dir },
                TmdbApiKey = "typed-by-the-user",
                UrActorApiKey = "uractor-typed-by-the-user",
                DownloadPosters = true,
                TmdbImageSize = "w500",
                SetupCompleted = true,
                Jellyfin = new JellyfinSettings
                {
                    ServerUrl = "http://media.invalid:8096",
                    Username = "viewer",
                    Password = "hunter2",
                    LibraryName = "Films"
                }
            }, path);

            var reloaded = AppConfig.Load(path);

            Assert.Equal(new[] { _dir }, reloaded.WatchFolders);
            Assert.Equal("typed-by-the-user", reloaded.TmdbApiKey);

            // Every key AppConfig carries has to be in the written document. Serialize lists the
            // fields explicitly, so a key added to the config and forgotten here is silently
            // wiped the first time the user saves anything from the setup screen.
            Assert.Equal("uractor-typed-by-the-user", reloaded.UrActorApiKey);
            Assert.True(reloaded.DownloadPosters);
            Assert.Equal("w500", reloaded.TmdbImageSize);
            Assert.True(reloaded.SetupCompleted);
            Assert.Equal("http://media.invalid:8096", reloaded.Jellyfin.ServerUrl);
            Assert.Equal("viewer", reloaded.Jellyfin.Username);
            Assert.Equal("hunter2", reloaded.Jellyfin.Password);
            Assert.Equal("Films", reloaded.Jellyfin.LibraryName);
        }

        [Fact]
        public void Saving_creates_the_folder_it_is_saving_into()
        {
            var path = Path.Combine(_dir, "nested", "deeper", AppConfig.FileName);

            ConfigStore.Save(new AppConfig { SetupCompleted = true }, path);

            Assert.True(File.Exists(path));
        }

        [Fact]
        public void A_path_that_only_matches_this_platforms_default_is_written_as_blank()
        {
            // Baking the resolved default in would freeze one machine's application data
            // directory into a file that is meant to describe a preference, not a location.
            var json = ConfigStore.Serialize(new AppConfig
            {
                DatabasePath = PlatformPaths.DefaultDatabasePath,
                PosterCacheDir = PlatformPaths.DefaultPosterCacheDir
            });

            Assert.Contains("\"DatabasePath\": \"\"", json);
            Assert.Contains("\"PosterCacheDir\": \"\"", json);
        }

        // ---------- what must never be written ----------

        [Fact]
        public void A_resolved_configuration_is_refused()
        {
            var resolved = AppConfig.Load(Path_("does-not-exist.json"));

            Assert.True(resolved.IsResolved);
            Assert.Throws<InvalidOperationException>(() => ConfigStore.Save(resolved, Path_("out.json")));
        }

        [Fact]
        public void A_key_taken_from_the_environment_never_reaches_the_saved_file()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable, "key-from-the-environment");

            var source = Path_("appsettings.json");
            File.WriteAllText(source, "{}");

            var raw = AppConfig.ReadRaw(source);
            var saved = Path_("written.json");

            ConfigStore.Save(SetupChoices.From(raw).ToConfig(raw), saved);

            Assert.DoesNotContain("key-from-the-environment", File.ReadAllText(saved));
            Assert.Equal("key-from-the-environment", AppConfig.Load(saved).TmdbApiKey);
        }

        [Fact]
        public void A_password_kept_in_the_environment_never_reaches_the_saved_file()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinPasswordVariable, "kept-out-of-files");

            var source = Path_("appsettings.json");
            File.WriteAllText(source, """{ "Jellyfin": { "ServerUrl": "http://media.invalid:8096", "Username": "viewer" } }""");

            var raw = AppConfig.ReadRaw(source);
            var saved = Path_("written.json");

            ConfigStore.Save(SetupChoices.From(raw).ToConfig(raw), saved);

            Assert.DoesNotContain("kept-out-of-files", File.ReadAllText(saved));
        }

        [Fact]
        public void Reading_raw_ignores_the_environment_and_the_platform_defaults()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.OmdbApiKeyVariable, "key-from-the-environment");

            var raw = AppConfig.ReadRaw(Path_("nothing-here.json"));

            Assert.Equal("", raw.OmdbApiKey);
            Assert.Equal("", raw.DatabasePath);
            Assert.Empty(raw.WatchFolders);
            Assert.False(raw.IsResolved);
        }

        // ---------- where a save lands ----------

        [Fact]
        public void The_documented_file_beside_the_app_is_the_first_choice()
        {
            var chosen = ConfigStore.ChooseSavePath(
                portablePath: "/app/appsettings.json",
                userPath: "/home/appsettings.json",
                fileExists: _ => false,
                directoryAcceptsWrites: _ => true);

            Assert.Equal("/app/appsettings.json", chosen);
        }

        [Fact]
        public void A_read_only_install_folder_falls_back_to_the_users_own_folder()
        {
            var chosen = ConfigStore.ChooseSavePath(
                portablePath: "/app/appsettings.json",
                userPath: "/home/appsettings.json",
                fileExists: _ => false,
                directoryAcceptsWrites: _ => false);

            Assert.Equal("/home/appsettings.json", chosen);
        }

        [Fact]
        public void A_file_that_already_exists_is_the_one_that_gets_written()
        {
            // Saving somewhere else would leave two configurations behind, only one of which is
            // ever read, and the user's next change would appear to do nothing.
            var chosen = ConfigStore.ChooseSavePath(
                portablePath: "/app/appsettings.json",
                userPath: "/home/appsettings.json",
                fileExists: path => path == "/home/appsettings.json",
                directoryAcceptsWrites: _ => true);

            Assert.Equal("/home/appsettings.json", chosen);
        }

        [Fact]
        public void Turning_local_films_off_really_stops_the_scan_reaching_them()
        {
            var path = Path_("appsettings.json");

            var choices = new SetupChoices
            {
                UseLocalFolders = false,
                UseJellyfin = true,
                ServerUrl = "http://media.invalid:8096",
                Username = "viewer"
            };

            ConfigStore.Save(choices.ToConfig(AppConfig.ReadRaw(path)), path);

            // Saving [] and reading back the platform default would put every film in the user's
            // home movie folder into a library they had just said was Jellyfin only.
            Assert.Empty(AppConfig.Load(path).WatchFolders);
        }

        [Fact]
        public void A_writable_folder_is_recognised_and_an_impossible_one_is_not()
        {
            Assert.True(ConfigStore.DirectoryAcceptsWrites(_dir));
            Assert.False(ConfigStore.DirectoryAcceptsWrites(""));
            Assert.False(ConfigStore.DirectoryAcceptsWrites(Path.Combine(_dir, "a-file-not-a-folder", "\0")));
        }

        [Fact]
        public void The_shipped_example_is_read_last_and_never_written()
        {
            var order = ConfigStore.ReadOrder;

            Assert.Equal(ConfigStore.ExamplePath, order[^1]);
            Assert.Contains(ConfigStore.UserPath, order);
            Assert.Contains(ConfigStore.PortablePath, order);
            Assert.DoesNotContain(ConfigStore.ExamplePath, new[] { ConfigStore.PortablePath, ConfigStore.UserPath });
        }

        // ---------- never inside the bundle ----------

        [Theory]
        [InlineData("/Applications/UrDatabase.app/Contents/MacOS")]
        [InlineData("/Applications/UrDatabase.app/Contents")]
        [InlineData("/Users/someone/Desktop/UrDatabase.app/Contents/MacOS/")]
        [InlineData(@"C:\Program Files\UrDatabase.app\Contents\MacOS")]
        public void A_path_inside_an_application_bundle_is_recognised(string directory)
        {
            Assert.True(ConfigStore.IsInsideApplicationBundle(directory));
        }

        [Theory]
        [InlineData("/Users/someone/Library/Application Support/UrDatabase")]
        [InlineData("/repo/src/UrDatabase.App/bin/Release/net8.0")]
        [InlineData(@"C:\Users\someone\AppData\Roaming\UrDatabase")]
        [InlineData("")]
        public void An_ordinary_folder_is_not_mistaken_for_a_bundle(string directory)
        {
            Assert.False(ConfigStore.IsInsideApplicationBundle(directory));
        }

        [Fact]
        public void A_bundle_is_refused_as_a_place_to_save_however_writable_it_looks()
        {
            // The bundle is owned by whoever installed it, so it usually passes a write test —
            // and writing there is exactly what invalidates the signature and stops the app
            // launching. The check happens before the disk is touched at all.
            Assert.False(ConfigStore.AcceptsConfiguration("/Applications/UrDatabase.app/Contents/MacOS"));
            Assert.True(ConfigStore.AcceptsConfiguration(_dir));
        }

        [Fact]
        public void A_save_from_inside_a_bundle_lands_in_the_users_own_folder()
        {
            var chosen = ConfigStore.ChooseSavePath(
                portablePath: "/Applications/UrDatabase.app/Contents/MacOS/appsettings.json",
                userPath: "/home/appsettings.json",
                fileExists: _ => true,
                directoryAcceptsWrites: ConfigStore.AcceptsConfiguration);

            Assert.Equal("/home/appsettings.json", chosen);
        }

        [Fact]
        public void Nothing_this_install_would_write_to_sits_inside_a_bundle()
        {
            Assert.False(ConfigStore.IsInsideApplicationBundle(ConfigStore.UserPath));
            Assert.False(ConfigStore.IsInsideApplicationBundle(ConfigStore.SavePath));
        }
    }
}
