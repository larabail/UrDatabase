using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Bringing an existing library up to the current shape.
    ///
    /// Every statement in the schema script is <c>CREATE ... IF NOT EXISTS</c>, which is right for
    /// a fresh install and does nothing whatever for one that already exists. A column added to a
    /// table in that script therefore never appears in any real user's database — the table is
    /// already there, so the statement is skipped — and the app then fails on "no such column"
    /// against a database it has just declared up to date. Anybody who had ever synced a Jellyfin
    /// server would have hit exactly that.
    ///
    /// Tested against a real SQLite file, because the thing being tested is what SQLite does.
    /// </summary>
    public class DatabaseMigrationTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public DatabaseMigrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-mig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// The jellyfin_movies table exactly as it shipped before cast and crew existed.
        /// </summary>
        private void CreateOldLibrary()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE jellyfin_movies (
    item_id          TEXT PRIMARY KEY,
    title            TEXT NOT NULL,
    year             INTEGER,
    genres           TEXT,
    overview         TEXT,
    runtime_minutes  INTEGER,
    community_rating REAL,
    imdb_id          TEXT,
    tmdb_id          TEXT,
    image_tag        TEXT,
    synced_at        TEXT NOT NULL
);
INSERT INTO jellyfin_movies (item_id, title, year, synced_at)
VALUES ('abc', 'Ran', 1985, '2026-01-01T00:00:00.0000000Z');";
            cmd.ExecuteNonQuery();
        }

        [Fact]
        public void An_existing_library_gains_the_columns_it_was_built_without()
        {
            CreateOldLibrary();

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "cast_list"));
            Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "crew_list"));
        }

        [Fact]
        public void Migrating_an_existing_library_keeps_the_films_already_in_it()
        {
            CreateOldLibrary();

            using var conn = Database.Open(_dbPath);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT title FROM jellyfin_movies WHERE item_id = 'abc'";

            Assert.Equal("Ran", cmd.ExecuteScalar() as string);
        }

        /// <summary>
        /// The app opens the database on nearly every action, so the migration runs constantly.
        /// </summary>
        [Fact]
        public void Opening_the_same_library_repeatedly_is_harmless()
        {
            CreateOldLibrary();

            for (var i = 0; i < 3; i++)
            {
                using var conn = Database.Open(_dbPath);
                Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "cast_list"));
            }
        }

        /// <summary>
        /// The films half of the same database, as it shipped before a scan could tell that one
        /// had gone. Separate from <see cref="CreateOldLibrary"/> because these are the tables a
        /// person who never touched Jellyfin still has, and they are the ones with their whole
        /// collection in them.
        /// </summary>
        private void CreateOldFilmLibrary()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE movies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT    NOT NULL,
    year        INTEGER,
    genres      TEXT,
    poster_path TEXT
);
CREATE TABLE files (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    movie_id   INTEGER REFERENCES movies(id) ON DELETE SET NULL,
    file_path  TEXT NOT NULL,
    size_bytes INTEGER,
    created_at TEXT,
    updated_at TEXT
);
CREATE UNIQUE INDEX ux_files_path ON files(file_path);
INSERT INTO movies (id, title, year, genres, poster_path)
VALUES (1, 'Ran', 1985, 'Drama', '/posters/ran.jpg');
INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at)
VALUES (1, '/films/Ran (1985).mkv', 1234, '2020-01-01T00:00:00.0000000', '2020-01-02T00:00:00.0000000');";
            cmd.ExecuteNonQuery();
        }

        [Fact]
        public void A_film_library_gains_the_scan_lifecycle_columns()
        {
            CreateOldFilmLibrary();

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "files", "last_seen_at"));
            Assert.True(Database.ColumnExists(conn, "files", "last_seen_scan_id"));
            Assert.True(Database.ColumnExists(conn, "files", "missing_since"));
        }

        [Fact]
        public void Migrating_a_film_library_keeps_every_film_and_every_file()
        {
            // The one that matters. Somebody's catalogue is the reason the app exists, and a
            // migration that rebuilt the table to add a column would be the way to lose it.
            CreateOldFilmLibrary();

            using var conn = Database.Open(_dbPath);
            using var cmd = conn.CreateCommand();

            cmd.CommandText =
                "SELECT m.title || '|' || m.genres || '|' || m.poster_path || '|' || f.file_path || '|' || f.size_bytes " +
                "FROM files f JOIN movies m ON m.id = f.movie_id";

            Assert.Equal("Ran|Drama|/posters/ran.jpg|/films/Ran (1985).mkv|1234", cmd.ExecuteScalar() as string);
        }

        [Fact]
        public void A_migrated_file_row_is_neither_seen_nor_missing_until_a_scan_says_so()
        {
            // NULL is the honest value: no scan has looked at this row yet. Defaulting either way
            // would claim a scan that never ran had seen the file, or mark a whole catalogue
            // missing on the strength of an upgrade.
            CreateOldFilmLibrary();

            using var conn = Database.Open(_dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files WHERE last_seen_at IS NULL AND missing_since IS NULL";

            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        [Fact]
        public void A_film_library_gains_the_table_a_scan_records_itself_in()
        {
            // A new table, unlike a new column, is something CREATE TABLE IF NOT EXISTS handles
            // correctly — but only if it is actually in the script, so this is worth pinning.
            CreateOldFilmLibrary();

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.TableExists(conn, "scans"));
        }

        [Fact]
        public async Task A_scan_of_a_migrated_film_library_works_like_any_other()
        {
            // End to end, because a column that exists and a scan that runs are separate claims.
            CreateOldFilmLibrary();

            var films = Path.Combine(_dir, "Films");
            Directory.CreateDirectory(films);
            File.WriteAllText(Path.Combine(films, "Seven Samurai (1954).mkv"), "x");

            using var conn = Database.Open(_dbPath);
            var result = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(1, result.Inserted);
            Assert.True(result.ScanId > 0);

            // The row carried over from the old database names a path that was never under the
            // folder just scanned, so the scan has no business saying anything about it.
            Assert.Equal(0, result.Missing);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }

        /// <summary>
        /// The migration under the concurrency it actually runs under.
        ///
        /// <c>AddColumnIfMissing</c> inspects the table and then alters it, which is check-then-act:
        /// two connections can both read the old shape before either has committed, and the loser's
        /// <c>ALTER</c> fails with "duplicate column name". That is <c>SQLITE_ERROR</c>, not
        /// <c>SQLITE_BUSY</c>, so the write lane does not retry it and it reaches whoever asked for
        /// the database.
        ///
        /// This is the arrangement a real install produces rather than a contrived one. The poster
        /// loader opens the catalogue from four tasks at once, and on a machine with no Jellyfin
        /// server nothing has migrated before them — the read path uses <c>Connect</c>, which does
        /// not migrate, and the cache load returns without opening anything. So the first thing
        /// ever to migrate such a library is several poster fetches racing, on the first launch
        /// after an upgrade.
        ///
        /// A test that only opened the database once would pass against the broken version, which
        /// is exactly why this one opens it many times at the same instant.
        /// </summary>
        [Fact]
        public void A_library_migrated_by_many_connections_at_once_still_upgrades_exactly_once()
        {
            CreateOldFilmLibrary();

            const int racers = 8;

            // Real threads and a barrier, not pooled tasks. Tasks queued on the thread pool ramp
            // up one at a time and end up politely serialising, at which point every one after the
            // first finds the work already done and the race never happens — a test that passes
            // against the broken code and proves nothing.
            using var gate = new Barrier(racers);
            var failures = new List<Exception>();
            var threads = new Thread[racers];

            for (var i = 0; i < racers; i++)
            {
                threads[i] = new Thread(() =>
                {
                    gate.SignalAndWait();

                    try
                    {
                        using var conn = Database.Open(_dbPath);
                        Assert.True(Database.ColumnExists(conn, "files", "last_seen_at"));
                    }
                    catch (Exception ex)
                    {
                        lock (failures) failures.Add(ex);
                    }
                });

                threads[i].Start();
            }

            foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

            Assert.True(
                failures.Count == 0,
                $"{failures.Count} of {racers} concurrent migrations failed: "
                + string.Join(" | ", failures.Select(f => f.Message)));

            // And the column exists exactly once rather than having been added repeatedly.
            using var conn = Database.Open(_dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(files)";

            var seen = 0;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), "last_seen_at", StringComparison.OrdinalIgnoreCase))
                    seen++;

            Assert.Equal(1, seen);
        }


        /// <summary>
        /// The check-then-act window itself, with nothing else serialising it.
        ///
        /// Going through <c>Database.Open</c> mostly hides this: the schema script runs first and
        /// takes the write lock, so connections tend to queue and each one after the first finds
        /// the column already committed. The window is still there, and this is what is inside it.
        /// </summary>
        [Fact]
        public void Two_connections_adding_one_column_at_the_same_moment_do_not_fight()
        {
            CreateOldFilmLibrary();

            const int racers = 8;
            using var gate = new Barrier(racers);

            var failures = new List<Exception>();
            var connections = new List<SqliteConnection>();
            var threads = new Thread[racers];

            try
            {
                for (var i = 0; i < racers; i++)
                {
                    // Connect rather than Open: Connect lays down no schema, so the only thing
                    // these threads contend over is the ALTER this test is about.
                    var conn = Database.Connect(_dbPath);
                    lock (connections) connections.Add(conn);

                    threads[i] = new Thread(() =>
                    {
                        gate.SignalAndWait();

                        try
                        {
                            Database.AddColumnIfMissing(conn, "files", "last_seen_at", "TEXT");
                        }
                        catch (Exception ex)
                        {
                            lock (failures) failures.Add(ex);
                        }
                    });

                    threads[i].Start();
                }

                foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

                Assert.True(
                    failures.Count == 0,
                    $"{failures.Count} of {racers} racing migrations failed: "
                    + string.Join(" | ", failures.Select(f => f.Message)));

                Assert.True(Database.ColumnExists(connections[0], "files", "last_seen_at"));
            }
            finally
            {
                foreach (var conn in connections) conn.Dispose();
            }
        }

        /// <summary>
        /// And the other half of the bargain: the guard treats a lost race as success, not every
        /// failure as success. A column SQLite refuses outright leaves the table as it was, so it
        /// has to be reported rather than buried — otherwise a genuine mistake in a future
        /// migration would look like it had been applied.
        /// </summary>
        [Fact]
        public void A_column_sqlite_refuses_is_still_reported()
        {
            CreateOldFilmLibrary();

            using var conn = Database.Open(_dbPath);

            // SQLite cannot add a NOT NULL column with no default to a table that has rows in it.
            Assert.Throws<SqliteException>(
                () => Database.AddColumnIfMissing(conn, "files", "demanded", "TEXT NOT NULL"));

            Assert.False(Database.ColumnExists(conn, "files", "demanded"));
        }

        [Fact]
        public void A_library_created_from_nothing_already_has_the_columns()
        {
            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "cast_list"));
            Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "crew_list"));
            Assert.True(Database.ColumnExists(conn, "files", "last_seen_at"));
            Assert.True(Database.ColumnExists(conn, "files", "last_seen_scan_id"));
            Assert.True(Database.ColumnExists(conn, "files", "missing_since"));
            Assert.True(Database.ColumnExists(conn, "movies", "tmdb_id"));
        }

        /// <summary>
        /// Which TMDB film a catalogued one is. A library built before the column existed has to
        /// gain it, or the details screen fails on "no such column" against a database the app has
        /// just declared up to date — and the film it names has to still be there afterwards.
        /// </summary>
        [Fact]
        public void An_existing_movies_table_gains_the_column_that_records_the_tmdb_match()
        {
            using (var old = new SqliteConnection($"Data Source={_dbPath}"))
            {
                old.Open();

                using var cmd = old.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE movies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT NOT NULL,
    year        INTEGER,
    genres      TEXT,
    poster_path TEXT
);
INSERT INTO movies (title, year, poster_path) VALUES ('El Drama', 2026, '/wrong.jpg');";
                cmd.ExecuteNonQuery();
            }

            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "movies", "tmdb_id"));

            using var read = conn.CreateCommand();
            read.CommandText = "SELECT poster_path FROM movies WHERE id = 1";
            Assert.Equal("/wrong.jpg", read.ExecuteScalar() as string);
        }

        /// <summary>
        /// Database.cs carries a copy of the schema for a publish layout that did not copy the
        /// file beside it. Two descriptions of one schema drift the moment somebody edits one of
        /// them, and the failure lands on a trimmed release build that cannot open its own
        /// catalogue — which is nobody's development machine.
        /// </summary>
        [Fact]
        public void The_embedded_schema_and_the_file_describe_the_same_database()
        {
            using var fromFile = Database.Open(_dbPath);

            using var fromEmbedded = Database.Connect(Path.Combine(_dir, "embedded.db"));
            using (var cmd = fromEmbedded.CreateCommand())
            {
                var field = typeof(Database).GetField(
                    "EmbeddedSchema",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.NotNull(field);
                cmd.CommandText = (string)field!.GetRawConstantValue()!;
                cmd.ExecuteNonQuery();
            }

            Assert.Equal(Shape(fromFile), Shape(fromEmbedded));
        }

        /// <summary>Every table and column a database has, as one comparable string.</summary>
        private static string Shape(SqliteConnection conn)
        {
            var lines = new List<string>();

            using var tables = conn.CreateCommand();
            tables.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

            var names = new List<string>();
            using (var reader = tables.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));

            foreach (var name in names)
            {
                using var columns = conn.CreateCommand();
                columns.CommandText = $"PRAGMA table_info({name})";

                var found = new List<string>();
                using var reader = columns.ExecuteReader();
                while (reader.Read()) found.Add(reader.GetString(1));

                lines.Add(name + ": " + string.Join(", ", found));
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// The end of the whole point: a film synced into a database that predates the columns
        /// still comes back with its cast.
        /// </summary>
        [Fact]
        public void Cast_and_crew_survive_a_sync_into_a_migrated_library()
        {
            CreateOldLibrary();

            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new[]
            {
                new JellyfinMovie
                {
                    ItemId = "xyz",
                    Title = "2001: A Space Odyssey",
                    Year = 1968,
                    Cast = new List<string> { "Keir Dullea (Dave Bowman)", "Douglas Rain" },
                    Crew = new List<string> { "Director: Stanley Kubrick" }
                }
            });

            var loaded = Assert.Single(JellyfinCache.Load(conn));

            Assert.Equal(new[] { "Keir Dullea (Dave Bowman)", "Douglas Rain" }, loaded.Cast);
            Assert.Equal(new[] { "Director: Stanley Kubrick" }, loaded.Crew);
        }

        [Fact]
        public void A_film_with_no_credits_comes_back_with_empty_lists_rather_than_null()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new[]
            {
                new JellyfinMovie { ItemId = "xyz", Title = "Ran", Year = 1985 }
            });

            var loaded = Assert.Single(JellyfinCache.Load(conn));

            Assert.NotNull(loaded.Cast);
            Assert.NotNull(loaded.Crew);
            Assert.Empty(loaded.Cast);
            Assert.Empty(loaded.Crew);
        }

        /// <summary>
        /// Credits are stored one per line, so a name containing the separator would come back as
        /// two people. Nothing in a name should, but this is the assumption worth pinning.
        /// </summary>
        [Fact]
        public void A_credit_list_round_trips_without_being_split_on_anything_but_lines()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new[]
            {
                new JellyfinMovie
                {
                    ItemId = "xyz",
                    Title = "Test",
                    Cast = new List<string> { "Name, With A Comma (A Part: With A Colon)" }
                }
            });

            var loaded = Assert.Single(JellyfinCache.Load(conn));

            Assert.Equal(new[] { "Name, With A Comma (A Part: With A Colon)" }, loaded.Cast);
        }
    }
}
