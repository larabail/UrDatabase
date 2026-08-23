using System;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>
    /// Every filesystem location the app needs, resolved per platform.
    /// Nothing here may assume Windows drive letters or <c>%APPDATA%</c>.
    /// </summary>
    public static class PlatformPaths
    {
        public const string AppFolderName = "UrDatabase";

        /// <summary>Environment variable consulted when the config file has no TMDB key.</summary>
        public const string TmdbApiKeyVariable = "URDATABASE_TMDB_API_KEY";

        /// <summary>Environment variable consulted when the config file has no OMDb key.</summary>
        public const string OmdbApiKeyVariable = "URDATABASE_OMDB_API_KEY";

        /// <summary>
        /// Jellyfin connection settings, for anyone who would rather not write a server address
        /// or a password into a file. Consulted only when the matching config field is blank.
        /// </summary>
        public const string JellyfinUrlVariable = "URDATABASE_JELLYFIN_URL";
        public const string JellyfinUsernameVariable = "URDATABASE_JELLYFIN_USERNAME";
        public const string JellyfinPasswordVariable = "URDATABASE_JELLYFIN_PASSWORD";
        public const string JellyfinApiKeyVariable = "URDATABASE_JELLYFIN_API_KEY";

        public static string AppDataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

        public static string DefaultDatabasePath => Path.Combine(AppDataRoot, "movies.db");

        public static string DefaultPosterCacheDir => Path.Combine(AppDataRoot, "posters");

        public static string LogDirectory => Path.Combine(AppDataRoot, "logs");

        /// <summary>
        /// Where a user's movies most likely live: <c>~/Movies</c> on macOS, the Videos
        /// known folder on Windows, <c>~/Videos</c> elsewhere.
        /// </summary>
        public static string DefaultWatchFolder
        {
            get
            {
                if (OperatingSystem.IsMacOS())
                    return Path.Combine(HomeDirectory, "Movies");

                var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                return string.IsNullOrWhiteSpace(videos) ? Path.Combine(HomeDirectory, "Videos") : videos;
            }
        }

        /// <summary>
        /// Where a film downloaded from Jellyfin lands: a subfolder of the platform's film folder.
        ///
        /// Inside the folder the app would scan anyway, deliberately. A download is registered in
        /// the catalogue the moment it finishes, but putting it somewhere a scan can also find it
        /// means the two agree — a user who deletes their database and rescans keeps their
        /// downloads, and one who moves a file out of here loses nothing. Its own subfolder rather
        /// than the root so that clearing out what the app fetched never means picking through
        /// films the user put there themselves.
        /// </summary>
        public static string DefaultDownloadFolder => Path.Combine(DefaultWatchFolder, AppFolderName);

        public static string HomeDirectory
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home)) return home;
                return Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory();
            }
        }

        /// <summary>
        /// Resolves a configured path. Handles the Windows-style tokens older installs wrote
        /// into <c>appsettings.json</c> (<c>%APPDATA%</c>, <c>%LOCALAPPDATA%</c>,
        /// <c>%USERPROFILE%</c>), a leading <c>~</c>, and mixed directory separators, so a
        /// config file written on Windows still resolves on macOS.
        /// </summary>
        public static string Expand(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var value = input.Trim();
            var hadWindowsToken = value.Contains('%') || value.StartsWith('~');

            value = ReplaceToken(value, "%APPDATA%", () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            value = ReplaceToken(value, "%LOCALAPPDATA%", () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            value = ReplaceToken(value, "%USERPROFILE%", () => HomeDirectory);
            value = ReplaceToken(value, "%HOME%", () => HomeDirectory);

            // Anything still wrapped in % may be a genuine environment variable.
            if (value.Contains('%')) value = Environment.ExpandEnvironmentVariables(value);

            if (value.StartsWith('~') && (value.Length == 1 || value[1] == '/' || value[1] == '\\'))
                value = HomeDirectory + value[1..];

            // A Windows-authored value carries backslashes that mean nothing on Unix.
            if (hadWindowsToken && Path.DirectorySeparatorChar != '\\')
                value = value.Replace('\\', Path.DirectorySeparatorChar);

            return value;
        }

        private static string ReplaceToken(string value, string token, Func<string> resolve)
        {
            if (value.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return value;

            // Prefer a real environment variable when the OS actually defines one.
            var name = token.Trim('%');
            var fromEnvironment = Environment.GetEnvironmentVariable(name);
            var replacement = string.IsNullOrWhiteSpace(fromEnvironment) ? resolve() : fromEnvironment;

            return value.Replace(token, replacement.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
