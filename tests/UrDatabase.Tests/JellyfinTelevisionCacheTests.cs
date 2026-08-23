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
    /// The television half of the cache, tested against a real SQLite file for the same reason the
    /// film half is: it is what makes a programme browsable with the server switched off, and a
    /// mock would assert that the code called itself rather than that a database came back.
    /// </summary>
    public class JellyfinTelevisionCacheTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public JellyfinTelevisionCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-tv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JellyfinSeries Show(string id, string title, int? year = 2011) => new()
        {
            ItemId = id,
            Title = title,
            Year = year,
            Genres = "Drama, Crime",
            Overview = "Nothing that happened to anybody.",
            CommunityRating = 8.4,
            ImdbId = "tt9000001",
            TmdbId = "1396",
            Cast = new List<string> { "An Invented Actor (A Part)" },
            Crew = new List<string> { "Director: An Invented Director" },
            ImageTag = "tag-" + id,
            SeasonCount = 5,
            EpisodeCount = 62
        };

        private static JellyfinMovie Film(string id, string title) => new()
        {
            ItemId = id,
            Title = title,
            Year = 1994,
            Genres = "Drama"
        };

        private static JellyfinSeason Season(string id, string series, int number) => new()
        {
            ItemId = id,
            SeriesId = series,
            Name = $"Season {number}",
            Number = number,
            EpisodeCount = 2
        };

        private static JellyfinEpisode Episode(string id, string series, int season, int number) => new()
        {
            ItemId = id,
            SeriesId = series,
            SeasonId = $"s{season}",
            Name = $"Episode {number}",
            SeasonNumber = season,
            Number = number,
            Overview = "Something happens.",
            RuntimeMinutes = 48
        };

        [Fact]
        public void The_schema_makes_room_for_television()
        {
            using var conn = Database.Open(_dbPath);

            // Reaches the tables rather than asserting on the DDL, so it holds for both copies of
            // the schema — the file and the one embedded for a trimmed publish.
            Assert.Empty(JellyfinCache.LoadSeries(conn));
            Assert.Empty(JellyfinCache.LoadSeasons(conn, "series1"));
            Assert.Empty(JellyfinCache.LoadEpisodes(conn, "series1"));
        }

        [Fact]
        public void A_synced_programme_survives_a_restart()
        {
            using (var conn = Database.Open(_dbPath))
            {
                JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                    Array.Empty<JellyfinMovie>(),
                    new[] { Show("series1", "A Wholly Invented Programme") }));
            }

            using (var conn = Database.Open(_dbPath))
            {
                var show = Assert.Single(JellyfinCache.LoadSeries(conn));

                Assert.Equal("A Wholly Invented Programme", show.Title);
                Assert.Equal(2011, show.Year);
                Assert.Equal("Drama, Crime", show.Genres);
                Assert.Equal(8.4, show.CommunityRating);
                Assert.Equal(5, show.SeasonCount);
                Assert.Equal(62, show.EpisodeCount);
                Assert.Equal("An Invented Actor (A Part)", Assert.Single(show.Cast));
                Assert.Equal("Director: An Invented Director", Assert.Single(show.Crew));
            }
        }

        [Fact]
        public void A_count_the_server_never_gave_stays_absent_through_the_cache()
        {
            var show = Show("series1", "A Wholly Invented Programme");
            show.SeasonCount = null;
            show.EpisodeCount = null;

            using var conn = Database.Open(_dbPath);
            JellyfinCache.Replace(conn, new JellyfinLibraryContents(Array.Empty<JellyfinMovie>(), new[] { show }));

            var loaded = Assert.Single(JellyfinCache.LoadSeries(conn));

            Assert.Null(loaded.SeasonCount);
            Assert.Null(loaded.EpisodeCount);
        }

        [Fact]
        public void A_sync_replaces_both_halves_of_the_library_together()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                new[] { Film("stale-film", "A Film Since Deleted") },
                new[] { Show("stale-show", "A Programme Since Deleted") }));

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                new[] { Film("film1", "A Wholly Invented Film") },
                new[] { Show("series1", "A Wholly Invented Programme") }));

            Assert.Equal("A Wholly Invented Film", Assert.Single(JellyfinCache.Load(conn)).Title);
            Assert.Equal("A Wholly Invented Programme", Assert.Single(JellyfinCache.LoadSeries(conn)).Title);
        }

        [Fact]
        public void A_server_that_stops_reporting_television_drops_it()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                Array.Empty<JellyfinMovie>(),
                new[] { Show("series1", "A Wholly Invented Programme") }));

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                new[] { Film("film1", "A Wholly Invented Film") },
                Array.Empty<JellyfinSeries>()));

            Assert.Empty(JellyfinCache.LoadSeries(conn));
        }

        [Fact]
        public void Episodes_are_remembered_for_one_series_at_a_time()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series1",
                new[] { Season("s1", "series1", 1) },
                new[] { Episode("e1", "series1", 1, 1), Episode("e2", "series1", 1, 2) });

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series2",
                new[] { Season("t1", "series2", 1) },
                new[] { Episode("f1", "series2", 1, 1) });

            // Writing the second show's episodes must not disturb the first: this runs when a
            // programme is opened, not during a sync, so eleven other shows are already cached.
            Assert.Equal(2, JellyfinCache.LoadEpisodes(conn, "series1").Count);
            Assert.Single(JellyfinCache.LoadEpisodes(conn, "series2"));
            Assert.Single(JellyfinCache.LoadSeasons(conn, "series1"));
        }

        [Fact]
        public void Reopening_a_programme_replaces_what_was_cached_for_it()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series1",
                new[] { Season("s1", "series1", 1) },
                new[] { Episode("gone", "series1", 1, 1), Episode("e2", "series1", 1, 2) });

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series1",
                new[] { Season("s1", "series1", 1) },
                new[] { Episode("e2", "series1", 1, 2) });

            // An episode deleted upstairs should stop being offered rather than linger as
            // something that cannot play.
            var episode = Assert.Single(JellyfinCache.LoadEpisodes(conn, "series1"));
            Assert.Equal("e2", episode.ItemId);
        }

        [Fact]
        public void A_sync_leaves_cached_episodes_alone()
        {
            // A sync does not ask the server about episodes, so it has nothing to say about them.
            // Clearing them would empty the episode list of a programme on a laptop nowhere near
            // the server, on the strength of a request that was never made.
            using var conn = Database.Open(_dbPath);

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series1",
                new[] { Season("s1", "series1", 1) },
                new[] { Episode("e1", "series1", 1, 1) });

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                Array.Empty<JellyfinMovie>(),
                new[] { Show("series1", "A Wholly Invented Programme") }));

            Assert.Single(JellyfinCache.LoadEpisodes(conn, "series1"));
        }

        [Fact]
        public void Episodes_come_back_in_broadcast_order()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series1",
                Array.Empty<JellyfinSeason>(),
                new[]
                {
                    Episode("b", "series1", 2, 1),
                    Episode("c", "series1", 1, 2),
                    Episode("a", "series1", 1, 1)
                });

            Assert.Equal(
                new[] { "a", "c", "b" },
                JellyfinCache.LoadEpisodes(conn, "series1").Select(e => e.ItemId).ToArray());
        }

        [Fact]
        public void Switching_the_server_off_forgets_the_television_too()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                new[] { Film("film1", "A Wholly Invented Film") },
                new[] { Show("series1", "A Wholly Invented Programme") }));

            JellyfinCache.ReplaceEpisodes(
                conn,
                "series1",
                new[] { Season("s1", "series1", 1) },
                new[] { Episode("e1", "series1", 1, 1) });

            JellyfinCache.Clear(conn);

            Assert.Empty(JellyfinCache.Load(conn));
            Assert.Empty(JellyfinCache.LoadSeries(conn));
            Assert.Empty(JellyfinCache.LoadSeasons(conn, "series1"));
            Assert.Empty(JellyfinCache.LoadEpisodes(conn, "series1"));
        }

        [Fact]
        public void A_television_only_library_has_still_been_synced()
        {
            // Read from both tables, or a server holding nothing but programmes would report that
            // it had never synced at all.
            using var conn = Database.Open(_dbPath);

            Assert.Null(JellyfinCache.LastSyncedUtc(conn));

            JellyfinCache.Replace(conn, new JellyfinLibraryContents(
                Array.Empty<JellyfinMovie>(),
                new[] { Show("series1", "A Wholly Invented Programme") }));

            Assert.NotNull(JellyfinCache.LastSyncedUtc(conn));
        }

        [Fact]
        public async Task A_sync_writes_the_films_and_the_programmes_and_counts_both()
        {
            const string users = """[ { "Id": "u1", "Name": "viewer" } ]""";
            const string views = """
                {
                  "Items": [
                    { "Id": "films", "Name": "Films", "CollectionType": "movies" },
                    { "Id": "shows", "Name": "Shows", "CollectionType": "tvshows" }
                  ]
                }
                """;

            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";

                if (url.Contains("/Views", StringComparison.Ordinal)) return Json(views);

                if (url.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal))
                    return Json("""
                        { "Items": [ { "Id": "film1", "Name": "A Wholly Invented Film" } ], "TotalRecordCount": 1 }
                        """);

                if (url.Contains("IncludeItemTypes=Series", StringComparison.Ordinal))
                    return Json("""
                        {
                          "Items": [
                            { "Id": "series1", "Name": "A Wholly Invented Programme" },
                            { "Id": "series2", "Name": "Another Made Up Show" }
                          ],
                          "TotalRecordCount": 2
                        }
                        """);

                return Json(users);
            });

            using var client = new JellyfinClient(
                new JellyfinSettings { ServerUrl = "http://media.invalid", ApiKey = "not-a-real-key" },
                deviceId: "device-1",
                handler: handler);

            using var conn = Database.Open(_dbPath);

            var result = await JellyfinSync.RefreshAsync(client, conn);

            Assert.Equal(1, result.Films);
            Assert.Equal(2, result.Series);
            Assert.Single(JellyfinCache.Load(conn));
            Assert.Equal(2, JellyfinCache.LoadSeries(conn).Count);

            static System.Net.Http.HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
