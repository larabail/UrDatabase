using System;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The switch the upload feature hangs off. An install with no SFTP account must behave
    /// exactly as it did before this existed — no button, no connection, no error — and one that
    /// has an account must not be tripped up by the shapes people actually paste into a config
    /// file, which come out of SSH commands and other people's instructions.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class JellyfinSftpSettingsTests : IDisposable
    {
        private readonly EnvironmentVariableScope _environment;

        public JellyfinSftpSettingsTests()
        {
            _environment = new EnvironmentVariableScope(
                PlatformPaths.JellyfinSftpHostVariable,
                PlatformPaths.JellyfinSftpPortVariable,
                PlatformPaths.JellyfinSftpUsernameVariable,
                PlatformPaths.JellyfinSftpKeyVariable,
                PlatformPaths.JellyfinSftpPassphraseVariable,
                PlatformPaths.JellyfinSftpMoviesPathVariable);
        }

        public void Dispose() => _environment.Dispose();

        [Fact]
        public void A_blank_configuration_is_switched_off()
        {
            var settings = new JellyfinSftpSettings();
            settings.Normalize();

            Assert.False(settings.IsConfigured);
            Assert.Equal("", settings.Host);
        }

        /// <summary>
        /// All three or nothing. A half-configured account cannot connect, and a button that is
        /// certain to fail is worse than no button.
        /// </summary>
        [Theory]
        [InlineData("box", "", "", false)]
        [InlineData("box", "uploader", "", false)]
        [InlineData("", "uploader", "/keys/id", false)]
        [InlineData("box", "uploader", "/keys/id", true)]
        public void It_takes_a_host_an_account_and_a_key(string host, string user, string key, bool configured)
        {
            var settings = new JellyfinSftpSettings { Host = host, Username = user, PrivateKeyPath = key };
            settings.Normalize();

            Assert.Equal(configured, settings.IsConfigured);
        }

        [Fact]
        public void A_bare_host_keeps_the_default_port()
        {
            var settings = new JellyfinSftpSettings { Host = "media.invalid" };
            settings.Normalize();

            Assert.Equal("media.invalid", settings.Host);
            Assert.Equal(22, settings.Port);
        }

        /// <summary>
        /// The address is copied out of an SSH command as often as it is typed, so it arrives
        /// carrying a scheme, an account, a port or a trailing slash. Connecting to a host
        /// literally called "uploader@box:2223" fails with a DNS error that explains nothing.
        /// </summary>
        [Theory]
        [InlineData("sftp://media.invalid", "media.invalid", 22)]
        [InlineData("ssh://media.invalid:2223", "media.invalid", 2223)]
        [InlineData("media.invalid:2223", "media.invalid", 2223)]
        [InlineData("media.invalid:2223/", "media.invalid", 2223)]
        [InlineData("  media.invalid  ", "media.invalid", 22)]
        [InlineData("\"media.invalid:2223\"", "media.invalid", 2223)]
        [InlineData("[2001:db8::1]:2223", "2001:db8::1", 2223)]
        [InlineData("2001:db8::1", "2001:db8::1", 22)]
        public void It_reads_a_host_the_way_people_write_one(string input, string host, int port)
        {
            var settings = new JellyfinSftpSettings { Host = input };
            settings.Normalize();

            Assert.Equal(host, settings.Host);
            Assert.Equal(port, settings.Port);
        }

        [Fact]
        public void An_account_typed_into_the_host_is_used_rather_than_lost()
        {
            var settings = new JellyfinSftpSettings { Host = "uploader@media.invalid:2223" };
            settings.Normalize();

            Assert.Equal("media.invalid", settings.Host);
            Assert.Equal(2223, settings.Port);
            Assert.Equal("uploader", settings.Username);
        }

        [Fact]
        public void A_configured_username_wins_over_one_in_the_host()
        {
            var settings = new JellyfinSftpSettings { Host = "someone@media.invalid", Username = "uploader" };
            settings.Normalize();

            Assert.Equal("uploader", settings.Username);
        }

        [Fact]
        public void A_configured_port_wins_over_one_in_the_host()
        {
            var settings = new JellyfinSftpSettings { Host = "media.invalid:2223", Port = 2022 };
            settings.Normalize();

            Assert.Equal(2022, settings.Port);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(70000)]
        public void A_port_that_is_not_a_port_falls_back_to_the_default(int configured)
        {
            var settings = new JellyfinSftpSettings { Host = "media.invalid", Port = configured };
            settings.Normalize();

            Assert.Equal(22, settings.Port);
        }

        [Fact]
        public void The_environment_fills_in_what_the_file_leaves_blank()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpHostVariable, "media.invalid");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpPortVariable, "2223");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpUsernameVariable, "uploader");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpKeyVariable, "/keys/id_ed25519");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpMoviesPathVariable, "films");

            var settings = new JellyfinSftpSettings();
            settings.Normalize();

            Assert.True(settings.IsConfigured);
            Assert.Equal("media.invalid", settings.Host);
            Assert.Equal(2223, settings.Port);
            Assert.Equal("uploader", settings.Username);
            Assert.Equal("/keys/id_ed25519", settings.PrivateKeyPath);
            Assert.Equal("films", settings.MoviesPath);
        }

        [Fact]
        public void The_file_wins_over_the_environment()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpHostVariable, "wrong.invalid");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinSftpPortVariable, "9999");

            var settings = new JellyfinSftpSettings { Host = "media.invalid", Port = 2223 };
            settings.Normalize();

            Assert.Equal("media.invalid", settings.Host);
            Assert.Equal(2223, settings.Port);
        }

        /// <summary>
        /// A path is written with a <c>~</c> far more often than in full, and the message about a
        /// key that is not there has to name the file the user meant rather than the tilde.
        /// </summary>
        [Fact]
        public void The_key_path_is_expanded_like_every_other_configured_path()
        {
            var settings = new JellyfinSftpSettings
            {
                Host = "media.invalid",
                Username = "uploader",
                PrivateKeyPath = "~/.ssh/id_ed25519"
            };

            settings.Normalize();

            Assert.DoesNotContain("~", settings.PrivateKeyPath, StringComparison.Ordinal);
            Assert.EndsWith("id_ed25519", settings.PrivateKeyPath, StringComparison.Ordinal);
        }

        /// <summary>
        /// Spaces are legal in a passphrase, and trimming one produces an authentication failure
        /// nobody could explain.
        /// </summary>
        [Fact]
        public void A_passphrase_keeps_its_spaces()
        {
            var settings = new JellyfinSftpSettings { PrivateKeyPassphrase = "  correct horse  " };
            settings.Normalize();

            Assert.Equal("  correct horse  ", settings.PrivateKeyPassphrase);
        }

        [Fact]
        public void A_blank_movies_path_means_the_usual_one()
        {
            var settings = new JellyfinSftpSettings();
            settings.Normalize();

            Assert.Equal("movies", settings.MoviesPath);
        }

        [Theory]
        [InlineData("/movies/", "/movies")]
        [InlineData("movies/", "movies")]
        [InlineData("\\movies\\", "/movies")]
        [InlineData("  /tank/movies  ", "/tank/movies")]
        public void The_movies_path_keeps_whether_it_was_absolute(string configured, string expected)
        {
            var settings = new JellyfinSftpSettings { MoviesPath = configured };
            settings.Normalize();

            Assert.Equal(expected, settings.MoviesPath);
        }

        /// <summary>
        /// Configuration is normalised once at startup and again by anything that reloads it, so
        /// doing it twice must not slowly eat a path or move a port.
        /// </summary>
        [Fact]
        public void Normalising_twice_changes_nothing()
        {
            var settings = new JellyfinSftpSettings
            {
                Host = "uploader@media.invalid:2223",
                PrivateKeyPath = "/keys/id_ed25519",
                MoviesPath = "/tank/movies/"
            };

            settings.Normalize();

            var host = settings.Host;
            var port = settings.Port;
            var user = settings.Username;
            var movies = settings.MoviesPath;

            settings.Normalize();

            Assert.Equal(host, settings.Host);
            Assert.Equal(port, settings.Port);
            Assert.Equal(user, settings.Username);
            Assert.Equal(movies, settings.MoviesPath);
        }
    }
}
