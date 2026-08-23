using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace UrDatabase.Services
{
    /// <summary>
    /// How far a download has got. Total is null until the server says how big the file is, which
    /// it usually does but is not obliged to, so every consumer has to cope with not knowing.
    /// </summary>
    public readonly record struct JellyfinDownloadProgress(long BytesRead, long? TotalBytes)
    {
        /// <summary>Null when the size is unknown, rather than a made-up 0% that never moves.</summary>
        public double? Fraction =>
            TotalBytes is > 0 ? Math.Clamp((double)BytesRead / TotalBytes.Value, 0d, 1d) : null;

        /// <summary>
        /// A line for the status area. Deliberately short: it is rewritten several times a second
        /// and sits next to the film's title.
        /// </summary>
        public string Describe() => TotalBytes is > 0
            ? $"{JellyfinDownload.DescribeSize(BytesRead)} of {JellyfinDownload.DescribeSize(TotalBytes.Value)} ({Fraction!.Value * 100:0}%)"
            : JellyfinDownload.DescribeSize(BytesRead);
    }

    /// <summary>
    /// Naming and sizing rules for a downloaded film. Pure, and separate from the transfer itself,
    /// because the awkward parts of "save this film to disk" are all decisions rather than I/O:
    /// which characters a filename may contain on the fussiest platform the app runs on, and what
    /// to call a file when the server declines to say.
    ///
    /// The target name matters beyond tidiness. A finished download is registered in the catalogue
    /// through the same filename parser a scan uses, so <c>Title (Year).ext</c> is not decoration —
    /// it is what makes the downloaded copy land on the film it belongs to instead of arriving as
    /// a second, oddly named entry.
    /// </summary>
    public static class JellyfinDownload
    {
        /// <summary>
        /// Used when the server sends no filename and no usable container. Matroska because that
        /// is what Jellyfin direct-plays most often, and because guessing wrong here costs only a
        /// misnamed file that still opens: players read the container, not the extension.
        /// </summary>
        public const string DefaultExtension = ".mkv";

        /// <summary>
        /// What a partial transfer is called while it is still running. A film is large enough
        /// that a download interrupted by a closed laptop is ordinary rather than exceptional, and
        /// the suffix is what keeps a half file from being played, scanned or counted as the
        /// finished article.
        /// </summary>
        public const string PartialExtension = ".part";

        /// <summary>
        /// Longest filename stem produced, in characters. Comfortably inside the 255 bytes that
        /// ext4, APFS and NTFS each allow, with room for the extension, the <c>.part</c> suffix and
        /// the multi-byte characters a title may be made of.
        /// </summary>
        public const int MaxStemLength = 160;

        /// <summary>
        /// Characters no filename may contain. The Windows set, applied on every platform: a
        /// library is routinely a shared drive or a synced folder, and a name that is legal only
        /// on the machine that created it fails later, somewhere else, for somebody else.
        /// </summary>
        private static readonly char[] Illegal = "<>:\"/\\|?*".ToCharArray();

        /// <summary>
        /// Device names MS-DOS reserved, which Windows still refuses as filenames whatever the
        /// extension. "Nul (2019).mkv" is a real film title away from being unwritable.
        /// </summary>
        private static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Folds a film's title into something every filesystem will accept, preserving as much of
        /// it as possible: an illegal character becomes a space rather than vanishing, so
        /// "Face/Off" reads as "Face Off" instead of "FaceOff".
        /// </summary>
        public static string SanitizeStem(string? title)
        {
            var builder = new StringBuilder((title ?? "").Length);

            foreach (var ch in title ?? "")
            {
                if (char.IsControl(ch)) continue;
                builder.Append(Illegal.Contains(ch) ? ' ' : ch);
            }

            var cleaned = CollapseWhitespace(builder.ToString());

            if (cleaned.Length > MaxStemLength)
                cleaned = CollapseWhitespace(cleaned[..MaxStemLength]);

            // Windows drops a trailing dot or space silently, so a name ending in one resolves to
            // a different file than the one just written — and the check for "already downloaded"
            // would never match it again.
            cleaned = cleaned.TrimEnd('.', ' ');

            if (cleaned.Length == 0) return "Untitled";

            return IsReserved(cleaned) ? "_" + cleaned : cleaned;
        }

        private static bool IsReserved(string stem)
        {
            var upToDot = stem.Split('.')[0];
            return Reserved.Any(name => string.Equals(name, upToDot, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The name a downloaded film is saved under: <c>Title (Year).ext</c>, matching the
        /// convention the filename parser reads and the one Jellyfin's own libraries use.
        /// A film with no year simply has none, which the parser also understands.
        /// </summary>
        public static string BuildFileName(string? title, int? year, string? extension)
        {
            var stem = SanitizeStem(title);
            var suffix = NormalizeExtension(extension);

            return year.HasValue
                ? $"{stem} ({year.Value.ToString(CultureInfo.InvariantCulture)}){suffix}"
                : $"{stem}{suffix}";
        }

        /// <summary>
        /// Full path for a download. The folder is expanded first, so a configured
        /// <c>~/Movies/UrDatabase</c> or <c>%USERPROFILE%\Videos</c> resolves the same way every
        /// other configured path in the app does.
        /// </summary>
        public static string BuildPath(string? folder, string? title, int? year, string? extension)
        {
            var directory = PlatformPaths.Expand(folder);
            if (string.IsNullOrWhiteSpace(directory)) directory = PlatformPaths.DefaultDownloadFolder;

            return Path.Combine(directory, BuildFileName(title, year, extension));
        }

        /// <summary>Where the bytes go while the transfer is running.</summary>
        public static string PartialPathFor(string path) => path + PartialExtension;

        /// <summary>
        /// The already-downloaded copy of a film, or null. Matched on the stem rather than on a
        /// full name because the extension depends on what the server turned out to be holding,
        /// which is not known until a transfer has started — so "is this already here?" cannot be
        /// asked as a single <see cref="File.Exists"/>.
        ///
        /// A <c>.part</c> file is deliberately not a match: it is the film arriving, not the film.
        /// </summary>
        public static string? FindExisting(string? folder, string? title, int? year)
        {
            var directory = PlatformPaths.Expand(folder);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

            var stem = year.HasValue
                ? $"{SanitizeStem(title)} ({year.Value.ToString(CultureInfo.InvariantCulture)})"
                : SanitizeStem(title);

            try
            {
                return Directory
                    .EnumerateFiles(directory, stem + ".*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file =>
                        !file.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            Path.GetFileNameWithoutExtension(file),
                            stem,
                            StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                // An unreadable folder means "not downloaded", which is the safe answer: the worst
                // it costs is offering a download that turns out to be unnecessary.
                return null;
            }
        }

        /// <summary>
        /// Cleans up an extension from any source: with or without a leading dot, upper case, or
        /// carrying the rest of a filename. Anything that does not look like an extension — too
        /// long, or containing something an extension never does — is rejected in favour of the
        /// default rather than trusted onto the filesystem.
        /// </summary>
        public static string NormalizeExtension(string? extension)
        {
            var value = (extension ?? "").Trim();
            if (value.Length == 0) return DefaultExtension;

            var lastDot = value.LastIndexOf('.');
            if (lastDot >= 0) value = value[(lastDot + 1)..];

            value = value.Trim();

            if (value.Length == 0 || value.Length > 5) return DefaultExtension;
            if (!value.All(char.IsAsciiLetterOrDigit)) return DefaultExtension;

            return "." + value.ToLowerInvariant();
        }

        /// <summary>
        /// The extension to save under, given what the server said. Jellyfin normally sends a
        /// <c>Content-Disposition</c> naming the original file, which carries the real container;
        /// the item's own container field is the fallback for a server that does not.
        ///
        /// Only the extension is taken from the server, never the whole name. The remote filename
        /// is attacker-controlled as far as this app is concerned — it may hold path separators,
        /// <c>..</c>, or a name that escapes the download folder entirely — and the local name is
        /// built from the catalogue's own title instead.
        /// </summary>
        public static string ResolveExtension(string? contentDispositionFileName, string? container)
        {
            var fromServer = ExtractExtension(contentDispositionFileName);
            if (fromServer is not null) return fromServer;

            var fromContainer = NormalizeExtension(container);
            return fromContainer;
        }

        private static string? ExtractExtension(string? fileName)
        {
            var value = (fileName ?? "").Trim().Trim('"');
            if (value.Length == 0) return null;

            // Take the last segment however it was separated, so a server that sends a whole path
            // cannot smuggle a directory into the name.
            var lastSeparator = value.LastIndexOfAny(new[] { '/', '\\' });
            if (lastSeparator >= 0) value = value[(lastSeparator + 1)..];

            var dot = value.LastIndexOf('.');
            if (dot < 0 || dot == value.Length - 1) return null;

            var normalized = NormalizeExtension(value[dot..]);
            return normalized == DefaultExtension && !value.EndsWith(DefaultExtension, StringComparison.OrdinalIgnoreCase)
                ? null
                : normalized;
        }

        /// <summary>
        /// Bytes as a person reads them. Kept as a name on this class because the download screen
        /// and its tests call it here; the rule itself moved to <see cref="ByteSize"/> once the
        /// update check needed the same one, and a second copy would have been a second copy.
        /// </summary>
        public static string DescribeSize(long bytes) => ByteSize.Describe(bytes);

        private static string CollapseWhitespace(string text)
        {
            var builder = new StringBuilder(text.Length);
            var pendingSpace = false;

            foreach (var ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace) builder.Append(' ');
                pendingSpace = false;
                builder.Append(ch);
            }

            return builder.ToString();
        }
    }
}
