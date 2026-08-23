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
        /// A generic SQL error. <c>SQLITE_ERROR</c>, which is what "duplicate column name" and a
        /// genuine mistake in a statement both arrive as — so it is never sufficient on its own to
        /// decide a failure was harmless.
        /// </summary>
        private const int SqliteError = 1;

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

            // Which TMDB film a catalogued one is. The identification used to be re-derived from
            // the title on every open, so correcting a wrong match could not survive the film
            // being reopened — the same wrong guess was simply worked out again.
            AddColumnIfMissing(conn, "movies", "tmdb_id", "INTEGER");

            // The name the scanner gave a film, now that its displayed title can be corrected to
            // something else. Null on every row that predates a correction, which is the honest
            // value: those rows are still called what the scan called them.
            AddColumnIfMissing(conn, "movies", "scan_title", "TEXT");

            // What the server measured about a film's file, as JSON. Nothing asked Jellyfin for
            // its media streams until now, so every library synced before this has the column
            // absent and every film would fail on "no such column" the moment one was opened.
            AddColumnIfMissing(conn, "jellyfin_movies", "media_info", "TEXT");
        }

        /// <summary>
        /// Adds a column when it is absent, and does nothing when it is already there. SQLite has
        /// no <c>ADD COLUMN IF NOT EXISTS</c>, so the table is inspected first.
        /// </summary>
        /// <remarks>
        /// The inspection is a fast path, not the mechanism, because it is check-then-act: two
        /// connections can both read the old shape before either has altered the table, and the
        /// second one's <c>ALTER</c> then fails with "duplicate column name". That is
        /// <c>SQLITE_ERROR</c> rather than <c>SQLITE_BUSY</c>, so <see cref="DatabaseWriteLane"/>
        /// deliberately does not retry it, and it surfaces to whoever asked for the database.
        ///
        /// Not hypothetical, and not rare. <c>PosterAutoLoader</c> calls <see cref="Open"/> from
        /// four tasks at once, and on an install with no Jellyfin server nothing has migrated
        /// before them: the window's read path uses <see cref="Connect"/>, which does not migrate,
        /// and the cache load returns without opening anything at all. So the first code ever to
        /// run <see cref="Migrate"/> on such a library is four concurrent poster fetches, on the
        /// first launch after an upgrade.
        ///
        /// The failure is therefore caught and treated as success — but only once the column is
        /// confirmed present, which is the post-condition this method promises whoever ended up
        /// producing it. An <c>ALTER</c> that failed for any other reason leaves the column
        /// absent, so a genuine schema mistake still throws rather than being quietly buried.
        /// Deliberately not matched on the message: this repository already settled that question
        /// in <see cref="DatabaseWriteLane.IsTransientLockFailure"/>, where the note is that the
        /// text is localised and has changed between provider versions.
        /// </remarks>
        internal static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
        {
            if (!TableExists(conn, table)) return;
            if (ColumnExists(conn, table, column)) return;

            try
            {
                using var cmd = conn.CreateCommand();
                // The names here are compile-time constants from Migrate, never user input.
                cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError && ColumnExists(conn, table, column))
            {
                // Somebody else added it between the check above and the statement. See remarks.
            }
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
    poster_path TEXT,
    tmdb_id     INTEGER,
    scan_title  TEXT
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
    media_info       TEXT,
    synced_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jellyfin_movies_title ON jellyfin_movies(title);
CREATE TABLE IF NOT EXISTS jellyfin_series (
    item_id          TEXT PRIMARY KEY,
    title            TEXT NOT NULL,
    year             INTEGER,
    genres           TEXT,
    overview         TEXT,
    community_rating REAL,
    imdb_id          TEXT,
    tmdb_id          TEXT,
    cast_list        TEXT,
    crew_list        TEXT,
    image_tag        TEXT,
    season_count     INTEGER,
    episode_count    INTEGER,
    synced_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jellyfin_series_title ON jellyfin_series(title);
CREATE TABLE IF NOT EXISTS jellyfin_seasons (
    item_id       TEXT PRIMARY KEY,
    series_id     TEXT NOT NULL,
    name          TEXT NOT NULL,
    season_number INTEGER,
    image_tag     TEXT,
    episode_count INTEGER,
    synced_at     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jellyfin_seasons_series ON jellyfin_seasons(series_id);
CREATE TABLE IF NOT EXISTS jellyfin_episodes (
    item_id          TEXT PRIMARY KEY,
    series_id        TEXT NOT NULL,
    season_id        TEXT,
    name             TEXT NOT NULL,
    season_number    INTEGER,
    episode_number   INTEGER,
    overview         TEXT,
    runtime_minutes  INTEGER,
    community_rating REAL,
    image_tag        TEXT,
    synced_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_jellyfin_episodes_series ON jellyfin_episodes(series_id);
CREATE TABLE IF NOT EXISTS jellyfin_resume (
    item_id           TEXT PRIMARY KEY,
    position_ticks    INTEGER NOT NULL,
    runtime_ticks     INTEGER,
    played_percentage REAL,
    sort_order        INTEGER NOT NULL,
    synced_at         TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS oscar_lookups (
    title      TEXT    NOT NULL,
    year       INTEGER NOT NULL,
    fetched_at TEXT    NOT NULL,
    PRIMARY KEY (title, year)
);
CREATE TABLE IF NOT EXISTS oscar_nominations (
    title    TEXT    NOT NULL,
    year     INTEGER NOT NULL,
    ceremony INTEGER NOT NULL,
    category TEXT    NOT NULL,
    nominee  TEXT    NOT NULL,
    detail   TEXT,
    won      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_oscar_nominations_film ON oscar_nominations(title, year);
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
