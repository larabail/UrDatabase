using System;
using System.Collections.Generic;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>
    /// Whether a catalogued path falls inside a folder the scan actually walked.
    ///
    /// This is the question that decides whether a row may be marked missing, and getting it
    /// wrong in either direction is expensive. Too narrow and a deleted film is never noticed;
    /// too wide and a scan of one folder marks a second folder's films missing because they were
    /// not in the first. It is also the reason an unplugged drive is survivable: its root is not
    /// among the walked ones, so nothing under it is even considered.
    /// </summary>
    public static class PathScope
    {
        /// <summary>
        /// How two paths are compared, which is a property of the platform rather than of the
        /// path. Windows and the default macOS volume are case-insensitive, so <c>/Movies</c> and
        /// <c>/movies</c> name one folder and treating them as two would leave every row under it
        /// permanently unmatched. Linux is case-sensitive and treating two names as one there
        /// would be a wrong answer in the other direction.
        /// </summary>
        public static StringComparison Comparison =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        /// <summary><see cref="Comparison"/> as a comparer, for keying a dictionary of paths.</summary>
        public static StringComparer Comparer =>
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        /// <summary>
        /// A path in the one form this comparison uses: absolute, with the platform's separator
        /// and no trailing one. Relative segments are resolved because a watch folder can be
        /// configured as <c>~/Movies/../Movies</c> and the scan reports the resolved path, so the
        /// two have to meet somewhere.
        /// </summary>
        public static string Normalise(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                // A path the OS will not resolve at all — a stale row naming a device that no
                // longer exists, say. It is still a string that can be compared literally, and
                // refusing to answer would abort a scan over one bad row.
                full = path;
            }

            return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        /// <summary>
        /// True when <paramref name="path"/> is <paramref name="root"/> or lies beneath it.
        ///
        /// The separator test is what stops <c>/Films</c> claiming <c>/Films Backup</c>, which a
        /// plain <c>StartsWith</c> would hand it — and with it every row in a folder the scan
        /// never opened.
        /// </summary>
        public static bool IsUnder(string? root, string? path)
        {
            var normalisedRoot = Normalise(root);
            var normalisedPath = Normalise(path);

            if (normalisedRoot.Length == 0 || normalisedPath.Length == 0) return false;
            if (normalisedPath.Equals(normalisedRoot, Comparison)) return true;

            if (!normalisedPath.StartsWith(normalisedRoot, Comparison)) return false;

            var next = normalisedPath[normalisedRoot.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        /// <summary>True when <paramref name="path"/> is under any of <paramref name="roots"/>.</summary>
        public static bool IsUnderAny(IEnumerable<string> roots, string? path)
        {
            if (roots is null) return false;

            foreach (var root in roots)
                if (IsUnder(root, path)) return true;

            return false;
        }
    }
}
