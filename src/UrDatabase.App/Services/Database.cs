using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    public static class Database
    {
        /// <summary>
        /// How long SQLite itself waits for another connection to release the write lock before
        /// reporting the database as locked. Long enough to sit out a scan's batch commit, short
        /// enough that a lock nobody is going to release is reported rather than hung on.
        /// </summary>
        public const int BusyTimeoutMilliseconds = 5000;

        /// <summary>
        /// The second, separate lock wait that <c>Microsoft.Data.Sqlite</c> applies on top of the
        /// pragma above, in seconds. It bounds waiting for a lock only; it never interrupts a
        /// statement that is running.
        ///
        /// Left at the provider's thirty second default the two compound badly. The provider
        /// re-issues a busy statement every 150ms for the whole of its timeout, so a single
        /// blocked write costs half a minute before it is allowed to fail, and any retry around
        /// it multiplies that again. Matching it to the busy timeout makes one attempt cost one
        /// wait, which is what lets <see cref="DatabaseWriteLane"/> put a predictable ceiling on
        /// a write.
        /// </summary>
        public const int LockWaitSeconds = BusyTimeoutMilliseconds / 1000;

        /// <summary>
        /// Opens a connection to the catalogue configured the way every caller needs it, and does
        /// nothing else. Reads want this. Anything that could be the first thing to touch a fresh
        /// install wants <see cref="Open"/>, which also lays the schema down.
        ///
        /// Nothing outside this class may construct a <see cref="SqliteConnection"/> for the
        /// catalogue. The window's read path used to build its own, and so ran the most frequent
        /// query in the app on the one connection with no busy timeout — a divergence nothing at
        /// the call site declared and nothing would have caught.
        /// </summary>
        public static SqliteConnection Connect(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("A database path is required.", nameof(dbPath));

            var full = Path.GetFullPath(dbPath);
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Built rather than interpolated. A connection string is a list of `key=value` pairs
            // separated by semicolons, so a library kept somewhere with a semicolon or a quote in
            // its name silently produced a connection string meaning something else entirely.
            //
            // No shared cache, and WAL: under Cache=Shared SQLite takes table-level locks and
            // returns "database table is locked" to a reader immediately, ignoring any busy
            // timeout. A scan holds a write transaction, so the window would fail to read the
            // library — and render it as empty — for as long as one was running. In WAL a reader
            // sees the last committed snapshot instead and is never blocked by the writer.
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = full,
                DefaultTimeout = LockWaitSeconds
            }.ToString();

            var conn = new SqliteConnection(connectionString);
            conn.Open();

            try
            {
                using var pragma = conn.CreateCommand();
                pragma.CommandText =
                    "PRAGMA foreign_keys=ON; " +
                    $"PRAGMA busy_timeout={BusyTimeoutMilliseconds}; " +
                    "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            catch
            {
                conn.Dispose();
                throw;
            }

            return conn;
        }

        /// <summary>
        /// Opens (creating if needed) the catalogue database and makes sure the schema exists.
        /// A fresh macOS or Windows install has no database at all, so the app has to be able to
        /// build one instead of failing on "no such table".
        /// </summary>
        public static SqliteConnection Open(string dbPath)
        {
            var conn = Connect(dbPath);

            try
            {
                EnsureSchema(conn);
            }
            catch
            {
                conn.Dispose();
                throw;
            }

            return conn;
        }

        /// <summary>Applies <c>Data/schema.sql</c>. Idempotent; safe against an existing library.</summary>
        public static void EnsureSchema(SqliteConnection conn)
        {
            var sql = ReadSchemaScript();
            if (string.IsNullOrWhiteSpace(sql)) return;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            Migrate(conn);
        }

        /// <summary>
        /// Brings an existing database up to the current shape.
        /// </summary>
        /// <remarks>
        /// Every statement in the schema script is <c>CREATE ... IF NOT EXISTS</c>, which builds a
        /// correct database from nothing and does absolutely nothing to one that already exists.
        /// A column added to a table in that script therefore never appears in anybody's actual
        /// library — the table is already there, so the statement is skipped, and the app then
        /// fails on "no such column" against a database it just declared up to date.
        /// </remarks>
        internal static void Migrate(SqliteConnection conn)
        {
            // Cast and crew from a Jellyfin server. Text rather than a table of people: they are
            // only ever read back whole, for one film, to be printed.
            AddColumnIfMissing(conn, "jellyfin_movies", "cast_list", "TEXT");
            AddColumnIfMissing(conn, "jellyfin_movies", "crew_list", "TEXT");

            // What a scan knows about a file between one run and the next. Without these a scan
            // could only ever add: it had no way to tell a file it did not find from one it never
            // walked past, so a deleted film stayed in the catalogue for good.
            //
            // All three are nullable with no default, which is both what SQLite requires of a
            // column added in place and the honest starting value — a row from an older library
            // has genuinely not been looked at by any scan yet. Defaulting the other way would
            // either claim a scan that never ran had seen every file, or mark somebody's entire
            // catalogue missing the moment they upgraded.
            AddColumnIfMissing(conn, "files", "last_seen_at", "TEXT");
            AddColumnIfMissing(conn, "files", "last_seen_scan_id", "INTEGER");
            AddColumnIfMissing(conn, "files", "missing_since", "TEXT");
        }

        /// <summary>
        /// Adds a column when it is absent, and does nothing when it is already there. SQLite has
        /// no <c>ADD COLUMN IF NOT EXISTS</c>, so the table is inspected first.
        /// </summary>
        internal static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
        {
            if (!TableExists(conn, table)) return;
            if (ColumnExists(conn, table, column)) return;

            using var cmd = conn.CreateCommand();
            // The names here are compile-time constants from Migrate, never user input.
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
            cmd.ExecuteNonQuery();
        }

        internal static bool TableExists(SqliteConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1";
            cmd.Parameters.AddWithValue("@name", table);

            return cmd.ExecuteScalar() is not null;
        }

        internal static bool ColumnExists(SqliteConnection conn, string table, string column)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // Column 1 of table_info is the column's name.
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string ReadSchemaScript()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
            return File.Exists(path) ? File.ReadAllText(path) : EmbeddedSchema;
        }

        /// <summary>
        /// Used when the app runs from a layout where <c>Data/schema.sql</c> was not copied
        /// (for example a trimmed single-file publish).
        /// </summary>
        private const string EmbeddedSchema = @"
