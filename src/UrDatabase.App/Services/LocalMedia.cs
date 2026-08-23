using System;
using System.IO;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Describes the copy of a film on this disk: whatever its name claims, plus the one thing
    /// about it that is not a claim — how big it is.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="FilenameMediaInfo"/> because that one is pure and this one touches
    /// the filesystem. The seam is <paramref name="sizeOf"/>, so the rules here can be asserted on
    /// without a real file, in the same spirit as <c>TmdbService</c>'s handler parameter.
    /// </remarks>
    public static class LocalMedia
    {
        /// <summary>
        /// What is known about a local file, or null when its name says nothing and it cannot be
        /// measured. Null rather than an empty description, so the details screen shows no badges
        /// at all instead of an empty row.
        /// </summary>
        public static MediaInfo? Describe(string? path, Func<string, long?>? sizeOf = null)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var info = FilenameMediaInfo.Parse(path);
            info.SizeBytes = (sizeOf ?? SizeOnDisk)(path);

            return info.HasAnything ? info : null;
        }

        /// <summary>
        /// The size of a file, or null for anything that cannot be read. A missing file is the
        /// ordinary case here rather than an error: the catalogue outlives an unplugged drive
        /// deliberately, and a film whose disk is elsewhere still opens.
        /// </summary>
        private static long? SizeOnDisk(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists && file.Length > 0 ? file.Length : null;
            }
            catch (Exception ex)
            {
                AppLog.Write("app.log", $"could not measure {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }
    }
}
