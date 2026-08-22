using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UrDatabase.Services
{
    /// <summary>
    /// Heuristic link between a catalogued movie and a file on disk. Extracted from the main
    /// window so the matching rules can be tested, and so comparisons stay ordinal — the app
    /// runs on both case-insensitive (NTFS, APFS) and case-sensitive (HFSX, ext4) filesystems.
    /// </summary>
    public static class MovieFileMatcher
    {
        public static string? FindBestMatch(IEnumerable<string> filePaths, string? title)
        {
            if (filePaths is null || string.IsNullOrWhiteSpace(title)) return null;

            var needle = title.Trim();
            string? looseMatch = null;

            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(name)) continue;

                if (string.Equals(name, needle, StringComparison.OrdinalIgnoreCase))
                    return path; // exact stem beats everything

                if (looseMatch is null && name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    looseMatch = path;
            }

            return looseMatch;
        }
    }
}