CREATE TABLE IF NOT EXISTS movies (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT    NOT NULL,
    year        INTEGER,
    genres      TEXT,
    poster_path TEXT
);
CREATE TABLE IF NOT EXISTS files (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    movie_id   INTEGER REFERENCES movies(id) ON DELETE SET NULL,
    file_path  TEXT NOT NULL,
    size_bytes INTEGER,
    created_at TEXT,
    updated_at TEXT,
    last_seen_at      TEXT,
    last_seen_scan_id INTEGER,
    missing_since     TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(file_path);
CREATE INDEX IF NOT EXISTS ix_files_movie ON files(movie_id);
CREATE TABLE IF NOT EXISTS scans (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at    TEXT NOT NULL,
    finished_at   TEXT,
    status        TEXT NOT NULL,
    roots         TEXT,
    skipped_roots TEXT,
    inserted      INTEGER NOT NULL DEFAULT 0,
    moved         INTEGER NOT NULL DEFAULT 0,
    updated       INTEGER NOT NULL DEFAULT 0,
    unchanged     INTEGER NOT NULL DEFAULT 0,
    failed        INTEGER NOT NULL DEFAULT 0,
    missing       INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS imdb_ratings (
    imdb_id    TEXT PRIMARY KEY,
    movie_id   INTEGER REFERENCES movies(id) ON DELETE SET NULL,
    rating     REAL,
    fetched_at TEXT NOT NULL,
    source     TEXT NOT NULL DEFAULT 'omdb'
);
CREATE TABLE IF NOT EXISTS jellyfin_movies (
    item_id          TEXT PRIMARY KEY,
    title            TEXT NOT NULL,
    year             INTEGER,
    genres           TEXT,
    overview         TEXT,
    runtime_minutes  INTEGER,
    community_rating REAL,
    imdb_id          TEXT,
    tmdb_id          TEXT,
    cast_list        TEXT,
    crew_list        TEXT,
    image_tag        TEXT,
    synced_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jellyfin_movies_title ON jellyfin_movies(title);
CREATE VIRTUAL TABLE IF NOT EXISTS movies_fts USING fts5(title, genres, content='movies', content_rowid='id');
CREATE TRIGGER IF NOT EXISTS movies_ai AFTER INSERT ON movies BEGIN
    INSERT INTO movies_fts(rowid, title, genres) VALUES (new.id, new.title, new.genres);
END;
CREATE TRIGGER IF NOT EXISTS movies_ad AFTER DELETE ON movies BEGIN
    INSERT INTO movies_fts(movies_fts, rowid, title, genres) VALUES ('delete', old.id, old.title, old.genres);
END;
CREATE TRIGGER IF NOT EXISTS movies_au AFTER UPDATE ON movies BEGIN
    INSERT INTO movies_fts(movies_fts, rowid, title, genres) VALUES ('delete', old.id, old.title, old.genres);
    INSERT INTO movies_fts(rowid, title, genres) VALUES (new.id, new.title, new.genres);
END;
";
    }
}
