using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    public static class Database
    {
        /// <summary>
        /// Opens (creating if needed) the catalogue database and makes sure the schema exists.
        /// A fresh macOS or Windows install has no database at all, so the app has to be able to
        /// build one instead of failing on "no such table".
        /// </summary>
        public static SqliteConnection Open(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("A database path is required.", nameof(dbPath));

            var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // No shared cache, and WAL: under Cache=Shared SQLite takes table-level locks and
            // returns "database table is locked" to a reader immediately, ignoring any busy
            // timeout. A scan holds a write transaction, so the window would fail to read the
            // library — and render it as empty — for as long as one was running. In WAL a reader
            // sees the last committed snapshot instead and is never blocked by the writer.
            var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            EnsureSchema(conn);
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
    updated_at TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_files_path ON files(file_path);
CREATE INDEX IF NOT EXISTS ix_files_movie ON files(movie_id);
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
