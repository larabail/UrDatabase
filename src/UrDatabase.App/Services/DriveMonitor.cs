using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Checks every drive the library lives on and says which of them are not there.
    ///
    /// The point is a disk that has died, or simply been left unplugged, being noticed when the
    /// app opens rather than when somebody presses Play on a film and the operating system refuses
    /// the path. Nothing in the catalogue changes as a result: a drive that is away is reported,
    /// not acted on.
    /// </summary>
    public sealed class DriveMonitor
    {
        /// <summary>
        /// Long enough for a sleeping USB disk to spin up, short enough that a vanished network
        /// share does not leave the answer hanging for the half-minute Windows will wait on it.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

        private readonly IDriveProbe _probe;
        private readonly TimeSpan _timeout;

        public DriveMonitor(IDriveProbe? probe = null, TimeSpan? timeout = null)
        {
            _probe = probe ?? FileSystemDriveProbe.Instance;
            _timeout = timeout ?? DefaultTimeout;
        }

        /// <summary>
        /// Probes every drive at once, each on the thread pool and each with its own time limit,
        /// so one dead share cannot hold up the verdict on the others. Never throws except to
        /// report cancellation.
        /// </summary>
        public async Task<IReadOnlyList<DriveReport>> CheckAsync(
            IEnumerable<DriveUsage> drives,
            CancellationToken ct = default)
        {
            var checks = drives.Select(drive => CheckOneAsync(drive, ct)).ToList();
            var reports = await Task.WhenAll(checks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return reports;
        }

        /// <summary>
        /// Reads the drives out of the configuration and the catalogue, then checks them. The
        /// catalogue is read on the thread pool along with everything else, so calling this from
        /// the UI thread costs it nothing.
        /// </summary>
        public Task<IReadOnlyList<DriveReport>> CheckLibraryAsync(
            IEnumerable<string>? watchFolders,
            string? databasePath,
            CancellationToken ct = default)
        {
            var folders = (watchFolders ?? Array.Empty<string>()).ToArray();

            return Task.Run(async () =>
            {
                var drives = DriveVolumes.Collect(folders, ReadCataloguedFiles(databasePath));
                return await CheckAsync(drives, ct).ConfigureAwait(false);
            }, ct);
        }

        /// <summary>
        /// Every file the catalogue believes is still present, with the film it belongs to. Empty,
        /// rather than an error, when there is no catalogue yet: a check of the drives must never
        /// be the thing that creates one, and must never be the thing that stops the app.
        /// </summary>
        public static IReadOnlyList<(string Path, long? MovieId)> ReadCataloguedFiles(string? databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
                return Array.Empty<(string, long?)>();

            try
            {
                using var conn = Database.Connect(databasePath);
                return conn.Query<(string, long?)>(
                        "SELECT file_path, movie_id FROM files WHERE missing_since IS NULL")
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLog.Write("drives.log", $"could not read catalogued files: {ex.Message}");
                return Array.Empty<(string, long?)>();
            }
        }

        private async Task<DriveReport> CheckOneAsync(DriveUsage drive, CancellationToken ct)
        {
            var probe = Task.Run(() =>
            {
                try { return _probe.Probe(drive.Root); }
                catch { return DriveProbeResult.Unavailable; }
            });

            var finished = await Task.WhenAny(probe, Task.Delay(_timeout, ct)).ConfigureAwait(false);

            if (finished != probe)
            {
                // The probe is left running rather than abandoned mid-call; there is no way to
                // cancel a blocked existence check, and it will finish on its own and be ignored.
                ct.ThrowIfCancellationRequested();
                return new DriveReport(drive.Root, DriveState.NotResponding, drive.WatchFolders, drive.FilmCount);
            }

            var result = await probe.ConfigureAwait(false);

            return result.IsAvailable
                ? new DriveReport(drive.Root, DriveState.Connected, drive.WatchFolders, drive.FilmCount,
                    result.Label, result.TotalBytes, result.FreeBytes)
                : new DriveReport(drive.Root, DriveState.Disconnected, drive.WatchFolders, drive.FilmCount);
        }
    }
}
