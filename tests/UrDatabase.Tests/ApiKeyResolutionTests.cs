using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The key resolution chain is the part most likely to break silently, so each step is
    /// asserted directly as well as through <see cref="AppConfig.Load"/>.
    /// </summary>
    [Collection(EnvironmentVariables.CollectionName)]
    public class ApiKeyResolutionTests : IDisposable
    {
        private readonly string _dir;
        private readonly EnvironmentVariableScope _environment;

        public ApiKeyResolutionTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-keys-" + Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void Config_beats_environment_and_compiled_in()
        {
            Assert.Equal("from-config", AppConfig.ResolveKey("from-config", "from-environment", "compiled-in"));
        }

        [Fact]
        public void Environment_beats_compiled_in_when_config_is_empty()
        {
            Assert.Equal("from-environment", AppConfig.ResolveKey("", "from-environment", "compiled-in"));
        }

        [Fact]
        public void Compiled_in_is_used_when_nothing_else_is_supplied()
        {
            Assert.Equal("compiled-in", AppConfig.ResolveKey(null, null, "compiled-in"));
        }

        [Fact]
        public void An_empty_result_is_returned_when_no_key_exists_anywhere()
        {
            Assert.Equal("", AppConfig.ResolveKey(null, null, null));
            Assert.Equal("", AppConfig.ResolveKey("  ", "  ", "  "));
        }

        [Fact]
        public void Values_are_trimmed_at_every_level()
        {
            Assert.Equal("from-config", AppConfig.ResolveKey("  from-config  ", null, null));
            Assert.Equal("from-environment", AppConfig.ResolveKey(" ", " from-environment ", null));
            Assert.Equal("compiled-in", AppConfig.ResolveKey(null, null, " compiled-in "));
        }

        [Fact]
        public void Load_prefers_the_config_file_over_the_environment_for_both_keys()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable, "tmdb-environment");
            Environment.SetEnvironmentVariable(PlatformPaths.OmdbApiKeyVariable, "omdb-environment");

            var path = Path.Combine(_dir, "appsettings.json");
            File.WriteAllText(path, @"{ ""TmdbApiKey"": ""tmdb-file"", ""OmdbApiKey"": ""omdb-file"" }");

            var config = AppConfig.Load(path);

            Assert.Equal("tmdb-file", config.TmdbApiKey);
            Assert.Equal("omdb-file", config.OmdbApiKey);
        }

        [Fact]
        public void Load_falls_back_to_the_environment_for_both_keys()
        {
            Environment.SetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable, "tmdb-environment");
            Environment.SetEnvironmentVariable(PlatformPaths.OmdbApiKeyVariable, "omdb-environment");

            var path = Path.Combine(_dir, "appsettings.json");
            File.WriteAllText(path, @"{ ""TmdbApiKey"": """", ""OmdbApiKey"": """" }");

            var config = AppConfig.Load(path);

            Assert.Equal("tmdb-environment", config.TmdbApiKey);
            Assert.Equal("omdb-environment", config.OmdbApiKey);
        }

        [Fact]
        public void Compiled_in_keys_default_to_empty_so_a_local_build_needs_no_secrets()
        {
            // Guards the contributor experience: `dotnet build` with no keys must stay valid.
            Assert.Equal("", BuildKeys.Read(typeof(AppConfig).Assembly, BuildKeys.TmdbMetadataName));
            Assert.Equal("", BuildKeys.Read(typeof(AppConfig).Assembly, BuildKeys.OmdbMetadataName));
        }

        [Fact]
        public void Reading_an_unknown_compiled_in_key_yields_empty_rather_than_throwing()
        {
            Assert.Equal("", BuildKeys.Read(typeof(AppConfig).Assembly, "NoSuchKey"));
        }

        [Fact]
        public void With_no_keys_anywhere_the_app_still_loads_a_usable_config()
        {
            var path = Path.Combine(_dir, "appsettings.json");
            File.WriteAllText(path, "{}");

            var config = AppConfig.Load(path);

            Assert.Equal("", config.TmdbApiKey);
            Assert.Equal("", config.OmdbApiKey);
            Assert.False(string.IsNullOrWhiteSpace(config.DatabasePath));
        }
    }
}
