using System;
using System.Collections.Generic;
using System.IO;
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

        [Fact]
        public void A_library_created_from_nothing_already_has_the_columns()
        {
            using var conn = Database.Open(_dbPath);

            Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "cast_list"));
            Assert.True(Database.ColumnExists(conn, "jellyfin_movies", "crew_list"));
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
