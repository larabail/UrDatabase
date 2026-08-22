using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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

        /// <summary>
        /// Set by the setup screen once the user has answered it, and the only thing that stops
        /// it being offered again. It is written even when the user skips, because a person who
        /// declined to answer a question has answered it.
        /// </summary>
        public bool SetupCompleted { get; set; } = false;

        /// <summary>
        /// True once <see cref="Normalize"/> has folded environment variables and compiled-in
        /// keys into this instance. Such a config describes this machine at this moment rather
        /// than what the user configured, and <see cref="ConfigStore.Save"/> refuses to write one:
        /// doing so would copy an official build's TMDB key, or a password deliberately kept in
        /// the environment, into a file that nobody put it in and nobody would think to clean out.
        /// </summary>
        [JsonIgnore]
        internal bool IsResolved { get; private set; }

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

        /// <summary>
        /// The configuration exactly as the user's own file has it — no environment fallbacks, no
        /// compiled-in keys, no platform paths substituted for blanks. This is what the setup
        /// screen edits and what gets written back, so that saving changes only the answers the
        /// user actually gave.
        ///
        /// Returns an empty configuration when there is no file yet, and deliberately never
        /// reads the shipped example: its placeholders are not this user's answers.
        /// </summary>
        public static AppConfig ReadRaw(string? path = null)
        {
            var candidate = path ?? ConfigStore.ExistingPath;
            var config = candidate is null ? null : ReadFile(candidate);

            return config ?? new AppConfig
            {
                DatabasePath = "",
                PosterCacheDir = "",
                WatchFolders = Array.Empty<string>()
            };
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

            return ConfigStore.ReadOrder.ToArray();
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

            // An install that has never been asked gets the platform's film folder, which is the
            // only useful guess available. One that has been asked and named no folder meant it:
            // substituting a default there would scan a folder the user had just declined, and a
            // Jellyfin-only library would fill up with films from this disk.
            if (config.WatchFolders.Length == 0 && !config.SetupCompleted)
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

            config.IsResolved = true;
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
