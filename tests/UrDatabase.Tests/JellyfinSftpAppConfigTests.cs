using System;
using System.IO;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The SFTP account as seen through the configuration file. The important assertion is the
    /// negative one: an install that has never heard of any of this has to keep behaving exactly
    /// as it did, because that is every existing install and the feature is invisible without it.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class JellyfinSftpAppConfigTests : IDisposable
    {
        private readonly string _dir;
        private readonly EnvironmentVariableScope _environment;

        public JellyfinSftpAppConfigTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-sftpcfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            // A developer machine with a real upload account exported would otherwise configure
            // the feature behind the test's back.
            _environment = new EnvironmentVariableScope(
                PlatformPaths.TmdbApiKeyVariable,
                PlatformPaths.OmdbApiKeyVariable,
                PlatformPaths.JellyfinSftpHostVariable,
                PlatformPaths.JellyfinSftpPortVariable,
                PlatformPaths.JellyfinSftpUsernameVariable,
                PlatformPaths.JellyfinSftpKeyVariable,
                PlatformPaths.JellyfinSftpPassphraseVariable,
                PlatformPaths.JellyfinSftpMoviesPathVariable);
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
        public void A_configuration_that_never_mentions_it_leaves_uploading_switched_off()
        {
            var config = AppConfig.Load(WriteConfig("""{ "TmdbImageSize": "w342" }"""));

            Assert.NotNull(config.JellyfinSftp);
            Assert.False(config.JellyfinSftp.IsConfigured);
        }

        [Fact]
        public void A_complete_account_switches_it_on()
        {
            var config = AppConfig.Load(WriteConfig("""
                {
                  "JellyfinSftp": {
                    "Host": "media.invalid",
                    "Port": 2223,
                    "Username": "uploader",
                    "PrivateKeyPath": "/keys/id_ed25519",
                    "MoviesPath": "movies"
                  }
                }
                """));

            Assert.True(config.JellyfinSftp.IsConfigured);
            Assert.Equal("media.invalid", config.JellyfinSftp.Host);
            Assert.Equal(2223, config.JellyfinSftp.Port);
            Assert.Equal("movies", config.JellyfinSftp.MoviesPath);
        }

        /// <summary>
        /// The Settings screen writes an explicit document rather than serialising the object, so
        /// a setting missing from either half of the round trip is not merely absent from a new
        /// file — it is deleted from an existing one. Somebody who configures uploading by hand
        /// and then changes their watch folders must not lose their server account for it.
        ///
        /// Deliberately driven through <see cref="SetupChoices"/> rather than by editing a config
        /// directly, because that is what the Save button runs. An earlier version of this test
        /// mutated the raw config itself, which skipped the half that was losing the data and
        /// passed while the real path wiped it.
        /// </summary>
        [Fact]
        public void Saving_from_the_settings_screen_keeps_an_account_configured_by_hand()
        {
            var path = WriteConfig("""
                {
                  "JellyfinSftp": {
                    "Host": "media.invalid",
                    "Port": 2223,
                    "Username": "uploader",
                    "PrivateKeyPath": "/keys/id_ed25519",
                    "PrivateKeyPassphrase": "not-a-real-passphrase",
                    "MoviesPath": "movies"
                  },
                  "DownloadFolder": "/films/from-the-server",
                  "WatchFolders": [ "/films" ]
                }
                """);

            var raw = AppConfig.ReadRaw(path);
            var choices = SetupChoices.From(raw);

            ConfigStore.Save(choices.ToConfig(raw), path);

            var reloaded = AppConfig.Load(path);

            Assert.True(reloaded.JellyfinSftp.IsConfigured);
            Assert.Equal("media.invalid", reloaded.JellyfinSftp.Host);
            Assert.Equal(2223, reloaded.JellyfinSftp.Port);
            Assert.Equal("uploader", reloaded.JellyfinSftp.Username);
            Assert.Equal("/keys/id_ed25519", reloaded.JellyfinSftp.PrivateKeyPath);
            Assert.Equal("not-a-real-passphrase", reloaded.JellyfinSftp.PrivateKeyPassphrase);

            // The download folder is the same kind of setting and was being lost the same way.
            Assert.Equal("/films/from-the-server", reloaded.DownloadFolder);
        }

        /// <summary>
        /// The trap this whole diagnostic exists for, in its newest form: a key that looks right,
        /// deserialises to nothing, and leaves a feature silently switched off.
        /// </summary>
        [Fact]
        public void A_mistyped_key_is_reported_with_the_one_that_was_meant()
        {
            var config = AppConfig.Load(WriteConfig("""
                {
                  "JellyfinSftp": {
                    "Host": "media.invalid",
                    "User": "uploader",
                    "PrivateKeyPath": "/keys/id_ed25519"
                  }
                }
                """));

            Assert.False(config.JellyfinSftp.IsConfigured);

            var unknown = config.UnknownSettings.Single();
            Assert.Equal("JellyfinSftp.User", unknown.Key);
            Assert.Contains("Username", unknown.Suggestions);
        }

        [Fact]
        public void The_tracked_example_file_configures_no_account_and_carries_no_key()
        {
            // It is committed to a public repository. It must never hold a working anything, and
            // a private key path is somebody's own machine even when the key itself is not in it.
            var example = Path.Combine(AppContext.BaseDirectory, AppConfig.ExampleFileName);
            Assert.True(File.Exists(example), $"Expected the shipped example at {example}");

            var config = AppConfig.Load(example);

            Assert.False(config.JellyfinSftp.IsConfigured);
            Assert.Equal("", config.JellyfinSftp.Host);
            Assert.Equal("", config.JellyfinSftp.Username);
            Assert.Equal("", config.JellyfinSftp.PrivateKeyPath);
            Assert.Equal("", config.JellyfinSftp.PrivateKeyPassphrase);
        }

        [Fact]
        public void An_account_may_be_configured_entirely_from_the_environment()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpHostVariable, "media.invalid");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpUsernameVariable, "uploader");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpKeyVariable, "/keys/id_ed25519");

            var config = AppConfig.Load(WriteConfig("""{ "TmdbImageSize": "w342" }"""));

            Assert.True(config.JellyfinSftp.IsConfigured);
            Assert.Equal("media.invalid", config.JellyfinSftp.Host);
        }
    }
}
