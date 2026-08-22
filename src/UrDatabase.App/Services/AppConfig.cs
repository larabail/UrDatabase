using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UrDatabase.Services
{
    public class AppConfig
    {
        public bool DownloadPosters { get; set; } = false;   // false = use TMDb URL; true = cache to disk
        public string TmdbImageSize { get; set; } = "w342";  // common sizes: w185, w342, w500, original

        public string DatabasePath { get; set; } = PlatformPaths.DefaultDatabasePath;
        public string[] WatchFolders { get; set; } = Array.Empty<string>();
        public string TmdbApiKey { get; set; } = "";

        /// <summary>
        /// Optional OMDb key for IMDb ratings. Resolved the same way as the TMDB key.
        /// </summary>
        public string OmdbApiKey { get; set; } = "";

        public string PosterCacheDir { get; set; } = PlatformPaths.DefaultPosterCacheDir;

        /// <summary>
        /// An optional Jellyfin server to browse alongside the local library. Left blank the app
        /// makes no network call for it and shows nothing about it, which is how every install
        /// that predates this setting behaves.
        /// </summary>
        public JellyfinSettings Jellyfin { get; set; } = new();

        /// <summary>File a user copies from the example to configure their own install.</summary>
        public const string FileName = "appsettings.json";

        /// <summary>Tracked template, shipped next to the binary as the fallback.</summary>
        public const string ExampleFileName = "appsettings.example.json";

        /// <summary>
        /// Loads configuration, preferring <c>appsettings.json</c> and falling back to the shipped
        /// example and then to built-in defaults. Never throws: a missing or malformed file yields
        /// a usable config so the app still starts.
        /// </summary>
        public static AppConfig Load(string? path = null)
        {
            var config = ReadFile(path) ?? new AppConfig();
            Normalize(config);
            return config;
        }

        private static AppConfig? ReadFile(string? path)
        {
            foreach (var candidate in CandidatePaths(path))
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var json = File.ReadAllText(candidate);
                    var parsed = JsonSerializer.Deserialize<AppConfig>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            ReadCommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        });
                    if (parsed is not null) return parsed;
                }
                catch
                {
                    // Malformed file: try the next candidate rather than failing startup.
                }
            }

            return null;
        }

        private static string[] CandidatePaths(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) return new[] { path };

            return new[]
            {
                Path.Combine(AppContext.BaseDirectory, FileName),
                Path.Combine(AppContext.BaseDirectory, ExampleFileName)
            };
        }

        /// <summary>
        /// Resolves paths for the current OS, applies platform defaults for anything blank and
        /// falls back to the <c>URDATABASE_TMDB_API_KEY</c> environment variable.
        /// </summary>
        private static void Normalize(AppConfig config)
        {
            config.DatabasePath = Fallback(PlatformPaths.Expand(config.DatabasePath), PlatformPaths.DefaultDatabasePath);
            config.PosterCacheDir = Fallback(PlatformPaths.Expand(config.PosterCacheDir), PlatformPaths.DefaultPosterCacheDir);

            config.WatchFolders = (config.WatchFolders ?? Array.Empty<string>())
                .Select(PlatformPaths.Expand)
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .ToArray();

            if (config.WatchFolders.Length == 0)
                config.WatchFolders = new[] { PlatformPaths.DefaultWatchFolder };

            config.TmdbImageSize = string.IsNullOrWhiteSpace(config.TmdbImageSize) ? "w342" : config.TmdbImageSize.Trim();

            config.TmdbApiKey = ResolveKey(
                config.TmdbApiKey,
                Environment.GetEnvironmentVariable(PlatformPaths.TmdbApiKeyVariable),
                BuildKeys.Tmdb);

            config.OmdbApiKey = ResolveKey(
                config.OmdbApiKey,
                Environment.GetEnvironmentVariable(PlatformPaths.OmdbApiKeyVariable),
                BuildKeys.Omdb);

            config.Jellyfin ??= new JellyfinSettings();
            config.Jellyfin.Normalize();
        }

        /// <summary>
        /// Key precedence, most specific first: the config file, then the environment variable,
        /// then whatever was compiled in at build time. This lets anyone substitute their own key
        /// without rebuilding, and lets a shipped key be rotated by changing a build secret.
        /// </summary>
        internal static string ResolveKey(string? fromConfig, string? fromEnvironment, string? compiledIn)
        {
            if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.Trim();
            if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
            return string.IsNullOrWhiteSpace(compiledIn) ? "" : compiledIn.Trim();
        }

        private static string Fallback(string value, string whenEmpty) =>
            string.IsNullOrWhiteSpace(value) ? whenEmpty : value;
    }
}
