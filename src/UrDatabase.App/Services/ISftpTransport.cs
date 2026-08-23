using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services
{
    /// <summary>
    /// The few SFTP operations putting a film on a server needs, and nothing else.
    ///
    /// This exists so that no test has to open a socket. Uploading is where the interesting
    /// failures live — a connection refused, a key the server will not take, a transfer stopped
    /// halfway, a film already there — and every one of them has to be exercised on a laptop with
    /// no server, in CI with no credentials, and on a plane. A live SFTP session cannot do that,
    /// so <see cref="JellyfinUploader"/> talks to this instead and the real implementation
    /// (<see cref="SshNetSftpTransport"/>) is the only thing that ever touches the network.
    ///
    /// Every path here is a remote path: forward slashes, built by
    /// <see cref="JellyfinUpload"/>, never by <see cref="Path"/>.
    /// </summary>
    public interface ISftpTransport : IDisposable
    {
        /// <summary>
        /// Opens the session. Safe to call more than once; the work happens once.
        /// </summary>
        /// <exception cref="JellyfinException">
        /// For anything a person can act on: the machine not answering, the key being missing or
        /// unreadable, the server refusing it.
        /// </exception>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>True when something is already at that path, file or directory.</summary>
        Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default);

        /// <summary>
        /// The names — not paths — of everything directly inside a remote directory, or nothing at
        /// all when it does not exist. Asking is what makes "this film is already up there" a
        /// question that survives the extension changing: a library holding
        /// <c>Title (Year).mp4</c> must not be sent <c>Title (Year).mkv</c> alongside it.
        /// </summary>
        Task<IReadOnlyList<string>> ListAsync(string remoteFolder, CancellationToken ct = default);

        /// <summary>
        /// How many bytes are at that path, or null when it is not there or cannot be measured.
        /// Used to check that what arrived is the size of what was sent.
        /// </summary>
        Task<long?> LengthAsync(string remotePath, CancellationToken ct = default);

        /// <summary>
        /// Creates one directory. The caller creates parents itself, in order, because SFTP has
        /// no equivalent of <c>mkdir -p</c>; an existing directory is not an error.
        /// </summary>
        Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);

        /// <summary>Writes a stream to a remote path, overwriting whatever is there.</summary>
        Task UploadAsync(
            Stream source,
            string remotePath,
            long? totalBytes,
            IProgress<JellyfinUploadProgress>? progress,
            CancellationToken ct = default);

        /// <summary>
        /// Renames within the same directory, which is what gives a finished upload its real name
        /// in one step rather than as a file that grows under it.
        /// </summary>
        Task RenameAsync(string fromRemotePath, string toRemotePath, CancellationToken ct = default);

        /// <summary>
        /// Removes a file. Used to clear up a partial transfer, so it must not throw for a path
        /// that is already gone.
        /// </summary>
        Task DeleteAsync(string remotePath, CancellationToken ct = default);
    }
}
