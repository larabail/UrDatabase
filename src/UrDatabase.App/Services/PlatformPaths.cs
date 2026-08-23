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

        /// <summary>Environment variable consulted when the config file has no UrActor key.</summary>
        public const string UrActorApiKeyVariable = "URDATABASE_URACTOR_API_KEY";

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

        /// <summary>
        /// Where the whole install lives, for anyone who needs it somewhere other than the
        /// account's own application data: the catalogue, the poster cache, the logs and
        /// <c>appsettings.json</c> all hang off it.
        /// </summary>
        /// <remarks>
        /// It exists because until now there was no way to point the app anywhere at all, and that
        /// left every verification run — every "launch it once and see that it paints" — opening
        /// somebody's real library, with their catalogue and their credentials in it.
        ///
        /// The obvious precaution does not work, which is the whole reason a variable is needed
        /// rather than a note telling people to be careful:
        ///
        /// <code>
        /// Environment.SetEnvironmentVariable("HOME", tempDir);   // does NOT redirect this
        /// Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        /// </code>
        ///
        /// On Linux <c>GetFolderPath</c> honours <c>HOME</c> and <c>XDG_DATA_HOME</c>, so a
        /// harness that sets them is isolated. On macOS it does not: .NET asks Foundation,
        /// Foundation asks the operating system, and the answer is the real account's Application
        /// Support whatever the environment says. So a harness written and checked on one platform
        /// quietly writes to the live install on the other, which has already cost a maintainer
        /// their API keys and their Jellyfin password — see AGENTS.md.
        /// </remarks>
        public const string AppDataVariable = "URDATABASE_DATA_DIR";

        /// <summary>
        /// The install directory: <see cref="AppDataVariable"/> when it names one, and the
        /// account's own application data otherwise.
        /// </summary>
        /// <remarks>
        /// Expanded through <see cref="Expand"/> like every other configured path, so
        /// <c>~/scratch</c> and <c>%LOCALAPPDATA%\scratch</c> both work, and then resolved to an
        /// absolute path — which is a deliberate difference from the settings in the config file.
        /// They are read once, by a process whose working directory is wherever it was launched
        /// from; this one is read by a harness that may well launch the app from somewhere else,
        /// and an install directory that means a different place depending on how the app was
        /// started is exactly the ambiguity this variable exists to remove. A macOS bundle starts
        /// with its working directory at <c>/</c>.
        ///
        /// A value the operating system will not resolve at all is kept as it was rather than
        /// discarded. Falling back to the account's own application data would be the one failure
        /// worth avoiding here: somebody who asked for a scratch install would silently get the
        /// real one, which is the accident this whole variable is for.
        ///
        /// A blank or whitespace value is ignored rather than honoured. An unset variable and one
        /// set to nothing are the same intention, and treating the second as a path would put the
        /// install at the filesystem root or at the working directory, neither of which anybody
        /// asked for.
        /// </remarks>
        public static string AppDataRoot
        {
            get
            {
                var configured = Expand(Environment.GetEnvironmentVariable(AppDataVariable));
                if (configured.Length > 0) return Resolve(configured);

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    AppFolderName);
            }
        }

        private static string Resolve(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

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
