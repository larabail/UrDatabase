using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UrDatabase.Services
{
    /// <summary>
    /// Where <c>appsettings.json</c> is read from and written to.
    ///
    /// Reading has always looked next to the binary, and it still does first, so an install that
    /// was configured by hand keeps behaving exactly as it did. The user's application data
    /// directory is a second candidate for the one case that file cannot cover: an app dropped
    /// somewhere the user may not write to, where the setup screen would otherwise have nowhere
    /// to put an answer. It is deliberately the lower priority of the two — a person who edited
    /// the documented file should never be quietly overruled by one they have never seen.
    /// </summary>
    public static class ConfigStore
    {
        /// <summary>The documented location: alongside the executable.</summary>
        public static string PortablePath => Path.Combine(AppContext.BaseDirectory, AppConfig.FileName);

        /// <summary>The fallback, used when the install directory is read-only.</summary>
        public static string UserPath => Path.Combine(PlatformPaths.AppDataRoot, AppConfig.FileName);

        /// <summary>The tracked template, which is never written to.</summary>
        public static string ExamplePath => Path.Combine(AppContext.BaseDirectory, AppConfig.ExampleFileName);

        /// <summary>Every file <see cref="AppConfig.Load"/> will try, in order.</summary>
        public static IReadOnlyList<string> ReadOrder => new[] { PortablePath, UserPath, ExamplePath };

        /// <summary>
        /// The configuration file this install actually has, or null when it has never been
        /// configured. The shipped example does not count: it is the same on every machine and
        /// says nothing about what this user chose.
        /// </summary>
        public static string? ExistingPath =>
            new[] { PortablePath, UserPath }.FirstOrDefault(SafeExists);

        /// <summary>True once the user has a configuration file of their own.</summary>
        public static bool IsConfigured => ExistingPath is not null;

        /// <summary>
        /// Where a save would land. Prefers the file that already exists, so saving twice never
        /// leaves two configurations behind with only one of them being read.
        /// </summary>
        public static string SavePath =>
            ChooseSavePath(PortablePath, UserPath, SafeExists, DirectoryAcceptsWrites);

        internal static string ChooseSavePath(
            string portablePath,
            string userPath,
            Func<string, bool> fileExists,
            Func<string, bool> directoryAcceptsWrites)
        {
            var portableDirectory = Path.GetDirectoryName(portablePath) ?? "";

            if (fileExists(portablePath) && directoryAcceptsWrites(portableDirectory)) return portablePath;
            if (fileExists(userPath)) return userPath;
            if (directoryAcceptsWrites(portableDirectory)) return portablePath;

            return userPath;
        }

        /// <summary>
        /// Writes the configuration and returns the file it went to. Pass a <paramref name="path"/>
        /// only in tests; leaving it null lets <see cref="SavePath"/> decide, and falls back to the
        /// user's application data directory if that choice turns out to be unwritable after all —
        /// permissions can say yes and a full or read-only disk still say no.
        /// </summary>
        public static string Save(AppConfig config, string? path = null)
        {
            if (config is null) throw new ArgumentNullException(nameof(config));

            // A resolved config carries values that were never in a file: a key from the
            // environment, or the one compiled into an official build. Writing it back would copy
            // those onto the user's disk under their own name, which is how a shipped key ends up
            // somewhere nobody thinks to rotate it.
            if (config.IsResolved)
                throw new InvalidOperationException(
                    "Refusing to save a resolved configuration. Edit the one from AppConfig.ReadRaw instead.");

            var target = path ?? SavePath;

            try
            {
                Write(target, config);
                return target;
            }
            catch (Exception ex) when (path is null && !PathsMatch(target, UserPath))
            {
                AppLog.Write("startup.log", $"could not write {target}: {ex.Message}");
                Write(UserPath, config);
                return UserPath;
            }
        }

        private static void Write(string path, AppConfig config)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, Serialize(config));
        }

        /// <summary>
        /// The file as it is written out. An explicit shape rather than the whole object, for two
        /// reasons: it keeps the saved file looking like <c>appsettings.example.json</c>, and it
        /// means a property added to <see cref="AppConfig"/> for internal use cannot silently
        /// start appearing in the user's configuration.
        ///
        /// A path that merely matches this platform's default is written as blank, so the file
        /// stays as portable as it was and a moved application data directory still resolves.
        /// </summary>
        internal static string Serialize(AppConfig config)
        {
            var jellyfin = config.Jellyfin ?? new JellyfinSettings();

            var document = new
            {
                DatabasePath = BlankWhenDefault(config.DatabasePath, PlatformPaths.DefaultDatabasePath),
                WatchFolders = config.WatchFolders ?? Array.Empty<string>(),
                TmdbApiKey = config.TmdbApiKey ?? "",
                OmdbApiKey = config.OmdbApiKey ?? "",
                PosterCacheDir = BlankWhenDefault(config.PosterCacheDir, PlatformPaths.DefaultPosterCacheDir),
                config.DownloadPosters,
                TmdbImageSize = config.TmdbImageSize ?? "",
                config.SetupCompleted,
                Jellyfin = new
                {
                    jellyfin.ServerUrl,
                    jellyfin.Username,
                    jellyfin.Password,
                    jellyfin.ApiKey,
                    jellyfin.LibraryName
                }
            };

            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string BlankWhenDefault(string? value, string platformDefault) =>
            string.IsNullOrWhiteSpace(value) || string.Equals(value, platformDefault, StringComparison.Ordinal)
                ? ""
                : value;

        /// <summary>
        /// Whether a directory can be written to, answered by trying rather than by reading
        /// permissions: a macOS app in a quarantined folder and a Windows install under
        /// Program Files both report plausible permissions and then refuse the write.
        /// </summary>
        internal static bool DirectoryAcceptsWrites(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;

            var probe = Path.Combine(directory, $".urdatabase-write-test-{Guid.NewGuid():N}");

            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(probe, "");
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(probe); } catch { }
            }
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }

        private static bool PathsMatch(string left, string right) =>
            string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
