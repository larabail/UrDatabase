using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Converting between the two units a playback position is spoken in. Jellyfin counts
    /// 100-nanosecond ticks and VLC answers in seconds, so every one of these is a factor of ten
    /// million waiting to be got wrong in a way nothing would obviously notice.
    /// </summary>
    public class PlaybackPositionTests
    {
        [Fact]
        public void A_second_is_ten_million_ticks()
        {
            Assert.Equal(10_000_000L, PlaybackPosition.SecondsToTicks(1));
            Assert.Equal(TimeSpan.TicksPerSecond, PlaybackPosition.TicksPerSecond);
        }

        [Fact]
        public void A_realistic_position_survives_the_round_trip()
        {
            // Forty-two minutes and eleven seconds into a film.
            var seconds = (42 * 60) + 11;

            var ticks = PlaybackPosition.SecondsToTicks(seconds);

            Assert.Equal(seconds, PlaybackPosition.TicksToSeconds(ticks));
        }

        [Fact]
        public void Zero_and_below_are_not_positions()
        {
            // VLC answers -1 for a stream it has not opened. Sent on as a negative position it
            // would be a place in no film at all.
            Assert.Equal(0, PlaybackPosition.SecondsToTicks(0));
            Assert.Equal(0, PlaybackPosition.SecondsToTicks(-1));
            Assert.Equal(0, PlaybackPosition.SecondsToTicks(-0.4));
            Assert.Equal(0, PlaybackPosition.TicksToSeconds(-5));
        }

        [Fact]
        public void A_reading_that_is_not_a_number_is_not_a_position()
        {
            Assert.Equal(0, PlaybackPosition.SecondsToTicks(double.NaN));
            Assert.Equal(0, PlaybackPosition.SecondsToTicks(double.PositiveInfinity));
        }

        [Fact]
        public void An_absurd_reading_clamps_rather_than_overflowing_into_a_negative()
        {
            // The failure this guards against is silent: the multiplication wraps, the server is
            // told the viewer is at a negative position, and nothing in between looks wrong.
            var ticks = PlaybackPosition.SecondsToTicks(1e18);

            Assert.True(ticks > 0);
            Assert.Equal(long.MaxValue, ticks);
        }

        [Fact]
        public void The_servers_own_percentage_is_preferred_to_dividing()
        {
            // A cached runtime is rounded to the minute and can describe a different cut. The
            // server knows the length of the file it is serving.
            var fraction = PlaybackPosition.Fraction(
                positionTicks: PlaybackPosition.SecondsToTicks(60),
                runtimeTicks: PlaybackPosition.SecondsToTicks(6000),
                playedPercentage: 40);

            Assert.Equal(0.4, fraction!.Value, 3);
        }

        [Fact]
        public void Without_a_percentage_the_runtime_answers()
        {
            var fraction = PlaybackPosition.Fraction(
                positionTicks: PlaybackPosition.SecondsToTicks(1500),
                runtimeTicks: PlaybackPosition.SecondsToTicks(6000),
                playedPercentage: null);

            Assert.Equal(0.25, fraction!.Value, 3);
        }

        [Fact]
        public void With_neither_there_is_no_fraction_rather_than_a_zero()
        {
            Assert.Null(PlaybackPosition.Fraction(PlaybackPosition.SecondsToTicks(90), null, null));
            Assert.Null(PlaybackPosition.Fraction(0, PlaybackPosition.SecondsToTicks(6000), null));
        }

        [Fact]
        public void A_fraction_never_leaves_its_own_range()
        {
            // A server that has been told a position past the end of the file would otherwise
            // draw a progress bar wider than the card it is on.
            var over = PlaybackPosition.Fraction(
                PlaybackPosition.SecondsToTicks(9000),
                PlaybackPosition.SecondsToTicks(6000),
                null);

            Assert.Equal(1.0, over!.Value, 3);
            Assert.Equal(1.0, PlaybackPosition.Fraction(1, 1, 250)!.Value, 3);
        }

        [Fact]
        public void The_card_says_how_much_is_left()
        {
            var note = PlaybackPosition.Describe(
                positionTicks: PlaybackPosition.SecondsToTicks(30 * 60),
                runtimeTicks: PlaybackPosition.SecondsToTicks(72 * 60),
                playedPercentage: null);

            Assert.Equal("42 MIN LEFT", note);
        }

        [Fact]
        public void A_long_film_is_counted_in_hours()
        {
            Assert.Equal(
                "1 HR 30 MIN LEFT",
                PlaybackPosition.Describe(
                    PlaybackPosition.SecondsToTicks(60),
                    PlaybackPosition.SecondsToTicks(91 * 60),
                    null));

            // A round two hours does not say "2 HR 0 MIN LEFT".
            Assert.Equal(
                "2 HR LEFT",
                PlaybackPosition.Describe(
                    PlaybackPosition.SecondsToTicks(600),
                    PlaybackPosition.SecondsToTicks(600 + 7200),
                    null));
        }

        [Fact]
        public void Under_a_minute_left_is_a_minute_rather_than_none()
        {
            // "0 MIN LEFT" reads as a film that is over, which is the one thing it is not.
            var note = PlaybackPosition.Describe(
                PlaybackPosition.SecondsToTicks((72 * 60) - 40),
                PlaybackPosition.SecondsToTicks(72 * 60),
                null);

            Assert.Equal("1 MIN LEFT", note);
        }

        [Fact]
        public void Without_a_runtime_it_says_how_far_in_instead()
        {
            Assert.Equal("35% IN", PlaybackPosition.Describe(PlaybackPosition.SecondsToTicks(90), null, 35));
        }

        [Fact]
        public void A_film_at_the_start_has_nothing_to_say()
        {
            Assert.Null(PlaybackPosition.Describe(0, PlaybackPosition.SecondsToTicks(6000), 0));
        }
    }

    /// <summary>
    /// Which films belong in the Continue watching row, and what the card then says.
    /// </summary>
    public class ResumeRowTests
    {
        private static UiMovie Server(string itemId, string title, int year = 1994) => new()
        {
            Id = 0,
            Source = MovieSource.Jellyfin,
            RemoteId = itemId,
            Title = title,
            Year = year,
            Genres = "Drama"
        };

        private static UiMovie Local(long id, string title, int year = 1994) => new()
        {
            Id = id,
            Source = MovieSource.Local,
            Title = title,
            Year = year,
            Genres = "Drama",
            HasFileHere = true
        };

        private static JellyfinResumeItem Entry(string itemId, int positionSeconds, int? runtimeSeconds = 6000, int sortOrder = 0) => new()
        {
            ItemId = itemId,
            PositionTicks = PlaybackPosition.SecondsToTicks(positionSeconds),
            RuntimeTicks = runtimeSeconds is null ? null : PlaybackPosition.SecondsToTicks(runtimeSeconds.Value),
            SortOrder = sortOrder
        };

        [Fact]
        public void A_part_watched_film_is_in_the_row_with_its_position()
        {
            var library = new[] { Server("item1", "The Drama") };

            var row = ResumeRow.Build(library, new[] { Entry("item1", 1500) });

            Assert.Single(row);
            Assert.Same(library[0], row[0]);
            Assert.True(library[0].HasResume);
            Assert.Equal(0.25, library[0].ResumeFraction!.Value, 3);
            Assert.Equal("1 HR 15 MIN LEFT", library[0].ResumeNote);
        }

        [Fact]
        public void A_film_with_no_position_is_not_something_to_continue()
        {
            // The endpoint is asked for part-watched films, but a server may list one that has
            // just been reset. Offering it invites somebody to carry on with a film they never
            // started.
            var library = new[] { Server("item1", "The Drama") };

            Assert.Empty(ResumeRow.Build(library, new[] { Entry("item1", 0) }));
            Assert.False(library[0].HasResume);
        }

        [Fact]
        public void A_position_of_under_a_second_is_a_player_warming_up_rather_than_a_viewing()
        {
            var library = new[] { Server("item1", "The Drama") };

            Assert.Empty(ResumeRow.Build(library, new[]
            {
                new JellyfinResumeItem { ItemId = "item1", PositionTicks = 5_000_000 }
            }));
        }

        [Fact]
        public void The_servers_order_is_kept_rather_than_re_sorted()
        {
            // Most recently watched first is a real answer, and sorting by year or title here
            // would throw it away.
            var library = new[]
            {
                Server("item1", "Alpha", 2001),
                Server("item2", "Zulu", 1971)
            };

            var row = ResumeRow.Build(library, new[]
            {
                Entry("item2", 1500, sortOrder: 0),
                Entry("item1", 1500, sortOrder: 1)
            });

            Assert.Equal(new[] { "Zulu", "Alpha" }, row.Select(m => m.Title).ToArray());
        }

        [Fact]
        public void An_entry_the_movie_library_does_not_hold_is_dropped_rather_than_invented()
        {
            // A television episode, or a film in a library this app was never pointed at. There is
            // no card for it and this app has no way to render one.
            var library = new[] { Server("item1", "The Drama") };

            Assert.Empty(ResumeRow.Build(library, new[] { Entry("episode-99", 1500) }));
        }

        [Fact]
        public void A_film_held_in_both_places_appears_once_as_the_card_that_plays_offline()
        {
            // Merge has already folded the server copy into the local card. Matching on the item
            // id finds that card, so the row shows the same badged card as every shelf below.
            var local = Local(7, "The Drama");
            local.AdoptServerCopy(Server("item1", "El Drama"));

            var row = ResumeRow.Build(new[] { local }, new[] { Entry("item1", 1500) });

            Assert.Single(row);
            Assert.Same(local, row[0]);
            Assert.True(row[0].IsInBothPlaces);
        }

        [Fact]
        public void Building_the_row_again_clears_a_film_that_has_since_been_finished()
        {
            // The window rebuilds its shelves from the same card objects when the source row is
            // clicked, without reloading anything, so this has to be idempotent.
            var library = new[] { Server("item1", "The Drama"), Server("item2", "The Other") };

            ResumeRow.Build(library, new[] { Entry("item1", 1500), Entry("item2", 3000) });
            Assert.True(library[0].HasResume);

            var row = ResumeRow.Build(library, new[] { Entry("item2", 3000) });

            Assert.Single(row);
            Assert.False(library[0].HasResume);
            Assert.Null(library[0].ResumeNote);
            Assert.True(library[1].HasResume);
        }

        [Fact]
        public void No_resume_list_at_all_is_an_empty_row_and_no_marks()
        {
            var library = new[] { Server("item1", "The Drama") };
            library[0].ResumeFraction = 0.5;

            Assert.Empty(ResumeRow.Build(library, null));
            Assert.False(library[0].HasResume);
        }

        [Fact]
        public void One_film_cannot_appear_twice_in_the_row()
        {
            var library = new[] { Server("item1", "The Drama") };

            var row = ResumeRow.Build(library, new[] { Entry("item1", 1500, sortOrder: 0), Entry("item1", 2000, sortOrder: 1) });

            Assert.Single(row);
        }

        [Fact]
        public void An_entry_with_no_id_qualifies_for_nothing()
        {
            Assert.False(ResumeRow.Qualifies(null));
            Assert.False(ResumeRow.Qualifies(new JellyfinResumeItem { ItemId = "  ", PositionTicks = long.MaxValue }));
        }
    }

    /// <summary>
    /// The shelves the library page shows, and the order it shows them in.
    /// </summary>
    public class ContinueWatchingShelfTests
    {
        private static UiMovie Film(string title, string genres, string? remoteId = null) => new()
        {
            Id = 0,
            Source = remoteId is null ? MovieSource.Local : MovieSource.Jellyfin,
            RemoteId = remoteId,
            Title = title,
            Year = 1994,
            Genres = genres,
            HasFileHere = remoteId is null
        };

        [Fact]
        public void The_row_is_above_every_genre()
        {
            var drama = Film("The Drama", "Drama", "item1");
            var western = Film("The Western", "Western", "item2");

            var shelves = LibraryGrouping.BuildShelves(
                new[] { drama, western },
                new[] { LibraryGrouping.AllGenres, "Drama", "Western" },
                new[] { western });

            Assert.Equal(
                new[] { ResumeRow.Heading, "Drama", "Western" },
                shelves.Select(s => s.Name).ToArray());
        }

        [Fact]
        public void An_empty_row_is_left_out_entirely_rather_than_shown_empty()
        {
            // A heading with nothing under it reads as a shelf that failed to load, and on an
            // install with no server that would be the permanent state of the top of the page.
            var shelves = LibraryGrouping.BuildShelves(
                new[] { Film("The Drama", "Drama") },
                new[] { "Drama" },
                Array.Empty<UiMovie>());

            Assert.Equal(new[] { "Drama" }, shelves.Select(s => s.Name).ToArray());
        }

        [Fact]
        public void With_jellyfin_unconfigured_there_is_no_row_at_all()
        {
            // Nothing is cached and nothing is fetched, so the window has no resume list to pass.
            var shelves = LibraryGrouping.BuildShelves(
                new[] { Film("The Drama", "Drama") },
                new[] { "Drama" },
                continueWatching: null);

            Assert.DoesNotContain(shelves, s => s.Name == ResumeRow.Heading);
        }

        [Fact]
        public void The_row_carries_its_own_count()
        {
            var one = Film("The Drama", "Drama", "item1");

            var shelves = LibraryGrouping.BuildShelves(new[] { one }, Array.Empty<string>(), new[] { one });

            Assert.Equal(1, shelves[0].Count);
            Assert.Equal("1 FILM", shelves[0].CountLabel);
        }

        [Fact]
        public void Continue_watching_is_never_a_genre_chip()
        {
            // It is not a genre, and a fourth filter competing with the source row for the same
            // corner of the screen is not what it is for.
            var chips = LibraryGrouping.BuildGenreChips(new[] { Film("The Drama", "Drama", "item1") });

            Assert.DoesNotContain(chips, c => c.Name == ResumeRow.Heading);
        }

        [Fact]
        public void An_empty_genre_is_still_dropped()
        {
            var shelves = LibraryGrouping.BuildShelves(
                new[] { Film("The Drama", "Drama") },
                new[] { "Drama", "Western" },
                null);

            Assert.Equal(new[] { "Drama" }, shelves.Select(s => s.Name).ToArray());
        }
    }

    /// <summary>
    /// The resume list on disk. Tested against a real SQLite file for the same reason the library
    /// cache is: it is what makes the row survive a server nobody can reach.
    /// </summary>
    public class JellyfinResumeCacheTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;

        public JellyfinResumeCacheTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JellyfinResumeItem Entry(string id, int seconds, int order = 0) => new()
        {
            ItemId = id,
            PositionTicks = PlaybackPosition.SecondsToTicks(seconds),
            RuntimeTicks = PlaybackPosition.SecondsToTicks(6000),
            PlayedPercentage = seconds / 60.0,
            SortOrder = order
        };

        [Fact]
        public void The_schema_makes_room_for_a_resume_list()
        {
            // Reaches the table rather than asserting on the DDL, so it holds for both copies of
            // the schema — the file and the one embedded for a trimmed publish.
            using var conn = Database.Open(_dbPath);

            Assert.Empty(JellyfinResumeCache.Load(conn));
        }

        [Fact]
        public void An_existing_library_gains_the_table_rather_than_failing_on_it()
        {
            // The upgrade path: a database written before this feature existed. CREATE TABLE IF
            // NOT EXISTS builds a whole new table on an old database, which is the one thing it
            // does do — unlike a new column.
            using (var old = Database.Open(_dbPath))
            {
                using var drop = old.CreateCommand();
                drop.CommandText = "DROP TABLE jellyfin_resume";
                drop.ExecuteNonQuery();
            }

            using var upgraded = Database.Open(_dbPath);

            Assert.Empty(JellyfinResumeCache.Load(upgraded));
        }

        [Fact]
        public void A_synced_row_survives_a_restart()
        {
            using (var conn = Database.Open(_dbPath))
            {
                Assert.Equal(2, JellyfinResumeCache.Replace(conn, new[] { Entry("item1", 1500), Entry("item2", 300, 1) }));
            }

            using (var reopened = Database.Open(_dbPath))
            {
                var loaded = JellyfinResumeCache.Load(reopened);

                Assert.Equal(new[] { "item1", "item2" }, loaded.Select(i => i.ItemId).ToArray());
                Assert.Equal(PlaybackPosition.SecondsToTicks(1500), loaded[0].PositionTicks);
                Assert.Equal(PlaybackPosition.SecondsToTicks(6000), loaded[0].RuntimeTicks);
                Assert.Equal(25.0, loaded[0].PlayedPercentage!.Value, 3);
            }
        }

        [Fact]
        public void The_servers_order_is_what_comes_back()
        {
            using var conn = Database.Open(_dbPath);
            JellyfinResumeCache.Replace(conn, new[] { Entry("zzz", 100), Entry("aaa", 200) });

            Assert.Equal(new[] { "zzz", "aaa" }, JellyfinResumeCache.Load(conn).Select(i => i.ItemId).ToArray());
        }

        [Fact]
        public void A_later_sync_replaces_the_row_rather_than_adding_to_it()
        {
            using var conn = Database.Open(_dbPath);

            JellyfinResumeCache.Replace(conn, new[] { Entry("item1", 100), Entry("item2", 200, 1) });
            JellyfinResumeCache.Replace(conn, new[] { Entry("item3", 300) });

            Assert.Equal(new[] { "item3" }, JellyfinResumeCache.Load(conn).Select(i => i.ItemId).ToArray());
        }

        [Fact]
        public void An_entry_with_no_id_is_not_written()
        {
            using var conn = Database.Open(_dbPath);

            Assert.Equal(1, JellyfinResumeCache.Replace(conn, new[]
            {
                new JellyfinResumeItem { ItemId = "  ", PositionTicks = 5 },
                Entry("item1", 100)
            }));
        }

        [Fact]
        public void A_missing_runtime_stays_missing_rather_than_becoming_zero()
        {
            using var conn = Database.Open(_dbPath);
            JellyfinResumeCache.Replace(conn, new[]
            {
                new JellyfinResumeItem { ItemId = "item1", PositionTicks = 900_000_000 }
            });

            var loaded = JellyfinResumeCache.Load(conn).Single();

            Assert.Null(loaded.RuntimeTicks);
            Assert.Null(loaded.PlayedPercentage);
        }

        [Fact]
        public void Switching_jellyfin_off_forgets_the_row()
        {
            using var conn = Database.Open(_dbPath);
            JellyfinResumeCache.Replace(conn, new[] { Entry("item1", 100) });

            JellyfinResumeCache.Clear(conn);

            Assert.Empty(JellyfinResumeCache.Load(conn));
        }
    }

    /// <summary>
    /// Reading the row off a server, and what happens when the server is not there.
    /// </summary>
    public class JellyfinResumeFetchTests : IDisposable
    {
        private const string ServerUrl = "http://media.invalid:8096";

        private readonly string _dir;
        private readonly string _dbPath;

        public JellyfinResumeFetchTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-resume-fetch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static JellyfinSettings Settings() => new()
        {
            ServerUrl = ServerUrl,
            Username = "viewer",
            Password = "hunter2"
        };

        private const string AuthJson = """
            {
              "AccessToken": "issued-session-token",
              "User": { "Id": "22222222222222222222222222222222", "Name": "viewer" }
            }
            """;

        private const string ViewsJson = """
            {
              "Items": [ { "Id": "library-1", "Name": "Films", "CollectionType": "movies" } ],
              "TotalRecordCount": 1
            }
            """;

        private const string ResumeJson = """
            {
              "Items": [
                {
                  "Id": "item1",
                  "Name": "The Drama",
                  "ProductionYear": 1994,
                  "RunTimeTicks": 60000000000,
                  "UserData": { "PlaybackPositionTicks": 15000000000, "PlayedPercentage": 25.0, "Played": false }
                },
                {
                  "Id": "item2",
                  "Name": "The Other",
                  "RunTimeTicks": 60000000000,
                  "UserData": { "PlaybackPositionTicks": 0, "PlayedPercentage": 0.0 }
                },
                {
                  "Id": "item3",
                  "Name": "No User Data At All"
                }
              ]
            }
            """;

        private const string LibraryJson = """
            {
              "Items": [
                { "Id": "item1", "Name": "The Drama", "ProductionYear": 1994, "Genres": ["Drama"] }
              ],
              "TotalRecordCount": 1
            }
            """;

        [Fact]
        public async Task The_resume_list_asks_for_films_and_for_the_position()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, ResumeJson));

            using var client = new JellyfinClient(Settings(), handler: handler);

            var resume = await client.GetResumeAsync();

            var request = handler.Requests.Single(r => r.Contains("UserItems/Resume", StringComparison.Ordinal));

            // Without the type filter the endpoint returns television episodes, which this app's
            // filename parser has no concept of and would show as oddly titled films.
            Assert.Contains("IncludeItemTypes=Movie", request, StringComparison.Ordinal);
            Assert.Contains("Fields=UserData", request, StringComparison.Ordinal);
            Assert.Contains("userId=22222222222222222222222222222222", request, StringComparison.Ordinal);

            var entry = Assert.Single(resume);
            Assert.Equal("item1", entry.ItemId);
            Assert.Equal(15_000_000_000L, entry.PositionTicks);
            Assert.Equal(60_000_000_000L, entry.RuntimeTicks);
            Assert.Equal(25.0, entry.PlayedPercentage!.Value, 3);
            Assert.Equal(0, entry.SortOrder);
        }

        [Fact]
        public async Task A_film_with_no_position_and_one_with_no_user_data_are_both_dropped()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, ResumeJson));

            using var client = new JellyfinClient(Settings(), handler: handler);

            Assert.Equal(new[] { "item1" }, (await client.GetResumeAsync()).Select(i => i.ItemId).ToArray());
        }

        [Fact]
        public async Task A_sync_writes_both_the_library_and_the_row()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, ResumeJson),
                ("Views", HttpStatusCode.OK, ViewsJson),
                ("Items", HttpStatusCode.OK, LibraryJson));

            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            Assert.Equal(1, await JellyfinSync.RefreshAsync(client, conn));

            Assert.Single(JellyfinCache.Load(conn));
            Assert.Equal(new[] { "item1" }, JellyfinResumeCache.Load(conn).Select(i => i.ItemId).ToArray());
        }

        [Fact]
        public async Task An_unreachable_server_leaves_the_last_good_row_exactly_where_it_was()
        {
            // The whole point of caching it. The window opens instantly from the cache, and a
            // failed refresh must not turn "no network" into "nothing to continue".
            using (var seeded = Database.Open(_dbPath))
            {
                JellyfinResumeCache.Replace(seeded, new[]
                {
                    new JellyfinResumeItem
                    {
                        ItemId = "item1",
                        PositionTicks = 15_000_000_000L,
                        RuntimeTicks = 60_000_000_000L
                    }
                });
            }

            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("No route to host"));
            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            await Assert.ThrowsAsync<JellyfinException>(() => JellyfinSync.RefreshAsync(client, conn));

            var kept = Assert.Single(JellyfinResumeCache.Load(conn));
            Assert.Equal("item1", kept.ItemId);
            Assert.Equal(15_000_000_000L, kept.PositionTicks);
        }

        [Fact]
        public async Task A_row_that_will_not_load_costs_the_row_and_not_the_library()
        {
            // An older server, a permission, a proxy rewriting a path. Losing the whole library
            // over the shelf above it would be the wrong trade by a wide margin.
            using (var seeded = Database.Open(_dbPath))
            {
                JellyfinResumeCache.Replace(seeded, new[]
                {
                    new JellyfinResumeItem { ItemId = "old-item", PositionTicks = 900_000_000 }
                });
            }

            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.NotFound, "{}"),
                ("Views", HttpStatusCode.OK, ViewsJson),
                ("Items", HttpStatusCode.OK, LibraryJson));

            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            Assert.Equal(1, await JellyfinSync.RefreshAsync(client, conn));

            Assert.Single(JellyfinCache.Load(conn));
            Assert.Equal(new[] { "old-item" }, JellyfinResumeCache.Load(conn).Select(i => i.ItemId).ToArray());
        }

        [Fact]
        public async Task A_server_that_says_nothing_is_part_watched_clears_the_row()
        {
            // An empty answer from a server that did answer is a real answer, unlike no answer.
            using (var seeded = Database.Open(_dbPath))
            {
                JellyfinResumeCache.Replace(seeded, new[]
                {
                    new JellyfinResumeItem { ItemId = "old-item", PositionTicks = 900_000_000 }
                });
            }

            var handler = FakeHttpMessageHandler.Routed(
                ("Users/AuthenticateByName", HttpStatusCode.OK, AuthJson),
                ("UserItems/Resume", HttpStatusCode.OK, """{ "Items": [] }"""),
                ("Views", HttpStatusCode.OK, ViewsJson),
                ("Items", HttpStatusCode.OK, LibraryJson));

            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            await JellyfinSync.RefreshAsync(client, conn);

            Assert.Empty(JellyfinResumeCache.Load(conn));
        }

        [Fact]
        public async Task The_row_survives_the_server_being_unreachable_and_still_renders_from_cache()
        {
            // End to end for the property that matters: a cached row, a server that is not there,
            // and a first shelf that is still built and still above the genres.
            using (var seeded = Database.Open(_dbPath))
            {
                JellyfinCache.Replace(seeded, new[]
                {
                    new JellyfinMovie { ItemId = "item1", Title = "The Drama", Year = 1994, Genres = "Drama" }
                });

                JellyfinResumeCache.Replace(seeded, new[]
                {
                    new JellyfinResumeItem
                    {
                        ItemId = "item1",
                        PositionTicks = 15_000_000_000L,
                        RuntimeTicks = 60_000_000_000L
                    }
                });
            }

            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("No route to host"));
            using var client = new JellyfinClient(Settings(), handler: handler);
            using var conn = Database.Open(_dbPath);

            await Assert.ThrowsAsync<JellyfinException>(() => JellyfinSync.RefreshAsync(client, conn));

            var library = JellyfinLibrary.ToUiMovies(JellyfinCache.Load(conn));
            var row = ResumeRow.Build(library, JellyfinResumeCache.Load(conn));
            var shelves = LibraryGrouping.BuildShelves(library, new[] { "Drama" }, row);

            Assert.Equal(ResumeRow.Heading, shelves[0].Name);
            Assert.Equal("The Drama", shelves[0].Items.Single().Title);
            Assert.Equal("1 HR 15 MIN LEFT", shelves[0].Items.Single().ResumeNote);
        }
    }
}
