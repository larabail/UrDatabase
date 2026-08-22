using System;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Configuration is the switch this whole feature hangs off: an install with no server must
    /// behave exactly as it did before, and one with a server must not be tripped up by the
    /// shapes people actually type into a config file.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class JellyfinSettingsTests : IDisposable
    {
        private readonly EnvironmentVariableScope _environment;

        public JellyfinSettingsTests()
        {
            _environment = new EnvironmentVariableScope(
                PlatformPaths.JellyfinUrlVariable,
                PlatformPaths.JellyfinUsernameVariable,
                PlatformPaths.JellyfinPasswordVariable,
                PlatformPaths.JellyfinApiKeyVariable);
        }

        public void Dispose() => _environment.Dispose();

        [Fact]
        public void A_blank_configuration_is_switched_off()
        {
            var settings = new JellyfinSettings();
            settings.Normalize();

            Assert.False(settings.IsConfigured);
            Assert.Equal("", settings.ServerUrl);
        }

        [Fact]
        public void An_address_without_a_username_or_key_is_still_switched_off()
        {
            var settings = new JellyfinSettings { ServerUrl = "http://media.invalid:8096" };
            settings.Normalize();

            Assert.False(settings.IsConfigured);
        }

        [Fact]
        public void A_username_without_an_address_is_switched_off()
        {
            var settings = new JellyfinSettings { Username = "someone" };
            settings.Normalize();

            Assert.False(settings.IsConfigured);
        }

        [Theory]
        [InlineData("media.invalid:8096", "http://media.invalid:8096")]
        [InlineData("http://media.invalid:8096/", "http://media.invalid:8096")]
        [InlineData("  https://media.invalid  ", "https://media.invalid")]
        [InlineData("http://media.invalid/jellyfin/", "http://media.invalid/jellyfin")]
        public void An_address_is_given_a_scheme_and_stripped_of_its_trailing_slash(string input, string expected)
        {
            Assert.Equal(expected, JellyfinSettings.NormalizeServerUrl(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ftp://media.invalid")]
        [InlineData("file:///etc/passwd")]
        public void An_address_that_is_not_http_switches_the_feature_off_rather_than_failing_later(string input)
        {
            Assert.Equal("", JellyfinSettings.NormalizeServerUrl(input));
        }

        [Fact]
        public void A_username_and_password_use_the_user_sign_in()
        {
            var settings = new JellyfinSettings
            {
                ServerUrl = "http://media.invalid",
                Username = "someone",
                Password = "secret"
            };
            settings.Normalize();

            Assert.True(settings.IsConfigured);
            Assert.True(settings.UsesUserAccount);
        }

        [Fact]
        public void A_key_on_its_own_uses_the_key()
        {
            var settings = new JellyfinSettings
            {
                ServerUrl = "http://media.invalid",
                ApiKey = "not-a-real-key"
            };
            settings.Normalize();

            Assert.True(settings.IsConfigured);
            Assert.False(settings.UsesUserAccount);
        }

        [Fact]
        public void A_key_alongside_a_username_but_no_password_authenticates_with_the_key()
        {
            // The only reason to configure both: sign in with the key, read the library as the
            // named user. A password, if present, wins, because it is the narrower credential.
            var settings = new JellyfinSettings
            {
                ServerUrl = "http://media.invalid",
                Username = "someone",
                ApiKey = "not-a-real-key"
            };
            settings.Normalize();

            Assert.False(settings.UsesUserAccount);
        }

        [Fact]
        public void A_password_beats_a_key_when_both_are_configured()
        {
            var settings = new JellyfinSettings
            {
                ServerUrl = "http://media.invalid",
                Username = "someone",
                Password = "secret",
                ApiKey = "not-a-real-key"
            };
            settings.Normalize();

            Assert.True(settings.UsesUserAccount);
        }

        [Fact]
        public void The_environment_fills_in_anything_the_file_leaves_blank()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinUrlVariable, "media.invalid:8096");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinUsernameVariable, "someone");
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinPasswordVariable, "secret");

            var settings = new JellyfinSettings();
            settings.Normalize();

            Assert.Equal("http://media.invalid:8096", settings.ServerUrl);
            Assert.Equal("someone", settings.Username);
            Assert.Equal("secret", settings.Password);
            Assert.True(settings.IsConfigured);
        }

        [Fact]
        public void The_file_beats_the_environment()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.JellyfinUrlVariable, "http://from-environment.invalid");

            var settings = new JellyfinSettings { ServerUrl = "http://from-file.invalid" };
            settings.Normalize();

            Assert.Equal("http://from-file.invalid", settings.ServerUrl);
        }

        [Fact]
        public void A_password_keeps_its_leading_and_trailing_spaces()
        {
            // Trimming a password silently turns a correct one into a rejected one, and the
            // rejection looks like a typo rather than like the app editing the value.
            var settings = new JellyfinSettings
            {
                ServerUrl = "http://media.invalid",
                Username = "someone",
                Password = "  spaces matter  "
            };
            settings.Normalize();

            Assert.Equal("  spaces matter  ", settings.Password);
        }
    }
}
