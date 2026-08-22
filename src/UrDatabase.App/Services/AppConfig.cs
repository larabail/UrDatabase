using System;
using System.Collections.Generic;
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

        /// <summary>File a user edits to configure their own install.</summary>
        public const string FileName = "appsettings.json";

        /// <summary>Tracked template, shipped next to the binary as the fallback.</summary>
        public const string ExampleFileName = "appsettings.example.json";

        /// <summary>
        /// Where a user's own settings belong: beside their database, poster cache and logs, in a
        /// directory they can write to. An installed macOS app lives in a signed bundle, so a file
        /// written next to the executable both breaks the code signature — Gatekeeper then refuses
        /// to launch it — and is thrown away by the next update.
        /// </summary>
        public static string UserConfigPath => Path.Combine(PlatformPaths.AppDataRoot, FileName);

        /// <summary>
        /// Which file this instance was actually read from, or null when nothing was found and the
        /// defaults are in force. Only for showing a person where to edit and for the log.
        /// </summary>
        [JsonIgnore]
        public string? SourcePath { get; set; }

        /// <summary>
        /// Loads configuration from the first location that has it, then layers the environment
        /// variables on top. Never throws: a missing or malformed file yields a usable config so
        /// the app still starts.
        /// </summary>
        public static AppConfig Load(string? path = null) =>
            Load(path, PlatformPaths.AppDataRoot, AppContext.BaseDirectory);

        /// <summary>
        /// The testable form. <paramref name="appDataRoot"/> and <paramref name="baseDirectory"/>
        /// are the per-user directory and the directory holding the executable; a test supplies
        /// temporary ones so it can prove the precedence without touching a real install.
        /// </summary>
        internal static AppConfig Load(string? path, string? appDataRoot, string? baseDirectory)
        {
            // Only when resolving by convention. An explicit path means the caller knows exactly
            // which file it wants and would not thank us for creating a different one.
            if (string.IsNullOrWhiteSpace(path)) EnsureUserConfig(appDataRoot, baseDirectory);

            var (config, source) = ReadFirst(CandidatePaths(path, appDataRoot, baseDirectory));

            config ??= new AppConfig();
            config.SourcePath = source;
            Normalize(config);
            return config;
        }

        private static (AppConfig? Config, string? Source) ReadFirst(IReadOnlyList<string> candidates)
        {
            foreach (var candidate in candidates)
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
                    if (parsed is not null) return (parsed, candidate);
                }
                catch
                {
                    // Malformed file: try the next candidate rather than failing startup.
                }
            }

            return (null, null);
        }

        /// <summary>
        /// Every place configuration may live, most specific first:
        /// <list type="number">
        ///   <item><description>a path the caller named outright;</description></item>
        ///   <item><description>the user's own file, in the per-user data directory;</description></item>
        ///   <item><description><c>appsettings.json</c> beside the executable, which is a build
        ///     tree when running from source;</description></item>
        ///   <item><description>the shipped <c>appsettings.example.json</c>.</description></item>
        /// </list>
        ///
        /// With one exception. A per-user file that is still a byte-for-byte copy of the template
        /// records no decision anybody made, so it drops below a file written beside the
        /// executable. Without that, a developer who ran the app once — seeding the copy — and
        /// then wrote <c>src/UrDatabase.App/appsettings.json</c> would find it silently ignored,
        /// which is the worst kind of bug to look for. Edit the per-user file at all and it wins
        /// again, everywhere.
        /// </summary>
        internal static string[] CandidatePaths(string? path, string? appDataRoot, string? baseDirectory)
        {
            if (!string.IsNullOrWhiteSpace(path)) return new[] { path };

            var user = string.IsNullOrWhiteSpace(appDataRoot) ? null : Path.Combine(appDataRoot, FileName);
            var beside = string.IsNullOrWhiteSpace(baseDirectory) ? null : Path.Combine(baseDirectory, FileName);
            var example = string.IsNullOrWhiteSpace(baseDirectory) ? null : Path.Combine(baseDirectory, ExampleFileName);

            var candidates = new List<string>(3);

            if (user is not null && beside is not null && File.Exists(beside) && IsUntouchedTemplate(user, example))
            {
                candidates.Add(beside);
                candidates.Add(user);
            }
            else
            {
                if (user is not null) candidates.Add(user);
                if (beside is not null) candidates.Add(beside);
            }

            if (example is not null) candidates.Add(example);

            return candidates.ToArray();
        }

        /// <summary>
        /// True when the per-user file is exactly what the app itself put there and nobody has
        /// touched it since — either a copy of the shipped example or the generated blank.
        /// </summary>
        private static bool IsUntouchedTemplate(string userConfig, string? example)
        {
            try
            {
                if (!File.Exists(userConfig)) return false;

                var actual = Flatten(File.ReadAllText(userConfig));

                if (example is not null && File.Exists(example) &&
                    string.Equals(actual, Flatten(File.ReadAllText(example)), StringComparison.Ordinal))
                    return true;

                return string.Equals(actual, Flatten(BlankTemplateJson()), StringComparison.Ordinal);
            }
            catch
            {
                // Unreadable is not untouched: leave the normal order in place.
                return false;
            }
        }

        /// <summary>Line endings differ between the platform that shipped a file and the one reading it.</summary>
        private static string Flatten(string text) => text.Replace("\r\n", "\n").Trim();

        /// <summary>
        /// Puts a real file in front of a first-time user, copied from the shipped example, so
        /// configuring the app is editing something that exists rather than creating a file in a
        /// directory they have been told about. Returns the path when one was written.
        ///
        /// Writes only into the per-user directory: <see cref="AppContext.BaseDirectory"/> may be
        /// inside a signed application bundle and must never be touched. Best effort throughout —
        /// a read-only home directory means no seed file, not a failed start.
        /// </summary>
        internal static string? EnsureUserConfig(string? appDataRoot, string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(appDataRoot)) return null;

            try
            {
                var target = Path.Combine(appDataRoot, FileName);
                if (File.Exists(target)) return null;

                // A build tree with its own appsettings.json belongs to somebody working on the
                // app, who does not need a second file appearing elsewhere. (Should one already
                // exist, CandidatePaths keeps an untouched copy from shadowing theirs.)
                if (!string.IsNullOrWhiteSpace(baseDirectory) &&
                    File.Exists(Path.Combine(baseDirectory, FileName)))
                    return null;

                Directory.CreateDirectory(appDataRoot);

                var example = string.IsNullOrWhiteSpace(baseDirectory)
                    ? null
                    : Path.Combine(baseDirectory, ExampleFileName);

                if (example is not null && File.Exists(example)) File.Copy(example, target);
                else File.WriteAllText(target, BlankTemplateJson());

                return target;
            }
            catch
            {
                // No writable home, a sandbox, a full disk: the app runs on defaults regardless.
                return null;
            }
        }

        /// <summary>
        /// The fallback seed, for the odd build with no example beside it. Serialised from the
        /// model so it cannot drift out of step with the settings that actually exist.
        /// </summary>
        private static string BlankTemplateJson() =>
            JsonSerializer.Serialize(
                new AppConfig { DatabasePath = "", PosterCacheDir = "", WatchFolders = Array.Empty<string>() },
                new JsonSerializerOptions { WriteIndented = true });

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
