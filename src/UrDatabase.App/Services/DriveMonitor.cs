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
    /// Checks every drive — the ones plugged in, the ones remembered from earlier launches and the
    /// ones the library lives on — and says which of them are not there.
    ///
    /// The point is a disk that has died, or simply been left unplugged, being noticed when the
    /// app opens rather than when somebody presses Play on a film and the operating system refuses
    /// the path. Nothing in the library changes as a result: a drive that is away is reported, not
    /// acted on. The one thing written is the list of disks seen, which is what lets a dead one be
    /// missed at all — the operating system does not list a disk that has died.
    /// </summary>
    public sealed class DriveMonitor
    {
        /// <summary>
        /// Long enough for a sleeping USB disk to spin up, short enough that a vanished network
        /// share does not leave the answer hanging for the half-minute Windows will wait on it.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

        private readonly IDriveProbe _probe;
        private readonly IDriveEnumerator _drives;
        private readonly TimeSpan _timeout;
        private readonly Func<DateTime> _clock;

        public DriveMonitor(
            IDriveProbe? probe = null,
            TimeSpan? timeout = null,
            IDriveEnumerator? drives = null,
            Func<DateTime>? clock = null)
        {
            _probe = probe ?? FileSystemDriveProbe.Instance;
            _drives = drives ?? SystemDriveEnumerator.Instance;
            _timeout = timeout ?? DefaultTimeout;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        /// <summary>
        /// Probes the given drives at once, each on a thread of its own and each with its own time
        /// limit, so one dead share cannot hold up the verdict on the others. Never throws except
        /// to report cancellation.
        /// </summary>
        public async Task<IReadOnlyList<DriveReport>> CheckAsync(
            IEnumerable<DriveUsage> drives,
            CancellationToken ct = default)
        {
            var list = drives.ToList();
            var outcomes = await Task.WhenAll(list.Select(d => ProbeAsync(d.Root, ct))).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            return list.Select((drive, i) =>
            {
                var outcome = outcomes[i];
                return outcome.State == DriveState.Connected
                    ? new DriveReport(drive.Root, DriveState.Connected, drive.WatchFolders, drive.FilmCount,
                        outcome.Result.Label, outcome.Result.TotalBytes, outcome.Result.FreeBytes)
                    : new DriveReport(drive.Root, outcome.State, drive.WatchFolders, drive.FilmCount);
            }).ToList();
        }

        /// <summary>
        /// The whole check: lists what is plugged in, reads the library's drives and the remembered
        /// disks out of the catalogue, probes everything, and records the disks it saw. Runs on the
        /// thread pool from the first line, so calling it from the UI thread costs nothing.
        /// </summary>
        /// <param name="watchFolders">The configured library folders.</param>
        /// <param name="databasePath">
        /// The catalogue. When it does not exist yet the check still runs, but remembers nothing:
        /// a check of the drives must never be the thing that creates a catalogue.
        /// </param>
        public Task<IReadOnlyList<DriveReport>> CheckDrivesAsync(
            IEnumerable<string>? watchFolders,
            string? databasePath,
            CancellationToken ct = default)
        {
            var folders = (watchFolders ?? Array.Empty<string>()).ToArray();

            return Task.Run(async () =>
            {
                var usages = DriveVolumes.Collect(folders, ReadCataloguedFiles(databasePath));
                var known = ReadKnownDrives(databasePath);
                var mounted = await EnumerateAsync(ct).ConfigureAwait(false);

                var roots = mounted.Select(v => v.Root)
                    .Concat(usages.Select(u => u.Root))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var outcomes = await Task.WhenAll(roots.Select(r => ProbeAsync(r, ct))).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var probes = new Dictionary<string, DriveProbeOutcome>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < roots.Count; i++) probes[roots[i]] = outcomes[i];

                var canRemember = CatalogueExists(databasePath);
                var inventory = DriveInventory.Build(mounted, probes, usages, known, canRemember, _clock());

                if (canRemember && inventory.Seen.Count > 0)
                    await RememberAsync(databasePath!, inventory.Seen, ct).ConfigureAwait(false);

                return inventory.Reports;
            }, ct);
        }

        /// <summary>
        /// Stops missing a disk the owner has retired. Throws on failure, so the dialog that asked
        /// can say it did not happen.
        /// </summary>
        public static async Task ForgetAsync(string databasePath, string id, CancellationToken ct = default)
        {
            if (!CatalogueExists(databasePath)) return;

            await Task.Run(async () =>
            {
                using var conn = Database.Open(databasePath);
                await DatabaseWriteLane.RunAsync(conn, _ =>
                {
                    KnownDriveStore.Forget(conn, id);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Every file the catalogue believes is still present, with the film it belongs to. Empty,
        /// rather than an error, when there is no catalogue yet: a check of the drives must never
        /// be the thing that creates one, and must never be the thing that stops the app.
        /// </summary>
        public static IReadOnlyList<(string Path, long? MovieId)> ReadCataloguedFiles(string? databasePath)
        {
            if (!CatalogueExists(databasePath))
                return Array.Empty<(string, long?)>();

            try
            {
                using var conn = Database.Connect(databasePath!);
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

        /// <summary>
        /// The disks remembered from earlier launches. Read without laying the schema down, for the
        /// same reason as <see cref="ReadCataloguedFiles"/>; a catalogue that predates the table
        /// simply remembers nothing yet.
        /// </summary>
        public static IReadOnlyList<KnownDrive> ReadKnownDrives(string? databasePath)
        {
            if (!CatalogueExists(databasePath)) return Array.Empty<KnownDrive>();

            try
            {
                using var conn = Database.Connect(databasePath!);
                return Database.TableExists(conn, "known_drives")
                    ? KnownDriveStore.Load(conn)
                    : Array.Empty<KnownDrive>();
            }
            catch (Exception ex)
            {
                AppLog.Write("drives.log", $"could not read remembered drives: {ex.Message}");
                return Array.Empty<KnownDrive>();
            }
        }

        private static bool CatalogueExists(string? databasePath) =>
            !string.IsNullOrWhiteSpace(databasePath) && File.Exists(databasePath);

        /// <summary>
        /// Records the disks just seen. A failure costs the memory of this sighting and nothing
        /// else, so it is logged rather than allowed to take the report with it.
        /// </summary>
        private static async Task RememberAsync(string databasePath, IReadOnlyList<KnownDrive> seen, CancellationToken ct)
        {
            try
            {
                using var conn = Database.Open(databasePath);
                await DatabaseWriteLane.RunAsync(conn, _ =>
                {
                    KnownDriveStore.Remember(conn, seen);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("drives.log", $"could not remember drives: {ex.Message}");
            }
        }

        /// <summary>
        /// What the operating system lists, under the same time limit as a probe. Listing is
        /// normally instant, but it is a call into the operating system about drives, one of which
        /// may be the dead share this whole check exists for. An answer that never comes, or an
        /// error, is an empty list: the library's own drives are still probed by path.
        /// </summary>
        private async Task<IReadOnlyList<MountedVolume>> EnumerateAsync(CancellationToken ct)
        {
            var listing = Task.Factory.StartNew(
                () => _drives.Enumerate(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var finished = await Task.WhenAny(listing, Task.Delay(_timeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (finished != listing)
            {
                AppLog.Write("drives.log", "listing the drives did not finish in time");
                return Array.Empty<MountedVolume>();
            }

            try
            {
                return await listing.ConfigureAwait(false) ?? Array.Empty<MountedVolume>();
            }
            catch (Exception ex)
            {
                AppLog.Write("drives.log", $"could not list the drives: {ex.Message}");
                return Array.Empty<MountedVolume>();
            }
        }

        private async Task<DriveProbeOutcome> ProbeAsync(string root, CancellationToken ct)
        {
            // A thread of its own rather than the pool: the time limit starts now, so a probe
            // still queued behind other work would be called unresponsive before it had asked,
            // and one that hangs on a dead share would otherwise hold a pool thread indefinitely.
            var probe = Task.Factory.StartNew(() =>
            {
                try { return _probe.Probe(root); }
                catch { return DriveProbeResult.Unavailable; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var finished = await Task.WhenAny(probe, Task.Delay(_timeout, ct)).ConfigureAwait(false);

            if (finished != probe)
            {
                // The probe is left running rather than abandoned mid-call; there is no way to
                // cancel a blocked existence check, and it will finish on its own and be ignored.
                ct.ThrowIfCancellationRequested();
                return new DriveProbeOutcome(DriveState.NotResponding, DriveProbeResult.Unavailable);
            }

            var result = await probe.ConfigureAwait(false) ?? DriveProbeResult.Unavailable;

            return result.IsAvailable
                ? new DriveProbeOutcome(DriveState.Connected, result)
                : DriveProbeOutcome.Away;
        }
    }
}