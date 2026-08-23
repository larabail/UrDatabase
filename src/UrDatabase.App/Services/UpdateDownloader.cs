using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// How far a download has got. Total is null only when the release API did not say how big the
    /// asset is, which it always does — but a value that can be absent is better than a zero that
    /// silently means "unknown" and shows as a bar stuck at the left.
    /// </summary>
    public readonly record struct UpdateProgress(long BytesRead, long? TotalBytes)
    {
        public double? Fraction =>
            TotalBytes is > 0 ? Math.Clamp((double)BytesRead / TotalBytes.Value, 0d, 1d) : null;

        /// <summary>A line for the banner. Short: it is rewritten several times a second.</summary>
        public string Describe() => TotalBytes is > 0
            ? $"{ByteSize.Describe(BytesRead)} of {ByteSize.Describe(TotalBytes.Value)} ({Fraction!.Value * 100:0}%)"
            : ByteSize.Describe(BytesRead);
    }

    /// <summary>
    /// Fetches a new build of the app onto this disk.
    ///
    /// It does not install it, and nothing here pretends otherwise. On macOS the running app is a
    /// signed bundle that cannot rewrite itself without invalidating its own signature, and on
    /// Windows it is a folder of files the running process holds open; a self-replacing updater is
    /// a separate program in both cases. So the most this can honestly do is put the file a person
    /// would otherwise have downloaded from the website where they can find it, and open it.
    ///
    /// The bytes land in a <c>.part</c> file and the finished download only takes its real name
    /// once the last one is written, so a cancelled or interrupted fetch can never leave something
    /// in the downloads folder that looks like a complete release and is not.
    /// </summary>
    public sealed class UpdateDownloader : IDisposable
    {
        /// <summary>
        /// 128 KiB. Large enough that an eighty megabyte archive is not a million loop iterations,
        /// small enough that Cancel is felt immediately rather than at the end of the current read.
        /// </summary>
        private const int BufferSize = 128 * 1024;

        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

        /// <summary>What a partial transfer is called while it is still running.</summary>
        public const string PartialExtension = ".part";

        private readonly HttpClient _http;

        public UpdateDownloader(HttpMessageHandler? handler = null)
        {
            _http = handler is null ? new HttpClient() : new HttpClient(handler);

            // No request timeout at all: this one covers reading the body, so any finite value is
            // a limit on how slow the user's connection may be rather than on how long the app
            // will wait for a server that has stopped answering. The cancellation token is the
            // only limit, and it is what the banner's Cancel is wired to.
            _http.Timeout = Timeout.InfiniteTimeSpan;
        }

        /// <summary>
        /// Fetches <paramref name="asset"/> into <paramref name="folder"/> and returns the file it
        /// wrote. A complete copy already sitting there is returned untouched, because pressing
        /// Update now twice is what a person does when they cannot remember whether the first one
        /// finished, and re-fetching eighty megabytes to answer that is rude.
        /// </summary>
        /// <exception cref="UpdateException">For anything the user can act on or be told about.</exception>
        public async Task<string> DownloadAsync(
            UpdateAsset asset,
            string? folder,
            IProgress<UpdateProgress>? progress = null,
            CancellationToken ct = default)
        {
            var path = ResolvePath(asset, folder);

            if (IsAlreadyComplete(path, asset.Bytes)) return path;

            var partial = path + PartialExtension;

            EnsureFolder(path);

            try
            {
                using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    AppLog.Write("update.log", $"download of {asset.Name}: HTTP {(int)response.StatusCode}");
                    throw new UpdateException(
                        "GitHub would not hand over that build. It can be downloaded from the website instead.");
                }

                var total = response.Content.Headers.ContentLength is long length && length > 0
                    ? length
                    : asset.Bytes > 0 ? asset.Bytes : (long?)null;

                await CopyAsync(response, partial, total, progress, ct);
            }
            catch (OperationCanceledException)
            {
                // Deliberately swept up rather than kept. There is no resume here — unlike a film,
                // a release archive is small enough to fetch again and is replaced wholesale by the
                // next one — so a partial file has no future and would only accumulate.
                Discard(partial);
                throw;
            }
            catch (HttpRequestException ex)
            {
                Discard(partial);
                AppLog.Write("update.log", $"download of {asset.Name} interrupted: {ex.Message}");
                throw new UpdateException(
                    "That download was interrupted. It can be downloaded from the website instead.", ex);
            }
            catch (IOException ex)
            {
                Discard(partial);
                AppLog.Write("update.log", $"download of {asset.Name} failed: {ex.Message}");
                throw new UpdateException(
                    $"Could not write to {Path.GetDirectoryName(path)}. Check there is space on the disk.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                Discard(partial);
                AppLog.Write("update.log", $"download of {asset.Name} refused: {ex.Message}");
                throw new UpdateException($"This app is not allowed to write to {Path.GetDirectoryName(path)}.", ex);
            }

            var written = SafeLength(partial);

            // A truncated archive that carries the release's real name is worse than no file: it is
            // a .dmg that will not mount or a .zip that will not open, and it looks like a bad
            // release rather than a bad download.
            if (asset.Bytes > 0 && written != asset.Bytes)
            {
                Discard(partial);
                AppLog.Write("update.log", $"download of {asset.Name} short: {written} of {asset.Bytes} bytes");
                throw new UpdateException("That download ended early. It can be downloaded from the website instead.");
            }

            Promote(partial, path);
            return path;
        }

        /// <summary>
        /// Where the asset lands. Named from the asset itself, but only ever from its filename —
        /// a name carrying a directory, or one made entirely of separators, is refused rather than
        /// allowed to write outside the folder that was chosen.
        /// </summary>
        internal static string ResolvePath(UpdateAsset asset, string? folder)
        {
            var directory = string.IsNullOrWhiteSpace(folder) ? PlatformPaths.DefaultUpdateFolder : folder.Trim();

            var name = Path.GetFileName((asset.Name ?? "").Trim());
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
                throw new UpdateException("That release asset has no usable filename.");

            return Path.Combine(directory, name);
        }

        private static async Task CopyAsync(
            HttpResponseMessage response,
            string partial,
            long? total,
            IProgress<UpdateProgress>? progress,
            CancellationToken ct)
        {
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var destination = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

            var buffer = new byte[BufferSize];
            var written = 0L;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            progress?.Report(new UpdateProgress(written, total));

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;

                if (clock.Elapsed - lastReport < ProgressInterval) continue;

                lastReport = clock.Elapsed;
                progress?.Report(new UpdateProgress(written, total));
            }

            await destination.FlushAsync(ct);
            progress?.Report(new UpdateProgress(written, total));
        }

        /// <summary>
        /// True when this exact build is already on the disk. Only ever answered yes on a size
        /// match: a file of the right name and the wrong length is the wreckage of something and
        /// must not be opened as though it were a release.
        /// </summary>
        private static bool IsAlreadyComplete(string path, long expectedBytes) =>
            expectedBytes > 0 && File.Exists(path) && SafeLength(path) == expectedBytes;

        private static void Promote(string partial, string path)
        {
            try
            {
                File.Move(partial, path, overwrite: true);
            }
            catch (IOException ex)
            {
                throw new UpdateException(
                    $"The update downloaded but could not be renamed. It is on this disk as {Path.GetFileName(partial)}.", ex);
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
                throw new UpdateException($"Could not create the folder {directory}.", ex);
            }
        }

        private static void Discard(string path)
        {
            try { File.Delete(path); } catch { }
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

        public void Dispose() => _http.Dispose();
    }

    /// <summary>Something about fetching an update that a person can be told in one sentence.</summary>
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
        public UpdateException(string message, Exception inner) : base(message, inner) { }
    }
}
