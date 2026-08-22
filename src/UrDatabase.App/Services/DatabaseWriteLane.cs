using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// One writer at a time per catalogue file, and a bounded retry around the lock failures that
    /// survive it.
    ///
    /// SQLite permits exactly one writer at a time, and this app runs several. A scan writes in
    /// batches, a Jellyfin sync replaces the cached server library in one transaction, and the
    /// poster loader writes from up to four tasks at once. Left to collide they spend their time
    /// waiting on each other's locks, and the write that eventually loses is reported as
    /// "database is locked" — which reaches the owner as a missing poster or a scan that appears
    /// to have done nothing.
    ///
    /// A busy timeout alone does not fix that. It converts an instant failure into a wait, which
    /// is strictly better, but under sustained write pressure the waits queue up in no particular
    /// order and the longest-suffering write is the one that fails. Taking a turn here instead
    /// means the writers in this process never contend for the SQLite lock at all: whoever holds
    /// the lane holds it alone, and the busy timeout is left to handle the only contention it
    /// cannot see, which is a second copy of the app open on the same file.
    ///
    /// The retry is the backstop for exactly that case, and it is deliberately finite. A write
    /// that still cannot be made after it is reported to the caller rather than dropped: the
    /// whole complaint in the original bug is about failures nobody was told about.
    /// </summary>
    public static class DatabaseWriteLane
    {
        /// <summary>The database is locked by another connection. <c>SQLITE_BUSY</c>.</summary>
        public const int SqliteBusy = 5;

        /// <summary>A table in the database is locked. <c>SQLITE_LOCKED</c>.</summary>
        public const int SqliteLocked = 6;

        /// <summary>
        /// Attempts, not retries: three means the write is tried three times in total. Each one
        /// can wait <see cref="Database.LockWaitSeconds"/> for the lock, so this is also the
        /// ceiling on how long a blocked write can take before it is reported.
        /// </summary>
        public const int DefaultMaxAttempts = 3;

        /// <summary>
        /// Waited before the second attempt, and doubled for each one after it. Short, because
        /// the lock wait inside the attempt is where the real waiting happens; this only exists
        /// so that two writers that lost to each other do not line up and collide again.
        /// </summary>
        public static readonly TimeSpan DefaultFirstDelay = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// Keyed by what SQLite says it opened rather than by the path a caller asked for, so a
        /// relative path and an absolute one cannot end up queueing in two different lanes and
        /// believing they are serialised.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Lanes =
            new(StringComparer.Ordinal);

        /// <summary>
        /// True when <paramref name="ex"/> is SQLite reporting a lock rather than a mistake.
        /// Matched on the result code: the message is localised and has changed between provider
        /// versions, and an extended code such as <c>SQLITE_BUSY_SNAPSHOT</c> keeps the primary
        /// code in its low byte.
        /// </summary>
        public static bool IsTransientLockFailure(Exception? ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
            {
                if (e is not SqliteException sqlite) continue;

                if (sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked) return true;
                if ((sqlite.SqliteExtendedErrorCode & 0xFF) is SqliteBusy or SqliteLocked) return true;
            }

            return false;
        }

        /// <summary>
        /// Runs <paramref name="write"/> with the lane held, retrying it while it fails on a lock.
        /// </summary>
        /// <exception cref="SqliteException">
        /// The write was still locked out after <paramref name="maxAttempts"/>. Deliberately not
        /// caught here — the caller is the only one that knows how to tell somebody.
        /// </exception>
        public static async Task<T> RunAsync<T>(
            SqliteConnection conn,
            Func<CancellationToken, Task<T>> write,
            CancellationToken ct = default,
            int maxAttempts = DefaultMaxAttempts,
            TimeSpan? firstDelay = null)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (write is null) throw new ArgumentNullException(nameof(write));
            if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));

            using var lease = await EnterAsync(conn, ct).ConfigureAwait(false);

            var delay = firstDelay ?? DefaultFirstDelay;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await write(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientLockFailure(ex))
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay += delay;
                }
            }
        }

        /// <summary>Runs <paramref name="write"/> with the lane held. See the overload above.</summary>
        public static async Task RunAsync(
            SqliteConnection conn,
            Func<CancellationToken, Task> write,
            CancellationToken ct = default,
            int maxAttempts = DefaultMaxAttempts,
            TimeSpan? firstDelay = null)
        {
            if (write is null) throw new ArgumentNullException(nameof(write));

            await RunAsync<object?>(
                conn,
                async token => { await write(token).ConfigureAwait(false); return null; },
                ct,
                maxAttempts,
                firstDelay).ConfigureAwait(false);
        }

        /// <summary>
        /// Takes a turn in the lane and holds it until the returned handle is disposed. For a
        /// caller that owns its own transaction and therefore cannot simply be re-run — the scan,
        /// which commits in batches and must let go between them.
        /// </summary>
        public static async Task<IDisposable> EnterAsync(SqliteConnection conn, CancellationToken ct = default)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var lane = LaneFor(conn);
            await lane.WaitAsync(ct).ConfigureAwait(false);
            return new Lease(lane);
        }

        /// <summary>
        /// The lane a connection belongs to. Internal so a test can prove two connections opened
        /// on one catalogue really do share a lane rather than each politely queueing alone.
        /// </summary>
        internal static string KeyFor(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            // What SQLite resolved the file to, once open. It reports the empty string for an
            // in-memory database, which is a single shared lane and is the right answer for one.
            return conn.DataSource ?? string.Empty;
        }

        private static SemaphoreSlim LaneFor(SqliteConnection conn)
            => Lanes.GetOrAdd(KeyFor(conn), _ => new SemaphoreSlim(1, 1));

        /// <summary>
        /// Releases once however many times it is disposed. The scan swaps its lease at every
        /// batch boundary and still disposes in a <c>finally</c>, so the same handle can be
        /// disposed twice on the way out of a cancelled scan.
        /// </summary>
        private sealed class Lease : IDisposable
        {
            private SemaphoreSlim? _lane;

            public Lease(SemaphoreSlim lane) => _lane = lane;

            public void Dispose() => Interlocked.Exchange(ref _lane, null)?.Release();
        }
    }
}
