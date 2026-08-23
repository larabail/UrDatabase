using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace UrDatabase.Services
{
    /// <summary>
    /// The one class in the app that opens an SSH connection. Everything else talks to
    /// <see cref="ISftpTransport"/>, so this file is also the only one a test cannot reach.
    ///
    /// SSH.NET is bundled rather than shelling out to the system <c>sftp</c> binary. Shelling out
    /// would mean parsing another program's output to report progress, having no way to cancel
    /// mid-file that is not a signal, and depending on a binary that Windows has only had since
    /// 2018 and that a stripped-down install may still not have. Carrying the library costs a
    /// megabyte and makes the failures typed.
    ///
    /// A key is the only credential it will use. The account worth pointing this at is one that
    /// can do nothing but write films, and such accounts are set up key-only; asking for a
    /// password as well would invite one into a configuration file for no gain.
    /// </summary>
    public sealed class SshNetSftpTransport : ISftpTransport
    {
        private readonly JellyfinSftpSettings _settings;
        private readonly TimeSpan _connectTimeout;

        private SftpClient? _client;
        private PrivateKeyFile? _key;
        private bool _disposed;

        public SshNetSftpTransport(JellyfinSftpSettings settings, TimeSpan? connectTimeout = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            // Long enough for a server that has to spin a disk up, short enough that an address
            // typed wrong says so while the user still remembers typing it.
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(20);
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (_client is { IsConnected: true }) return;

            if (!_settings.IsConfigured)
                throw new JellyfinException("No SFTP account is configured for your Jellyfin server.");

            // Read first and separately. A missing or unreadable key is the commonest failure of
            // the lot, and it deserves to be reported as itself rather than as whatever the
            // connection attempt would have made of it.
            _key ??= ReadKey();

            var connection = new PrivateKeyConnectionInfo(
                _settings.Host,
                _settings.Port,
                _settings.Username,
                _key)
            {
                Timeout = _connectTimeout
            };

            var client = new SftpClient(connection);

            try
            {
                await client.ConnectAsync(ct);
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                client.Dispose();
                AppLog.Write("jellyfin.log", $"sftp connect failed: {ex.GetType().Name}: {ex.Message}");
                throw new JellyfinException(
                    SftpFailure.Describe(ex, _settings.Host, _settings.Port, _settings.PrivateKeyPath), ex);
            }

            _client?.Dispose();
            _client = client;
        }

        private PrivateKeyFile ReadKey()
        {
            try
            {
                return string.IsNullOrEmpty(_settings.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(_settings.PrivateKeyPath)
                    : new PrivateKeyFile(_settings.PrivateKeyPath, _settings.PrivateKeyPassphrase);
            }
            catch (Exception ex)
            {
                // Deliberately no passphrase, no key material and no exception detail in the log:
                // the path is the useful part and the rest is a secret.
                AppLog.Write("jellyfin.log", $"sftp key unreadable: {_settings.PrivateKeyPath}");
                throw new JellyfinException(
                    SftpFailure.Describe(ex, _settings.Host, _settings.Port, _settings.PrivateKeyPath), ex);
            }
        }

        public Task<bool> ExistsAsync(string remotePath, CancellationToken ct = default) =>
            Guarded(client => client.ExistsAsync(remotePath, ct));

        public async Task<IReadOnlyList<string>> ListAsync(string remoteFolder, CancellationToken ct = default)
        {
            var names = new List<string>();

            try
            {
                await foreach (var entry in Client().ListDirectoryAsync(remoteFolder, ct))
                {
                    if (entry.Name is "." or "..") continue;
                    names.Add(entry.Name);
                }
            }
            catch (SftpPathNotFoundException)
            {
                // A film nobody has uploaded yet has no directory, which is not a failure — it is
                // the answer.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JellyfinException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Translate(ex);
            }

            return names;
        }

        public async Task<long?> LengthAsync(string remotePath, CancellationToken ct = default)
        {
            try
            {
                var attributes = await Client().GetAttributesAsync(remotePath, ct);
                return attributes.Size;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // "How big is it?" has a safe wrong answer — not knowing — and every caller here
                // is asking in order to check something rather than to act on it.
                return null;
            }
        }

        public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            var client = Client();

            try
            {
                if (await client.ExistsAsync(remotePath, ct)) return;
                await client.CreateDirectoryAsync(remotePath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JellyfinException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Two uploads racing, or a directory created between the check and the call.
                if (await SafeExistsAsync(client, remotePath, ct)) return;

                throw Translate(ex);
            }
        }

        public async Task UploadAsync(
            Stream source,
            string remotePath,
            long? totalBytes,
            IProgress<JellyfinUploadProgress>? progress,
            CancellationToken ct = default)
        {
            var client = Client();

            // SSH.NET counts in unsigned bytes, in a type of its own, and knows nothing about this
            // app's progress record.
            var relay = progress is null
                ? null
                : new Progress<UploadFileProgressReport>(report =>
                    progress.Report(new JellyfinUploadProgress((long)report.TotalBytesUploaded, totalBytes)));

            try
            {
                await client.UploadFileAsync(source, remotePath, canOverride: true, relay, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Translate(ex);
            }
        }

        public async Task RenameAsync(string fromRemotePath, string toRemotePath, CancellationToken ct = default)
        {
            try
            {
                await Client().RenameFileAsync(fromRemotePath, toRemotePath, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Translate(ex);
            }
        }

        public async Task DeleteAsync(string remotePath, CancellationToken ct = default)
        {
            try
            {
                await Client().DeleteFileAsync(remotePath, ct);
            }
            catch (SftpPathNotFoundException)
            {
                // Already gone, which is what was being asked for.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Translate(ex);
            }
        }

        private static async Task<bool> SafeExistsAsync(SftpClient client, string remotePath, CancellationToken ct)
        {
            try { return await client.ExistsAsync(remotePath, ct); }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }

        private async Task<T> Guarded<T>(Func<SftpClient, Task<T>> operation)
        {
            try
            {
                return await operation(Client());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JellyfinException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Translate(ex);
            }
        }

        private JellyfinException Translate(Exception ex)
        {
            AppLog.Write("jellyfin.log", $"sftp failed: {ex.GetType().Name}: {ex.Message}");
            return new JellyfinException(
                SftpFailure.Describe(ex, _settings.Host, _settings.Port, _settings.PrivateKeyPath), ex);
        }

        private SftpClient Client() =>
            _client ?? throw new JellyfinException("The SFTP connection was not opened.");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _client?.Dispose(); } catch { }
            try { _key?.Dispose(); } catch { }

            _client = null;
            _key = null;
        }
    }
}
