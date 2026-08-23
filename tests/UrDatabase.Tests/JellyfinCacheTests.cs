using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The cache is what makes the app open instantly and stay useful with the server switched
    /// off, so it is tested against a real SQLite file rather than a mock.
    /// </summary>
    public class JellyfinCacheTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public JellyfinCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-jf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JellyfinMovie Film(string id, string title, int? year = 1994) => new()
        {
            ItemId = id,
            Title = title,
            Year = year,
            Genres = "Drama, Crime",
            Overview = "Nothing that happened to anybody.",
            RuntimeMinutes = 95,
            CommunityRating = 6.8,
            ImdbId = "tt0000001",
            TmdbId = "42",
            ImageTag = "tag-" + id
        };

        [Fact]
        public void The_schema_makes_room_for_a_server_library()
        {
            using var conn = Database.Open(_dbPath);

            // Reaches the table rather than asserting on the DDL, so it holds for both copies of
            // the schema — the file and the one embedded for a trimmed publish.
            Assert.Empty(JellyfinCache.Load(conn));
        }

        [Fact]
        public void A_synced_library_survives_a_restart()
        {
            using (var conn = Database.Open(_dbPath))
            {
                JellyfinCache.Replace(conn, new[] { Film("a", "A Wholly Invented Film"), Film("b", "Another Made Up Picture", 2001) });
            }

            using var reopened = Database.Open(_dbPath);
            var cached = JellyfinCache.Load(reopened);

            Assert.Equal(2, cached.Count);

            var newest = cached[0];
            Assert.Equal("Another Made Up Picture", newest.Title);
            Assert.Equal(2001, newest.Year);
            Assert.Equal("Drama, Crime", newest.Genres);
            Assert.Equal(95, newest.RuntimeMinutes);
            Assert.Equal(6.8, newest.CommunityRating);
            Assert.Equal("tt0000001", newest.ImdbId);
            Assert.Equal("tag-b", newest.ImageTag);
        }

        [Fact]
        public void A_film_removed_from_the_server_disappears_from_the_cache()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new[] { Film("a", "A Wholly Invented Film"), Film("b", "Another Made Up Picture") });
            JellyfinCache.Replace(conn, new[] { Film("a", "A Wholly Invented Film") });

            var cached = JellyfinCache.Load(conn);

            Assert.Single(cached);
            Assert.Equal("a", cached[0].ItemId);
        }

        [Fact]
        public void A_renamed_film_is_updated_rather_than_duplicated()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new[] { Film("a", "Working Title") });
            JellyfinCache.Replace(conn, new[] { Film("a", "The Released Title") });

            var cached = JellyfinCache.Load(conn);

            Assert.Single(cached);
            Assert.Equal("The Released Title", cached[0].Title);
        }

        [Fact]
        public void A_film_with_nothing_optional_round_trips()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new[]
            {
                new JellyfinMovie { ItemId = "bare", Title = "Bare Minimum" }
            });

            var cached = Assert.Single(JellyfinCache.Load(conn));

            Assert.Null(cached.Year);
            Assert.Null(cached.RuntimeMinutes);
            Assert.Null(cached.CommunityRating);
            Assert.Null(cached.ImdbId);
            Assert.Null(cached.ImageTag);
        }

        [Fact]
        public void Films_with_no_id_are_not_stored()
        {
            using var conn = Database.Open(_dbPath);

            var written = JellyfinCache.Replace(conn, new[]
            {
                Film("a", "A Wholly Invented Film"),
                new JellyfinMovie { ItemId = "", Title = "Nameless" }
            });

            Assert.Equal(1, written);
            Assert.Single(JellyfinCache.Load(conn));
        }

        [Fact]
        public void Clearing_forgets_the_server_library_and_nothing_else()
        {
            using var conn = Database.Open(_dbPath);

            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = "INSERT INTO movies (title, year) VALUES ('A Local Film', 1999)";
                insert.ExecuteNonQuery();
            }

            JellyfinCache.Replace(conn, new[] { Film("a", "A Wholly Invented Film") });
            JellyfinCache.Clear(conn);

            Assert.Empty(JellyfinCache.Load(conn));

            using var count = conn.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM movies";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }

        [Fact]
        public void The_last_sync_time_is_remembered()
        {
            using var conn = Database.Open(_dbPath);

            Assert.Null(JellyfinCache.LastSyncedUtc(conn));

            var before = DateTime.UtcNow.AddSeconds(-1);
            JellyfinCache.Replace(conn, new[] { Film("a", "A Wholly Invented Film") });

            var synced = JellyfinCache.LastSyncedUtc(conn);

            Assert.NotNull(synced);
            Assert.True(synced!.Value.ToUniversalTime() >= before);
        }

        [Fact]
        public async Task A_sync_that_fails_leaves_the_last_good_library_alone()
        {
            // The case this exists for: the laptop has left the house. Replacing the cache with
            // whatever a failed fetch produced would turn "no network" into "no films".
            using var conn = Database.Open(_dbPath);
            JellyfinCache.Replace(conn, new[] { Film("a", "A Wholly Invented Film") });

            var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
            using var client = new JellyfinClient(
                new JellyfinSettings { ServerUrl = "http://media.invalid", ApiKey = "not-a-real-key" },
                deviceId: "device-1",
                handler: handler);

            await Assert.ThrowsAsync<JellyfinException>(() => JellyfinSync.RefreshAsync(client, conn));

            Assert.Single(JellyfinCache.Load(conn));
        }

        [Fact]
        public async Task A_sync_that_succeeds_replaces_the_library()
        {
            const string users = """[ { "Id": "u1", "Name": "viewer" } ]""";
            const string views = """{ "Items": [ { "Id": "lib", "Name": "Films", "CollectionType": "movies" } ] }""";
            const string items = """
                {
                  "Items": [ { "Id": "new", "Name": "A Freshly Synced Film", "ProductionYear": 2020, "Genres": ["Drama"] } ],
                  "TotalRecordCount": 1
                }
                """;

            using var conn = Database.Open(_dbPath);
            JellyfinCache.Replace(conn, new[] { Film("stale", "A Film Since Deleted") });

            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, items),
                ("/Views", HttpStatusCode.OK, views),
                ("/Users", HttpStatusCode.OK, users));

            using var client = new JellyfinClient(
                new JellyfinSettings { ServerUrl = "http://media.invalid", ApiKey = "not-a-real-key" },
                deviceId: "device-1",
                handler: handler);

            var result = await JellyfinSync.RefreshAsync(client, conn);

            Assert.Equal(1, result.Films);
            Assert.Equal(0, result.Series);
            var cached = Assert.Single(JellyfinCache.Load(conn));
            Assert.Equal("A Freshly Synced Film", cached.Title);
        }
    }
}
