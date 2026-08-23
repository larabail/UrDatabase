using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// What a finished download left behind.
    /// </summary>
    /// <param name="Path">The film on this disk.</param>
    /// <param name="Bytes">Size of the finished file.</param>
    /// <param name="AlreadyExisted">
    /// True when nothing was transferred because the film was already there. Not an error: asking
    /// twice is what a person does when they cannot remember whether the first one finished.
    /// </param>
    public readonly record struct JellyfinDownloadResult(string Path, long Bytes, bool AlreadyExisted);

    /// <summary>
    /// Copies a film off the Jellyfin server so it can be watched with the server switched off, on
    /// a train, or on a laptop that is nowhere near the house.
    ///
    /// Everything here is arranged around the transfer being long and the machine being a laptop.
    /// Bytes land in a <c>.part</c> file so a half-downloaded film is never mistaken for a whole
    /// one; an interrupted transfer keeps what it got and resumes from there; and the finished file
    /// only takes its real name once the last byte is written, which is what makes "is this film
    /// already downloaded?" a question the filesystem can answer.
    ///
    /// The transfer is authenticated with a header rather than a token in the URL, so unlike
    /// streaming there is no credential in anything this class handles, logs or leaves on disk.
    /// </summary>
    public sealed class JellyfinDownloader
    {
        /// <summary>
        /// 128 KiB. Large enough that a gigabyte film is not millions of loop iterations, small
        /// enough that Cancel is felt immediately rather than at the end of the current read.
        /// </summary>
        private const int BufferSize = 128 * 1024;

        /// <summary>
        /// How often progress is reported. A film is read faster than a person can look, and
        /// forwarding every chunk to the UI thread would spend more time redrawing a label than
        /// writing the file.
        /// </summary>
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

        private readonly JellyfinClient _client;

        public JellyfinDownloader(JellyfinClient client)
            => _client = client ?? throw new ArgumentNullException(nameof(client));

        /// <summary>
        /// Fetches one film into <paramref name="folder"/>, named from the catalogue's own title
        /// rather than from anything the server sends.
        /// </summary>
        /// <param name="container">
        /// The film's container as Jellyfin reports it, used for the extension when the response
        /// carries no filename. Optional.
        /// </param>
        /// <exception cref="JellyfinException">
        /// For anything the user can act on: the server being unreachable, the item having been
        /// removed, the disk being full or the folder being unwritable.
        /// </exception>
        public async Task<JellyfinDownloadResult> DownloadAsync(
            string itemId,
            string? title,
            int? year,
            string? folder,
            string? container = null,
            IProgress<JellyfinDownloadProgress>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new JellyfinException("This film has no id on the server, so it cannot be downloaded.");

            // Asked before the request, because a film already on disk should cost nothing at all —
            // not a connection, not a range request the server has to answer. Matched by stem
            // rather than by full name: the container is whatever the server turned out to be
            // holding, so a film fetched earlier as .mp4 must still be recognised here.
            var existing = JellyfinDownload.FindExisting(folder, title, year);
            if (existing is not null)
                return new JellyfinDownloadResult(existing, SafeLength(existing), AlreadyExisted: true);

            var provisional = JellyfinDownload.BuildPath(folder, title, year, container);
            var partialOfProvisional = JellyfinDownload.PartialPathFor(provisional);
            var resumeFrom = File.Exists(partialOfProvisional) ? SafeLength(partialOfProvisional) : 0L;

            using var response = await _client.OpenDownloadAsync(itemId, resumeFrom, ct);

            // The server may name the file, and its extension is the only trustworthy statement
            // about the container. That can change the target path, so the "already there" and
            // resume questions are asked once more against the real name.
            var extension = JellyfinDownload.ResolveExtension(
                response.Content.Headers.ContentDisposition?.FileNameStar
                    ?? response.Content.Headers.ContentDisposition?.FileName,
                container);

            var path = JellyfinDownload.BuildPath(folder, title, year, extension);
            var partial = JellyfinDownload.PartialPathFor(path);

            if (!string.Equals(path, provisional, StringComparison.Ordinal))
            {
                if (File.Exists(path))
                    return new JellyfinDownloadResult(path, SafeLength(path), AlreadyExisted: true);

                resumeFrom = File.Exists(partial) ? SafeLength(partial) : 0L;
            }

            // Honoured only if the server said so. A proxy that ignores Range answers 200 with the
            // whole film, and appending that to a partial file would silently corrupt it.
            var resuming = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
            var startAt = resuming ? resumeFrom : 0L;

            var total = ResolveTotalBytes(response, startAt);

            EnsureFolder(path);

            try
            {
                await CopyAsync(response, partial, startAt, total, progress, ct);
            }
            catch (OperationCanceledException)
            {
                // The partial file is deliberately left where it is: it is the whole point of
                // being able to resume, and deleting a user's half-hour of transfer because they
                // closed a window would be its own bug.
                throw;
            }
            catch (IOException ex)
            {
                AppLog.Write("jellyfin.log", $"download failed: {ex.Message}");
                throw new JellyfinException(
                    $"Could not write the film to {Path.GetDirectoryName(path)}. " +
                    "Check there is space on the disk and that the folder can be written to.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Write("jellyfin.log", $"download failed: {ex.Message}");
                throw new JellyfinException(
                    $"This app is not allowed to write to {Path.GetDirectoryName(path)}.", ex);
            }
            catch (HttpRequestException ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"download interrupted: {ex.Message}"));
                throw new JellyfinException(
                    "The download was interrupted. Starting it again will carry on from where it stopped.", ex);
            }

            var written = SafeLength(partial);

            // A truncated file that keeps the film's real name is worse than no file: it plays,
            // stops early, and looks like a bad rip rather than a failed download.
            if (total is > 0 && written != total)
            {
                AppLog.Write("jellyfin.log", $"download short: {written} of {total} bytes for item {itemId}");
                throw new JellyfinException(
                    "The download ended early and the film is incomplete. " +
                    "Starting it again will carry on from where it stopped.");
            }

            Promote(partial, path);
            return new JellyfinDownloadResult(path, written, AlreadyExisted: false);
        }

        /// <summary>
        /// Streams the body into the partial file. Opened for append when resuming and truncated
        /// otherwise, so a server that ignored the range restarts cleanly instead of appending a
        /// second copy of the film onto the first few minutes of one.
        /// </summary>
        private static async Task CopyAsync(
            HttpResponseMessage response,
            string partial,
            long startAt,
            long? total,
            IProgress<JellyfinDownloadProgress>? progress,
            CancellationToken ct)
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(
                partial,
                startAt > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);

            var buffer = new byte[BufferSize];
            var written = startAt;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            progress?.Report(new JellyfinDownloadProgress(written, total));

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;

                if (clock.Elapsed - lastReport < ProgressInterval) continue;

                lastReport = clock.Elapsed;
                progress?.Report(new JellyfinDownloadProgress(written, total));
            }

            await destination.FlushAsync(ct);
            progress?.Report(new JellyfinDownloadProgress(written, total));
        }

        /// <summary>
        /// How big the finished film will be. On a resumed transfer the body is only the remainder,
        /// so <c>Content-Length</c> alone would report a percentage of the wrong number;
        /// <c>Content-Range</c> carries the real total and is preferred when present.
        /// </summary>
        private static long? ResolveTotalBytes(HttpResponseMessage response, long startAt)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Length is long length && length > 0) return length;

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is null or <= 0) return null;

            return startAt > 0 ? startAt + contentLength.Value : contentLength.Value;
        }

        /// <summary>
        /// Gives the finished file its real name. Done as a move within one folder, which is
        /// atomic on both platforms: at no point does a file with the film's name exist while
        /// still being written.
        /// </summary>
        private static void Promote(string partial, string path)
        {
            try
            {
                File.Move(partial, path, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new JellyfinException(
                    "The film downloaded but could not be renamed. " +
                    $"It is on this disk as {Path.GetFileName(partial)}.", ex);
            }
        }

        private static void EnsureFolder(string path)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return;

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                throw new JellyfinException($"Could not create the download folder {directory}.", ex);
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }
    }
}
