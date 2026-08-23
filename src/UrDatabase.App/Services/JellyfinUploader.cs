using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// What a finished upload left on the server.
    /// </summary>
    /// <param name="RemotePath">Where the film now is, as the SFTP account sees it.</param>
    /// <param name="Bytes">Size of the file that was sent, or of the one already there.</param>
    /// <param name="AlreadyExisted">
    /// True when nothing was transferred because the server already had this film. Not an error:
    /// asking twice is what a person does when they cannot remember whether the first one
    /// finished, and a film is far too large to send again to find out.
    /// </param>
    /// <param name="LibraryRefreshed">
    /// True when Jellyfin agreed to rescan. False means the film is on the server's disk but
    /// Jellyfin has not been told, which happens when the account browsing the library is not an
    /// administrator — the film then appears at the server's next scheduled scan instead.
    /// </param>
    public readonly record struct JellyfinUploadResult(
        string RemotePath,
        long Bytes,
        bool AlreadyExisted,
        bool LibraryRefreshed);

    /// <summary>
    /// Puts a film from this disk onto the Jellyfin server, which is not something Jellyfin itself
    /// can be asked to do.
    ///
    /// Its API takes an image or a subtitle and no other kind of file; a film becomes a film by
    /// being on the server's own filesystem when the library is scanned. So this copies the file
    /// there over SFTP and then asks Jellyfin to look again — two steps, and the second one is
    /// what makes the first one visible.
    ///
    /// The safety properties are the download's, mirrored, and they exist for the same reasons:
    /// bytes land under a name ending in <c>.uploading</c>, which no scan will read as a film, and
    /// the file only takes the film's real name once the last byte is there and the size matches.
    /// The rename is the last thing that happens, so nothing that goes wrong before it can put the
    /// film's own name on a partial file — which is the property worth guaranteeing, and the one
    /// that is actually guaranteed.
    ///
    /// The partial file itself is removed on every failure path, but that is best effort rather
    /// than a promise: deleting it needs the connection that has just dropped. What is left is
    /// inert — Jellyfin ignores the extension, so it never becomes a film — and the next attempt
    /// clears it before starting. Messages say "nothing was added to your library" rather than
    /// "nothing was left on the server" for exactly this reason.
    ///
    /// The one property it does not mirror is resume. SFTP can append, but a remote file's bytes
    /// cannot be checked against the local ones without reading them all back, so "carry on from
    /// 3.2 GB" would be a guess that the first 3.2 GB are the right ones. Starting again is slower
    /// and correct; see the README's Known gaps.
    /// </summary>
    public sealed class JellyfinUploader
    {
        /// <summary>
        /// 128 KiB, matching the download. Large enough that a gigabyte film is not millions of
        /// loop iterations, small enough that Cancel is felt immediately.
        /// </summary>
        private const int BufferSize = 128 * 1024;

        private readonly ISftpTransport _transport;
        private readonly JellyfinClient? _client;

        /// <param name="transport">
        /// How to talk to the server's filesystem. Owned by the caller, which disposes it.
        /// </param>
        /// <param name="client">
        /// The Jellyfin server itself, for asking it to rescan afterwards. Optional: without one
        /// the film still arrives, and Jellyfin finds it on its own schedule.
        /// </param>
        public JellyfinUploader(ISftpTransport transport, JellyfinClient? client = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _client = client;
        }

        /// <summary>
        /// Sends one film to the server, into <c>movies/Title (Year)/Title (Year).ext</c>.
        /// </summary>
        /// <param name="localPath">The file on this disk. Refused unless it is a video file that exists.</param>
        /// <param name="title">The catalogue's title, which names the film on the server.</param>
        /// <param name="year">The catalogue's year. A film without one simply has none in its name.</param>
        /// <param name="moviesPath">The server's movies directory, as the SFTP account sees it.</param>
        /// <exception cref="JellyfinException">
        /// For anything the user can act on: the file not being a film, the server refusing the
        /// key, the account being unable to write there, the transfer ending early.
        /// </exception>
        public async Task<JellyfinUploadResult> UploadAsync(
            string? localPath,
            string? title,
            int? year,
            string? moviesPath,
            IProgress<JellyfinUploadProgress>? progress = null,
            CancellationToken ct = default)
        {
            // Asked before anything opens a connection: a file that was never going to be sent
            // should not cost a handshake, and the answer does not depend on the server.
            var refusal = JellyfinUpload.DescribeRefusal(localPath);
            if (refusal is not null) throw new JellyfinException(refusal);

            var folder = JellyfinUpload.BuildRemoteFolder(moviesPath, title, year);
            var remotePath = JellyfinUpload.BuildRemotePath(moviesPath, title, year, localPath);
            var partial = JellyfinUpload.PartialPathFor(remotePath);

            await _transport.ConnectAsync(ct);

            var existing = await FindExistingAsync(folder, title, year, ct);
            if (existing is not null)
            {
                var size = await _transport.LengthAsync(existing, ct) ?? 0L;
                return new JellyfinUploadResult(existing, size, AlreadyExisted: true, LibraryRefreshed: false);
            }

            foreach (var ancestor in JellyfinUpload.AncestorsOf(folder))
                await _transport.CreateDirectoryAsync(ancestor, ct);

            // A previous attempt that was cancelled or dropped leaves one of these. It is not
            // resumed — see the class remarks — so it is cleared out rather than appended to.
            //
            // Best effort, and deliberately not fatal: the transfer below overwrites whatever is
            // at that path anyway, so failing to delete it first is not a reason to refuse to
            // upload. Left unguarded this was the one call in the method that could put a raw
            // transport exception in front of somebody.
            await DiscardAsync(partial);

            var total = LocalLength(localPath!);

            // One try around the transfer *and* everything after it that can still fail. Scoping
            // it to the transfer alone left a full-size .uploading file on the server whenever the
            // size check or the rename was the thing that went wrong — which is exactly when a
            // person has already waited an hour and is least inclined to go and look.
            try
            {
                await using (var source = new FileStream(
                    localPath!,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    useAsync: true))
                {
                    await _transport.UploadAsync(source, partial, total, progress, ct);
                }

                // A film that arrives short and then takes the film's real name is worse than no
                // film at all: it plays, stops early, and looks like a bad rip rather than a
                // failed upload — and it does so on everybody's television, not just this one.
                var written = await _transport.LengthAsync(partial, ct);
                if (written is not null && total > 0 && written != total)
                {
                    AppLog.Write("jellyfin.log", $"upload short: {written} of {total} bytes for {remotePath}");
                    throw new JellyfinException(
                        "The upload ended early and the film on the server was incomplete, so it was removed. " +
                        "Starting again uploads it from the beginning.");
                }

                // The last step, and the only one that puts the film's real name on anything.
                await _transport.RenameAsync(partial, remotePath, ct);
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the half-written file goes. Leaving it would be leaving
                // rubbish in somebody's film library that only this app knows how to interpret.
                await DiscardAsync(partial);

                throw ex switch
                {
                    OperationCanceledException or JellyfinException => ex,
                    IOException => new JellyfinException(
                        $"Could not read {Path.GetFileName(localPath)} from this disk while uploading it.", ex),
                    UnauthorizedAccessException => new JellyfinException(
                        $"This app is not allowed to read {Path.GetFileName(localPath)}.", ex),
                    _ => new JellyfinException($"The upload failed: {ex.Message}", ex)
                };
            }

            var refreshed = await RefreshLibraryAsync(ct);

            return new JellyfinUploadResult(remotePath, total, AlreadyExisted: false, LibraryRefreshed: refreshed);
        }

        /// <summary>
        /// The film's existing copy on the server, or null. One directory listing rather than a
        /// question about one filename, because the copy up there may have a different extension
        /// from the copy down here and asking only about the exact name would miss it.
        /// </summary>
        private async Task<string?> FindExistingAsync(string folder, string? title, int? year, CancellationToken ct)
        {
            var names = await _transport.ListAsync(folder, ct);
            var match = JellyfinUpload.FindExisting(names, title, year);

            return match is null ? null : JellyfinUpload.JoinRemote(folder, match);
        }

        /// <summary>
        /// Asks Jellyfin to look at its library again, and reports whether it agreed to.
        ///
        /// Never throws. The film is already on the server's disk by this point, so a refusal here
        /// changes when it appears rather than whether it does — and interrupting somebody with an
        /// error about a transfer that succeeded would be its own bug. The commonest refusal is a
        /// 403: scanning a library is an administrative action and the account browsing it need
        /// not be an administrator.
        /// </summary>
        private async Task<bool> RefreshLibraryAsync(CancellationToken ct)
        {
            if (_client is null) return false;

            try
            {
                await _client.RefreshLibraryAsync(ct);
                return true;
            }
            catch (OperationCanceledException)
            {
                // The film is up; the user cancelled the wait, not the upload.
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"library refresh declined: {ex.Message}"));
                return false;
            }
        }

        /// <summary>
        /// Removes a partial file, best effort and without a cancellation token: the commonest
        /// reason to be here is that the token was just cancelled, and cleanup that cancels itself
        /// is cleanup that never happens.
        ///
        /// It can still fail, and the case where it fails is not obscure — a dropped connection is
        /// both the reason for the cleanup and the reason it cannot happen. Nothing depends on it
        /// succeeding: what is left is inert, and the next attempt deletes it before starting.
        /// </summary>
        private async Task DiscardAsync(string remotePath)
        {
            try
            {
                await _transport.DeleteAsync(remotePath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"could not remove partial upload {remotePath}: {ex.Message}");
            }
        }

        private static long LocalLength(string path)
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
