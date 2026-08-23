using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UrDatabase.Services
{
    /// <summary>
    /// How far an upload has got. Total is known here in a way it never is for a download — the
    /// file is on this disk and its size can simply be read — but it stays nullable so the shape
    /// matches <see cref="JellyfinDownloadProgress"/> and a transport that cannot say is not a
    /// special case.
    /// </summary>
    public readonly record struct JellyfinUploadProgress(long BytesSent, long? TotalBytes)
    {
        /// <summary>Null when the size is unknown, rather than a made-up 0% that never moves.</summary>
        public double? Fraction =>
            TotalBytes is > 0 ? Math.Clamp((double)BytesSent / TotalBytes.Value, 0d, 1d) : null;

        /// <summary>
        /// A line for the status area. Deliberately short: it is rewritten several times a second
        /// and sits next to the film's title.
        /// </summary>
        public string Describe() => TotalBytes is > 0
            ? $"{JellyfinDownload.DescribeSize(BytesSent)} of {JellyfinDownload.DescribeSize(TotalBytes.Value)} ({Fraction!.Value * 100:0}%)"
            : JellyfinDownload.DescribeSize(BytesSent);
    }

    /// <summary>
    /// Where a film goes on the server, and what it is called when it gets there. Pure, and
    /// separate from the transfer, for the same reason <see cref="JellyfinDownload"/> is: the hard
    /// parts of "put this film on the server" are decisions rather than I/O.
    ///
    /// Two of those decisions have teeth.
    ///
    /// The first is the layout. Jellyfin identifies a film by the folder and file it finds, so
    /// <c>movies/Title (Year)/Title (Year).mkv</c> is not tidiness — it is the difference between
    /// the server showing the film and the server showing an unmatched entry named after whatever
    /// the local file happened to be called. It is also the layout an existing Jellyfin library
    /// already uses, so an uploaded film sits among the others rather than announcing itself.
    ///
    /// The second is the separator. These are remote paths, so every one of them uses a forward
    /// slash on every platform. <see cref="Path.Combine"/> would produce a backslash on Windows
    /// and SFTP would take it as part of the name, creating one file literally called
    /// <c>Title (Year)\Title (Year).mkv</c> inside the movies directory — which no scan would
    /// ever match and no listing would explain. Nothing in this file may use it.
    /// </summary>
    public static class JellyfinUpload
    {
        /// <summary>
        /// What a film is called on the server while it is still arriving. Not a video extension,
        /// so a library scan that runs mid-transfer walks past it instead of adding a film that
        /// is four minutes long and getting longer.
        /// </summary>
        public const string PartialExtension = ".uploading";

        /// <summary>The one separator a remote path may use, whatever platform built it.</summary>
        public const char RemoteSeparator = '/';

        /// <summary>
        /// The directory a film gets to itself, named the way Jellyfin's own libraries name one.
        /// The sanitiser is <see cref="JellyfinDownload.SanitizeStem"/> rather than a second copy
        /// of the same rules: a film that came down from the server as <c>Face Off (1997).mkv</c>
        /// must go back up into <c>Face Off (1997)/</c>, and two sanitisers would eventually
        /// disagree about that.
        /// </summary>
        public static string RemoteStem(string? title, int? year)
        {
            var stem = JellyfinDownload.SanitizeStem(title);

            return year.HasValue
                ? $"{stem} ({year.Value.ToString(CultureInfo.InvariantCulture)})"
                : stem;
        }

        /// <summary>
        /// The film's own directory on the server: <c>movies/Title (Year)</c>.
        /// </summary>
        public static string BuildRemoteFolder(string? moviesPath, string? title, int? year) =>
            JoinRemote(NormalizeRemoteRoot(moviesPath), RemoteStem(title, year));

        /// <summary>
        /// Where the bytes end up: <c>movies/Title (Year)/Title (Year).ext</c>.
        /// </summary>
        /// <param name="localPath">
        /// The file being uploaded. Only its extension is taken from it — the name comes from the
        /// catalogue, so a film linked to <c>arrival.2016.1080p.WEB-DL.mkv</c> arrives on the
        /// server as something Jellyfin can identify.
        /// </param>
        public static string BuildRemotePath(string? moviesPath, string? title, int? year, string? localPath)
        {
            var stem = RemoteStem(title, year);
            var extension = JellyfinDownload.NormalizeExtension(Path.GetExtension(localPath ?? ""));

            return JoinRemote(NormalizeRemoteRoot(moviesPath), stem, stem + extension);
        }

        /// <summary>Where the bytes go while the transfer is running.</summary>
        public static string PartialPathFor(string remotePath) => remotePath + PartialExtension;

        /// <summary>
        /// Joins remote path segments with forward slashes, collapsing the empty and doubled ones
        /// that come of building a path out of configuration. A leading slash on the first segment
        /// is kept: an account that is not chrooted needs <c>/tank/movies</c> and would be
        /// silently pointed at a relative <c>tank/movies</c> without it.
        /// </summary>
        public static string JoinRemote(params string?[] segments)
        {
            var builder = new StringBuilder();
            var absolute = segments.Length > 0 && (segments[0] ?? "").TrimStart().StartsWith(RemoteSeparator);

            foreach (var segment in segments)
            {
                var part = (segment ?? "").Replace('\\', RemoteSeparator).Trim(RemoteSeparator);
                if (part.Length == 0) continue;

                if (builder.Length > 0) builder.Append(RemoteSeparator);
                builder.Append(part);
            }

            return absolute ? RemoteSeparator + builder.ToString() : builder.ToString();
        }

        /// <summary>
        /// The configured movies directory in the one shape the rest of the code expects.
        /// Blank means <see cref="JellyfinSftpSettings.DefaultMoviesPath"/>, because the feature
        /// is only ever reached with a host and an account configured, and asking somebody for a
        /// path when the usual answer is "movies" is a question with a right answer.
        ///
        /// Backslashes are translated rather than rejected: a Windows user writes them out of
        /// habit, and the server they are typing about is not a Windows one.
        /// </summary>
        public static string NormalizeRemoteRoot(string? moviesPath)
        {
            var value = (moviesPath ?? "").Trim().Replace('\\', RemoteSeparator);
            if (value.Trim(RemoteSeparator).Length == 0) return JellyfinSftpSettings.DefaultMoviesPath;

            var absolute = value.StartsWith(RemoteSeparator);
            var trimmed = value.Trim(RemoteSeparator);

            return absolute ? RemoteSeparator + trimmed : trimmed;
        }

        /// <summary>
        /// Every directory that has to exist before a file can be written at
        /// <paramref name="remoteFolder"/>, outermost first. SFTP has no "make parents" flag, so a
        /// server whose movies directory is two levels down needs each level asked for in turn.
        /// </summary>
        public static string[] AncestorsOf(string? remoteFolder)
        {
            var value = (remoteFolder ?? "").Trim().Replace('\\', RemoteSeparator);
            if (value.Trim(RemoteSeparator).Length == 0) return Array.Empty<string>();

            var absolute = value.StartsWith(RemoteSeparator);
            var parts = value.Trim(RemoteSeparator).Split(RemoteSeparator, StringSplitOptions.RemoveEmptyEntries);
            var result = new string[parts.Length];
            var built = new StringBuilder();

            for (var i = 0; i < parts.Length; i++)
            {
                if (built.Length > 0) built.Append(RemoteSeparator);
                built.Append(parts[i]);
                result[i] = absolute ? RemoteSeparator + built.ToString() : built.ToString();
            }

            return result;
        }

        /// <summary>
        /// The name in <paramref name="names"/> that is already this film, or null. Matched on the
        /// stem rather than the whole filename because the extension depends on what the user
        /// happens to hold — a library that already has <c>Arrival (2016).mp4</c> must not be sent
        /// <c>Arrival (2016).mkv</c> to sit beside it, which Jellyfin would show as two versions
        /// of one film and nobody asked for.
        ///
        /// A leftover <c>.uploading</c> file is deliberately not a match: it is a transfer that
        /// failed, not a film, and treating it as one would make a retry impossible.
        /// </summary>
        public static string? FindExisting(IEnumerable<string>? names, string? title, int? year)
        {
            if (names is null) return null;

            var stem = RemoteStem(title, year);

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!ScanService.IsVideoFile(name)) continue;

                if (string.Equals(Path.GetFileNameWithoutExtension(name), stem, StringComparison.OrdinalIgnoreCase))
                    return name;
            }

            return null;
        }

        /// <summary>
        /// Why this file may not be sent to the server, or null when it may. The same rule the app
        /// applies to a file it would open — see
        /// <see cref="PlayTargetResolver.DescribeLinkRefusal"/> — because a path that is not a
        /// video file has no business being copied into somebody's film library either, and a
        /// second implementation of "is this a film?" would eventually disagree with the first.
        /// </summary>
        public static string? DescribeRefusal(string? localPath, Func<string, bool>? fileExists = null) =>
            PlayTargetResolver.DescribeLinkRefusal(localPath, fileExists);
    }
}
