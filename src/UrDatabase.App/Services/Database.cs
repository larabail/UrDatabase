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

            var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            conn.Open();

            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON;";
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

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
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
