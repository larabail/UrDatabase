using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A server's filesystem, in memory. Stands in for SFTP the way
    /// <see cref="FakeHttpMessageHandler"/> stands in for the network, and for the same reason:
    /// the failures worth testing here are a refused key, a dropped connection and a transfer
    /// stopped halfway, and none of them can be arranged reliably against a real server — least
    /// of all in CI, which has no server, no key and no network to the house.
    ///
    /// It records what it was asked to do as well as what it holds, because half of what this
    /// feature must get right is about calls not made: a film already up there costs no transfer,
    /// and a failed upload leaves nothing renamed into place.
    /// </summary>
    public class FakeSftpTransport : ISftpTransport
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

        /// <summary>Every method call, in order, as "verb path".</summary>
        public List<string> Calls { get; } = new();

        public int Connections { get; private set; }

        /// <summary>Thrown by <see cref="ConnectAsync"/> when set, for the failure cases.</summary>
        public Exception? ConnectFailure { get; set; }

        /// <summary>Thrown by <see cref="UploadAsync"/> after <see cref="StopAfterBytes"/>.</summary>
        public Exception? UploadFailure { get; set; }

        /// <summary>
        /// How many bytes to accept before <see cref="UploadFailure"/> is raised, so a test can
        /// leave a genuinely half-written file on the fake server.
        /// </summary>
        public int StopAfterBytes { get; set; }

        /// <summary>
        /// Called once the upload has begun, for a test that needs to cancel mid-transfer.
        /// </summary>
        public Action? DuringUpload { get; set; }

        /// <summary>Trims the file after it is written, to fake a transfer that arrived short.</summary>
        public int? TruncateWrittenTo { get; set; }

        /// <summary>Thrown by <see cref="RenameAsync"/> when set — the last step, and the one that
        /// puts the film's real name on the file.</summary>
        public Exception? RenameFailure { get; set; }

        /// <summary>Called at the start of <see cref="LengthAsync"/>, for cancelling in the window
        /// between the last byte arriving and the rename.</summary>
        public Action? DuringLength { get; set; }

        public bool Disposed { get; private set; }

        public IReadOnlyDictionary<string, byte[]> Files => _files;

        public IReadOnlyCollection<string> Directories => _directories;

        public void Put(string remotePath, string contents)
        {
            _files[remotePath] = System.Text.Encoding.UTF8.GetBytes(contents);
            var slash = remotePath.LastIndexOf('/');
            if (slash > 0) AddDirectories(remotePath[..slash]);
        }

        public string TextAt(string remotePath) =>
            _files.TryGetValue(remotePath, out var bytes) ? System.Text.Encoding.UTF8.GetString(bytes) : "";

        public Task ConnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("connect");

            if (ConnectFailure is not null) throw ConnectFailure;

            Connections++;
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("exists " + remotePath);

            return Task.FromResult(_files.ContainsKey(remotePath) || _directories.Contains(remotePath));
        }

        public Task<IReadOnlyList<string>> ListAsync(string remoteFolder, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("list " + remoteFolder);

            var prefix = remoteFolder.TrimEnd('/') + "/";

            IReadOnlyList<string> names = _files.Keys
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Select(path => path[prefix.Length..])
                .Where(name => !name.Contains('/'))
                .ToList();

            return Task.FromResult(names);
        }

        public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("mkdir " + remotePath);
            _directories.Add(remotePath.TrimEnd('/'));

            return Task.CompletedTask;
        }

        public async Task UploadAsync(
            Stream source,
            string remotePath,
            long? totalBytes,
            IProgress<JellyfinUploadProgress>? progress,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("upload " + remotePath);

            var buffer = new MemoryStream();
            var chunk = new byte[8];

            DuringUpload?.Invoke();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var read = await source.ReadAsync(chunk, ct);
                if (read == 0) break;

                buffer.Write(chunk, 0, read);
                progress?.Report(new JellyfinUploadProgress(buffer.Length, totalBytes));

                if (UploadFailure is not null && buffer.Length >= StopAfterBytes)
                {
                    // Written first, deliberately: a real transfer that fails has already put
                    // something on the server, and the point of the test is what happens to it.
                    _files[remotePath] = buffer.ToArray();
                    throw UploadFailure;
                }
            }

            var written = buffer.ToArray();
            if (TruncateWrittenTo is int limit && written.Length > limit) written = written[..limit];

            _files[remotePath] = written;
        }

        public Task<long?> LengthAsync(string remotePath, CancellationToken ct = default)
        {
            DuringLength?.Invoke();
            ct.ThrowIfCancellationRequested();
            Calls.Add("length " + remotePath);

            return Task.FromResult(_files.TryGetValue(remotePath, out var bytes) ? bytes.LongLength : (long?)null);
        }

        public Task RenameAsync(string fromRemotePath, string toRemotePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add($"rename {fromRemotePath} -> {toRemotePath}");

            if (RenameFailure is not null) throw RenameFailure;

            if (!_files.Remove(fromRemotePath, out var bytes))
                throw new InvalidOperationException($"nothing at {fromRemotePath}");

            _files[toRemotePath] = bytes;
            return Task.CompletedTask;
        }

        public virtual Task DeleteAsync(string remotePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("delete " + remotePath);

            _files.Remove(remotePath);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        private void AddDirectories(string folder)
        {
            var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var built = folder.StartsWith('/') ? "/" : "";

            foreach (var part in parts)
            {
                built = built.Length is 0 or 1 ? built + part : built + "/" + part;
                _directories.Add(built);
            }
        }
    }
}
