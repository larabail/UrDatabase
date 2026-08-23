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

        /// <summary>
        /// The SFTP account films are uploaded through, which is a different machine account from
        /// the Jellyfin login above and deserves to be settable without touching a file. The key
        /// variable holds a path, never key material: a private key belongs in a file with its own
        /// permissions, not in an environment every child process inherits.
        /// </summary>
        public const string JellyfinSftpHostVariable = "URDATABASE_JELLYFIN_SFTP_HOST";
        public const string JellyfinSftpPortVariable = "URDATABASE_JELLYFIN_SFTP_PORT";
        public const string JellyfinSftpUsernameVariable = "URDATABASE_JELLYFIN_SFTP_USERNAME";
        public const string JellyfinSftpKeyVariable = "URDATABASE_JELLYFIN_SFTP_KEY";
        public const string JellyfinSftpPassphraseVariable = "URDATABASE_JELLYFIN_SFTP_PASSPHRASE";
        public const string JellyfinSftpMoviesPathVariable = "URDATABASE_JELLYFIN_SFTP_MOVIES_PATH";

        public static string AppDataRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

        public static string DefaultDatabasePath => Path.Combine(AppDataRoot, "movies.db");

        public static string DefaultPosterCacheDir => Path.Combine(AppDataRoot, "posters");

        public static string LogDirectory => Path.Combine(AppDataRoot, "logs");

        /// <summary>
        /// Where a downloaded release lands.
        ///
        /// The user's own downloads folder, because that is what "download" means to everybody
        /// else on the machine, it is where they will look for the file again next week, and it is
        /// somewhere they already clear out. An eighty megabyte archive per release accumulating
        /// unseen inside the app's data directory, which nothing in the app ever lists or empties,
        /// would be the app quietly filling a disk.
        ///
        /// The folder is not guaranteed to exist — it can be renamed, and a service account may
        /// never have had one — so the app's own directory is the fallback. The name is the one on
        /// disk rather than the one Finder or Explorer displays: both localise it for the user
        /// while leaving it as <c>Downloads</c> in the filesystem.
        /// </summary>
        public static string DefaultUpdateFolder => ResolveUpdateFolder(HomeDirectory, AppDataRoot, Directory.Exists);

        /// <summary>The testable form, so the fallback can be asserted on a machine that has the folder.</summary>
        internal static string ResolveUpdateFolder(string home, string appDataRoot, Func<string, bool> directoryExists)
        {
            var downloads = Path.Combine(home, "Downloads");
            return directoryExists(downloads) ? downloads : Path.Combine(appDataRoot, "updates");
        }

        /// <summary>
        /// OpenSSH's record of which host keys belong to which machines, which is what says
        /// whether the server an upload is about to go to is the one it went to last time.
        ///
        /// Deliberately the same file every other SSH tool on the machine uses, rather than a
        /// private copy: the entry is usually already there from connecting by hand once, and a
        /// second list would go stale without anybody noticing. Built from
        /// <see cref="HomeDirectory"/> rather than from a literal <c>~</c>, which resolves to
        /// nothing on Windows — where this app also runs, and where OpenSSH keeps the file in the
        /// same place under the user profile.
        /// </summary>
        public static string KnownHostsPath => Path.Combine(HomeDirectory, ".ssh", "known_hosts");

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
