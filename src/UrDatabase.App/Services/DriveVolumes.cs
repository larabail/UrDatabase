using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Works out which drives a library lives on, from the folders it is configured to scan and
    /// the files its catalogue already holds.
    ///
    /// Plain string work rather than <c>Path.GetPathRoot</c>, for two reasons. The answer has to
    /// be the same whichever platform runs the tests — <c>GetPathRoot</c> on Windows calls
    /// <c>/Volumes/Films/x.mkv</c> a path on the current drive. And the root of a path is not the
    /// drive it lives on everywhere: on macOS every external disk hangs off <c>/</c>, so asking
    /// whether <c>/</c> exists would report an unplugged disk as perfectly healthy.
    /// </summary>
    public static class DriveVolumes
    {
        /// <summary>
        /// Linux mount points of the form <c>/prefix/name</c>, longest first so <c>/run/media</c>
        /// wins over a hypothetical <c>/run</c>.
        /// </summary>
        private static readonly string[][] UnixMountParents =
        {
            new[] { "run", "media" },   // /run/media/<user>/<name>: udisks on most desktops
            new[] { "media" },          // /media/<user>/<name> or /media/<name>
            new[] { "mnt" },            // /mnt/<name>
            new[] { "Volumes" }         // /Volumes/<name>: every non-boot disk on macOS
        };

        /// <summary>
        /// The volume a path lives on: <c>D:\</c>, <c>\\server\share\</c>, <c>/Volumes/Films</c>,
        /// or <c>/</c> for anything else rooted on a Unix filesystem. Null for a blank or relative
        /// path, which names no drive at all.
        /// </summary>
        public static string? VolumeOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim();

            // UNC, in either slash direction: the share is the unit that comes and goes.
            if (p.Length > 2 && IsSeparator(p[0]) && IsSeparator(p[1]))
            {
                var parts = Split(p);
                if (parts.Length < 2) return null;
                return $@"\\{parts[0]}\{parts[1]}\";
            }

            if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
                return char.ToUpperInvariant(p[0]) + @":\";

            if (p[0] != '/') return null;

            var segments = Split(p);

            foreach (var parent in UnixMountParents)
            {
                if (segments.Length <= parent.Length) continue;
                if (!parent.Select((s, i) => string.Equals(segments[i], s, StringComparison.Ordinal)).All(x => x)) continue;

                // /media and /run/media put a user directory between the parent and the disk, but
                // only when the path goes deep enough to have one; a bare /media/<name> is a disk.
                var hasUserLevel = parent[^1] == "media" && segments.Length > parent.Length + 1;
                var depth = parent.Length + (hasUserLevel ? 2 : 1);

                return "/" + string.Join('/', segments.Take(depth));
            }

            return "/";
        }

        /// <summary>
        /// Every drive the library touches, with what is on each. A drive appears when a watch
        /// folder is on it or a catalogued file still present at the last scan is on it; a file a
        /// scan has already recorded as gone does not keep a long-retired disk on the list.
        /// </summary>
        /// <param name="watchFolders">The configured library folders.</param>
        /// <param name="files">Catalogued file paths and the film each belongs to, if any.</param>
        public static IReadOnlyList<DriveUsage> Collect(
            IEnumerable<string>? watchFolders,
            IEnumerable<(string Path, long? MovieId)>? files)
        {
            var folders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var films = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in watchFolders ?? Array.Empty<string>())
            {
                var root = VolumeOf(folder);
                if (root is null) continue;

                if (!folders.TryGetValue(root, out var list)) folders[root] = list = new List<string>();
                if (!list.Contains(folder, StringComparer.OrdinalIgnoreCase)) list.Add(folder);
            }

            foreach (var (path, movieId) in files ?? Array.Empty<(string, long?)>())
            {
                // The file's folder rather than the file: on Linux the depth of a mount point is a
                // guess, and a guess that lands on the file itself would be probed as a directory
                // and reported missing while the disk sat there mounted.
                var root = VolumeOf(DirectoryOf(path));
                if (root is null) continue;

                if (!films.TryGetValue(root, out var set)) films[root] = set = new HashSet<long>();
                if (movieId is long id) set.Add(id);
            }

            return folders.Keys
                .Union(films.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .Select(root => new DriveUsage(
                    root,
                    folders.TryGetValue(root, out var f) ? f : (IReadOnlyList<string>)Array.Empty<string>(),
                    films.TryGetValue(root, out var s) ? s.Count : 0))
                .ToList();
        }

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static string? DirectoryOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var trimmed = path.TrimEnd('/', '\\');
            var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            return cut <= 0 ? trimmed : trimmed[..(cut + 1)];
        }

        private static string[] Split(string path) =>
            path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
