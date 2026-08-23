using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The catalogue is written by a scan, by a Jellyfin sync and by up to four poster loaders at
    /// once, and SQLite allows one writer at a time. These cover what stops that from reaching
    /// somebody as a missing poster or a scan that appears to have done nothing.
    ///
    /// Against a real database file in a temporary directory rather than a mock. The behaviour
    /// under test is SQLite's own locking, and a mock of it would only assert what the test author
    /// already believed.
    /// </summary>
    public class DatabaseConcurrencyTests : IDisposable
    {
        private readonly string _dir;

        public DatabaseConcurrencyTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-concurrency-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        private string DbPath => Path.Combine(_dir, "movies.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ---- Every connection is configured the same way -------------------------------------

        [Fact]
        public void Connect_applies_the_busy_timeout_and_the_wal_journal()
        {
            using var conn = Database.Connect(DbPath);

            Assert.Equal(Database.BusyTimeoutMilliseconds, conn.ExecuteScalar<long>("PRAGMA busy_timeout"));
            Assert.Equal("wal", conn.ExecuteScalar<string>("PRAGMA journal_mode"));
            Assert.Equal(1L, conn.ExecuteScalar<long>("PRAGMA foreign_keys"));
        }

        [Fact]
        public void Open_is_Connect_plus_the_schema()
        {
            using var conn = Database.Open(DbPath);

            Assert.Equal(Database.BusyTimeoutMilliseconds, conn.ExecuteScalar<long>("PRAGMA busy_timeout"));
            Assert.Equal("wal", conn.ExecuteScalar<string>("PRAGMA journal_mode"));
            Assert.True(TableExists(conn, "movies"));
        }

        /// <summary>
        /// The reason the two are separate. The window re-reads the library on every keystroke in
        /// the search box, and re-running the schema each time is work nobody asked for — but it
        /// still has to get the pragmas, which is what the split is for.
        /// </summary>
        [Fact]
        public void Connect_does_not_migrate_the_schema()
        {
            using var conn = Database.Connect(DbPath);

            Assert.False(TableExists(conn, "movies"));
        }

        [Fact]
        public void Connect_bounds_the_providers_own_lock_wait_as_well()
        {
            using var conn = Database.Connect(DbPath);

            // Microsoft.Data.Sqlite re-issues a busy statement every 150ms for the whole of its
            // command timeout, on top of the pragma above. Left at its thirty second default one
            // blocked write costs half a minute before it is allowed to fail, and the retry in
            // DatabaseWriteLane would multiply that rather than bound it.
            Assert.Equal(Database.LockWaitSeconds, conn.DefaultTimeout);
        }

        /// <summary>
        /// The bug this was opened for, stated as a fact about the thing that caused it. The
        /// window's read path built its own connection, and a connection built this way silently
        /// has no busy timeout at all — so it fails the moment anything else holds the lock,
        /// rather than waiting the moment out like every other connection in the app.
        /// </summary>
        [Fact]
        public void A_hand_built_connection_has_no_busy_timeout()
        {
            using var seeded = Database.Open(DbPath);

            using var byHand = new SqliteConnection($"Data Source={DbPath}");
            byHand.Open();

            Assert.Equal(0L, byHand.ExecuteScalar<long>("PRAGMA busy_timeout"));
            Assert.NotEqual(byHand.ExecuteScalar<long>("PRAGMA busy_timeout"),
                            seeded.ExecuteScalar<long>("PRAGMA busy_timeout"));
        }

        /// <summary>
        /// And the rule that keeps it from coming back. A divergence like the one above is
        /// invisible in review — the call site reads perfectly well and says nothing about the
        /// pragmas it is missing — so the only durable fix is that there is one way to open the
        /// catalogue and nothing else may do it.
        /// </summary>
        [Fact]
        public void Nothing_outside_Database_builds_its_own_catalogue_connection()
        {
            var root = RepositoryRoot();

            var offenders = Directory
                .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsBuildArtefact(root, file))
                .Where(file => !string.Equals(Path.GetFileName(file), "Database.cs", StringComparison.Ordinal))
                .Where(file => File.ReadAllText(file).Contains("new SqliteConnection", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file))
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                "These open a catalogue connection by hand and so miss the pragmas every other "
                + "connection gets. Use Database.Connect, or Database.Open when the schema may be "
                + "absent: " + string.Join(", ", offenders));
        }

        /// <summary>
        /// A connection string is a list of <c>key=value</c> pairs separated by semicolons, so the
        /// path used to be able to end the string early and mean something else entirely. Nobody
        /// would name a folder this on purpose; plenty of external drives arrive named worse.
        /// </summary>
        [Fact]
        public void A_catalogue_path_containing_a_semicolon_still_opens()
        {
            var awkward = Path.Combine(_dir, "films; and 'more'.db");

            using var conn = Database.Open(awkward);
            conn.Execute("INSERT INTO movies (title, year) VALUES ('Awkward', 2001)");

            Assert.Equal(1L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
            Assert.True(File.Exists(awkward));
        }

        // ---- The write lane ------------------------------------------------------------------

        [Fact]
        public void Two_connections_to_one_catalogue_share_a_lane()
        {
            using var first = Database.Open(DbPath);
            using var second = Database.Connect(Path.Combine(_dir, ".", "movies.db"));

            // Not an idle assertion: a lane keyed on the path a caller happened to pass would put
            // these two in separate queues, and every writer would be politely taking a turn on
            // its own while colliding with everybody else.
            Assert.Equal(DatabaseWriteLane.KeyFor(first), DatabaseWriteLane.KeyFor(second));
        }

        [Fact]
        public async Task A_lock_failure_is_retried_until_it_clears()
        {
            using var conn = Database.Open(DbPath);
            var attempts = 0;

            var result = await DatabaseWriteLane.RunAsync(
                conn,
                _ =>
                {
                    if (++attempts < 3) throw Locked(DatabaseWriteLane.SqliteBusy);
                    return Task.FromResult(attempts);
                },
                firstDelay: TimeSpan.FromMilliseconds(1));

            Assert.Equal(3, result);
        }

        /// <summary>
        /// The half of the original complaint that is not about locking at all: a failure nobody
        /// was told about. The retry is finite, and what survives it is the caller's problem to
        /// report rather than the lane's to drop.
        /// </summary>
        [Fact]
        public async Task A_lock_failure_that_outlasts_the_retries_is_reported()
        {
            using var conn = Database.Open(DbPath);
            var attempts = 0;

            var thrown = await Assert.ThrowsAsync<SqliteException>(() => DatabaseWriteLane.RunAsync(
                conn,
                _ =>
                {
                    attempts++;
                    throw Locked(DatabaseWriteLane.SqliteBusy);
                },
                maxAttempts: 3,
                firstDelay: TimeSpan.FromMilliseconds(1)));

            Assert.Equal(DatabaseWriteLane.SqliteBusy, thrown.SqliteErrorCode);
            Assert.Equal(3, attempts);
        }

        [Fact]
        public async Task A_failure_that_is_not_a_lock_is_not_retried()
        {
            using var conn = Database.Open(DbPath);
            var attempts = 0;

            await Assert.ThrowsAsync<SqliteException>(() => DatabaseWriteLane.RunAsync(
                conn,
                _ =>
                {
                    attempts++;
                    // SQLITE_CONSTRAINT. Retrying a mistake only makes it later.
                    throw new SqliteException("constraint failed", 19);
                },
                firstDelay: TimeSpan.FromMilliseconds(1)));

            Assert.Equal(1, attempts);
        }

        [Theory]
        [InlineData(5, 0, true)]    // SQLITE_BUSY
        [InlineData(6, 0, true)]    // SQLITE_LOCKED
        [InlineData(5, 517, true)]  // SQLITE_BUSY_SNAPSHOT keeps the primary code in its low byte
        [InlineData(19, 0, false)]  // SQLITE_CONSTRAINT
        [InlineData(1, 0, false)]   // SQLITE_ERROR
        public void A_lock_is_recognised_by_its_result_code(int code, int extended, bool transient)
        {
            Assert.Equal(transient, DatabaseWriteLane.IsTransientLockFailure(new SqliteException("x", code, extended)));
        }

        [Fact]
        public void A_lock_wrapped_in_another_exception_is_still_recognised()
        {
            var wrapped = new InvalidOperationException("could not save", Locked(DatabaseWriteLane.SqliteLocked));

            Assert.True(DatabaseWriteLane.IsTransientLockFailure(wrapped));
            Assert.False(DatabaseWriteLane.IsTransientLockFailure(new InvalidOperationException("no")));
            Assert.False(DatabaseWriteLane.IsTransientLockFailure(null));
        }

        [Fact]
        public async Task Writers_take_turns_rather_than_colliding()
        {
            const int writers = 8;
            using (var schema = Database.Open(DbPath)) { }

            var inFlight = 0;
            var peak = 0;

            var connections = Enumerable.Range(0, writers).Select(_ => Database.Connect(DbPath)).ToList();
            try
            {
                await Task.WhenAll(connections.Select((conn, i) => Task.Run(() => DatabaseWriteLane.RunAsync(
                    conn,
                    async _ =>
                    {
                        RecordPeak(ref peak, Interlocked.Increment(ref inFlight));
                        try
                        {
                            // Wide enough that eight tasks starting together would overlap if
                            // nothing were serialising them.
                            await Task.Delay(20);
                            await conn.ExecuteAsync(
                                "INSERT INTO movies (title, year) VALUES (@title, 2000)",
                                new { title = $"Film {i}" });
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inFlight);
                        }
                    }))));
            }
            finally
            {
                foreach (var conn in connections) conn.Dispose();
            }

            Assert.Equal(1, peak);

            using var verify = Database.Connect(DbPath);
            Assert.Equal((long)writers, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
        }

        /// <summary>
        /// The failure the bug report describes, reproduced and then survived: something else is
        /// part way through a write transaction when this one starts.
        /// </summary>
        [Fact]
        public async Task A_write_waits_out_a_transaction_held_by_another_connection()
        {
            using var holder = Database.Open(DbPath);
            using var tx = holder.BeginTransaction();
            holder.Execute("INSERT INTO movies (title, year) VALUES ('Held', 2001)", transaction: tx);

            using var writer = Database.Connect(DbPath);
            var competing = Task.Run(() => DatabaseWriteLane.RunAsync(
                writer,
                _ => writer.ExecuteAsync("INSERT INTO movies (title, year) VALUES ('Competing', 2002)")));

            // Long enough to be sure it is blocked on the holder rather than racing it to the lock.
            await Task.Delay(250);
            Assert.False(competing.IsCompleted);

            tx.Commit();

            Assert.Equal(1, await competing.WaitAsync(TimeSpan.FromSeconds(30)));

            using var verify = Database.Connect(DbPath);
            Assert.Equal(2L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
        }

        /// <summary>
        /// The two writers the app actually runs together. A scan commits in batches for the whole
        /// length of a library while the poster loader writes a row at a time behind it, and
        /// before this neither took a turn: they simply collided and whichever lost was reported
        /// as "database is locked".
        /// </summary>
        [Fact]
        public async Task A_scan_and_the_writes_behind_it_both_finish()
        {
            var folder = Path.Combine(_dir, "films");
            Directory.CreateDirectory(folder);
            for (var i = 0; i < 250; i++)
                File.WriteAllText(Path.Combine(folder, $"Film {i} ({2000 + i % 20}).mkv"), "x");

            using (var schema = Database.Open(DbPath)) { }

            using var cts = new CancellationTokenSource();
            using var behind = Database.Connect(DbPath);

            var written = 0;
            var alongside = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await DatabaseWriteLane.RunAsync(behind, async _ =>
                    {
                        await behind.ExecuteAsync(
                            "INSERT INTO jellyfin_movies (item_id, title, synced_at) VALUES (@id, 'Server film', 'now')",
                            new { id = Guid.NewGuid().ToString("N") });
                    }, CancellationToken.None);

                    written++;
                }
            });

            var scanned = await ScanService.ScanLibraryAsync(DbPath, new[] { folder });

            cts.Cancel();
            await alongside;

            Assert.Equal(250, scanned.Inserted);
            Assert.True(written > 0, "the competing writer never got a turn, so this proved nothing");

            using var verify = Database.Connect(DbPath);
            Assert.Equal(250L, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal((long)written, verify.ExecuteScalar<long>("SELECT COUNT(*) FROM jellyfin_movies"));
        }

        // ---- helpers -------------------------------------------------------------------------

        private static SqliteException Locked(int code) => new("database is locked", code);

        private static bool TableExists(SqliteConnection conn, string name)
            => conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name",
                new { name }) > 0;

        private static void RecordPeak(ref int peak, int observed)
        {
            int seen;
            while (observed > (seen = Volatile.Read(ref peak)))
            {
                if (Interlocked.CompareExchange(ref peak, observed, seen) == seen) return;
            }
        }

        private static bool IsBuildArtefact(string root, string file)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            return relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Walks up from the test assembly looking for the solution file. The tests run out of
        /// <c>tests/UrDatabase.Tests/bin/…</c>, so the repository is always above them.
        /// </summary>
        private static string RepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "UrDatabase.sln"))) return dir.FullName;
            }

            throw new InvalidOperationException(
                $"No UrDatabase.sln above {AppContext.BaseDirectory}, so the source tree could not be checked.");
        }
    }
}
