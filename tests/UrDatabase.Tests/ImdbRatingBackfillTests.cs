using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The background IMDb rating backfill, the OMDb answers it depends on telling apart, the
    /// daily budget, the TMDB-to-IMDb id map, and ratings reaching cards already on screen.
    ///
    /// Against a real SQLite file in a temporary directory, with OMDb and TMDB faked. Nothing here
    /// reaches the network or the real app data directory.
    /// </summary>
    public sealed class ImdbRatingBackfillTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "urdb-backfill-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "movies.db");
        private DateTime _now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        public ImdbRatingBackfillTests()
        {
            Directory.CreateDirectory(_dir);
            using var _ = Database.Open(DbPath);
        }

        public void Dispose()
        {
            _log.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>Answers from a script, and counts what it was asked.</summary>
        private sealed class ScriptedLookup : IImdbRatingLookup
        {
            private readonly Func<string, ImdbLookupResult> _answer;
            public ScriptedLookup(Func<string, ImdbLookupResult> answer, bool available = true)
            {
                _answer = answer;
                IsAvailable = available;
            }

            public bool IsAvailable { get; }
            public List<string> Asked { get; } = new();

            public Task<double?> LookupAsync(string imdbId, CancellationToken ct = default) =>
                Task.FromResult(_answer(imdbId).Rating);

            public Task<ImdbLookupResult> LookupResultAsync(string imdbId, CancellationToken ct = default)
            {
                lock (Asked) Asked.Add(imdbId);
                return Task.FromResult(_answer(imdbId));
            }
        }

        private ImdbRatingBackfill Backfill(
            IImdbRatingLookup lookup,
            List<ImdbRatingFound>? found = null,
            int budget = ImdbRatingBackfill.DefaultDailyBudget,
            Func<int, CancellationToken, Task<string?>>? resolve = null,
            Func<bool>? busy = null) =>
            new(DbPath, lookup, resolve, busy, f => { if (found is not null) lock (found) found.Add(f); },
                dailyBudget: budget, spacing: TimeSpan.Zero, busyPoll: TimeSpan.FromMilliseconds(5), utcNow: () => _now);

        private static UiMovie Card(string? imdb = null, int? tmdb = null, MediaKind kind = MediaKind.Film,
            MovieSource source = MovieSource.Local, long id = 0) =>
            new() { Title = "Card", ImdbId = imdb, TmdbId = tmdb, Kind = kind, Source = source, Id = id };

        private (int Requests, bool Limit) Usage()
        {
            using var conn = Database.Open(DbPath);
            return OmdbUsage.Read(conn, OmdbUsage.DayOf(_now));
        }

        private double? Cached(string imdbId)
        {
            using var conn = Database.Open(DbPath);
            return ImdbRatingService.TryReadCache(conn, imdbId, out var r) ? r : -1;
        }

        // ---------- what OMDb said ----------

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, """{"Response":"False","Error":"Request limit reached!"}""", ImdbLookupStatus.LimitReached)]
        [InlineData(HttpStatusCode.Unauthorized, """{"Response":"False","Error":"Invalid API key!"}""", ImdbLookupStatus.Refused)]
        [InlineData(HttpStatusCode.Unauthorized, "", ImdbLookupStatus.Refused)]
        [InlineData(HttpStatusCode.OK, """{"Response":"False","Error":"Incorrect IMDb ID."}""", ImdbLookupStatus.NoRating)]
        [InlineData(HttpStatusCode.OK, """{"Response":"False","Error":"Movie not found!"}""", ImdbLookupStatus.NoRating)]
        [InlineData(HttpStatusCode.OK, """{"Response":"False","Error":"Error getting data."}""", ImdbLookupStatus.Failed)]
        [InlineData(HttpStatusCode.ServiceUnavailable, "{}", ImdbLookupStatus.Failed)]
        [InlineData(HttpStatusCode.OK, """{"Response":"True","imdbRating":"N/A"}""", ImdbLookupStatus.NoRating)]
        [InlineData(HttpStatusCode.OK, """{"Response":"True","imdbRating":"7.6"}""", ImdbLookupStatus.Rated)]
        public async Task OMDb_says_whether_it_answered_about_the_title(HttpStatusCode status, string body, ImdbLookupStatus expected)
        {
            using var omdb = new OmdbService("test-placeholder", FakeHttpMessageHandler.Json(body, status));

            var result = await omdb.LookupResultAsync("tt0000001");

            Assert.Equal(expected, result.Status);
            Assert.Equal(expected == ImdbLookupStatus.Rated ? 7.6 : null, result.Rating);
        }

        // ---------- the details screen's lookups ----------

        [Fact]
        public async Task A_spent_quota_is_not_cached_as_no_rating()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.LimitReached);
            using var service = new ImdbRatingService(lookup, utcNow: () => _now);
            using var conn = Database.Open(DbPath);

            Assert.Null(await service.GetRatingAsync(conn, "tt0000001"));

            Assert.Equal(-1, Cached("tt0000001"));
            Assert.Equal((1, true), Usage());

            // OMDb said the day is spent; the next title is not asked about at all.
            Assert.Null(await service.GetRatingAsync(conn, "tt0000002"));
            Assert.Single(lookup.Asked);

            // A new day, a new allowance.
            _now = _now.AddDays(1);
            Assert.Null(await service.GetRatingAsync(conn, "tt0000002"));
            Assert.Equal(2, lookup.Asked.Count);
        }

        [Fact]
        public async Task An_outage_is_not_cached_either_but_an_answer_is_and_both_are_counted()
        {
            var lookup = new ScriptedLookup(id => id == "tt0000001" ? ImdbLookupResult.Failed : ImdbLookupResult.Of(null));
            using var service = new ImdbRatingService(lookup, utcNow: () => _now);
            using var conn = Database.Open(DbPath);

            await service.GetRatingAsync(conn, "tt0000001");
            await service.GetRatingAsync(conn, "tt0000002");

            Assert.Equal(-1, Cached("tt0000001"));
            Assert.Null(Cached("tt0000002"));
            Assert.Equal((2, false), Usage());
        }

        // ---------- the backfill ----------

        [Fact]
        public async Task Every_title_with_an_IMDb_id_is_rated_once_and_reported()
        {
            var lookup = new ScriptedLookup(id => ImdbLookupResult.Of(id == "tt0000001" ? 7.6 : null));
            var found = new List<ImdbRatingFound>();
            using var backfill = Backfill(lookup, found);

            backfill.Offer(new[] { Card("tt0000001"), Card("tt0000002", kind: MediaKind.Series), Card("tt0000001") });
            await backfill.WhenIdleAsync();

            // Offered again by the next library read: nothing is asked twice.
            backfill.Offer(new[] { Card("tt0000001"), Card("tt0000002") });
            await backfill.WhenIdleAsync();

            Assert.Equal(new[] { "tt0000001", "tt0000002" }, lookup.Asked.OrderBy(x => x));
            var rated = Assert.Single(found);
            Assert.Equal(("tt0000001", 7.6), (rated.ImdbId, rated.Rating));
            Assert.Equal(7.6, Cached("tt0000001"));
            Assert.Null(Cached("tt0000002"));
            Assert.Equal(2, Usage().Requests);
        }

        [Fact]
        public async Task A_title_already_answered_costs_no_request_but_is_still_reported()
        {
            using (var conn = Database.Open(DbPath))
            {
                ImdbRatingService.WriteCache(conn, "tt0000001", null, 8.1);
                ImdbRatingService.WriteCache(conn, "tt0000002", null, null);
            }

            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(5.0));
            var found = new List<ImdbRatingFound>();
            using var backfill = Backfill(lookup, found);

            backfill.Offer(new[] { Card("tt0000001"), Card("tt0000002") });
            await backfill.WhenIdleAsync();

            Assert.Empty(lookup.Asked);
            Assert.Equal(8.1, Assert.Single(found).Rating);
        }

        [Fact]
        public async Task Cards_already_rated_and_episodes_are_not_offered()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(5.0));
            using var backfill = Backfill(lookup);

            var rated = Card("tt0000001");
            rated.ImdbRating = 7.0;

            backfill.Offer(new[] { rated, Card("tt0000002", kind: MediaKind.Episode) });
            await backfill.WhenIdleAsync();

            Assert.Empty(lookup.Asked);
        }

        [Fact]
        public async Task It_stops_at_its_daily_share_and_carries_on_the_next_day()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(6.0));
            using var backfill = Backfill(lookup, budget: 2);

            backfill.Offer(Enumerable.Range(1, 4).Select(i => Card($"tt000000{i}")));
            await backfill.WhenIdleAsync();

            Assert.Equal(2, lookup.Asked.Count);
            Assert.Equal(2, backfill.Waiting);

            // Offered again the same day: still stopped.
            backfill.Offer(new[] { Card("tt0000009") });
            await backfill.WhenIdleAsync();
            Assert.Equal(2, lookup.Asked.Count);

            _now = _now.AddDays(1);
            backfill.Offer(Array.Empty<UiMovie>());
            await backfill.WhenIdleAsync();

            // A fresh day's share: two more, and the last one waits for the day after.
            Assert.Equal(4, lookup.Asked.Count);
            Assert.Equal(1, backfill.Waiting);
        }

        [Fact]
        public async Task The_details_screens_requests_count_against_the_same_day()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(6.0));
            using (var service = new ImdbRatingService(lookup, utcNow: () => _now))
            using (var conn = Database.Open(DbPath))
                await service.GetRatingAsync(conn, "tt0000099");

            using var backfill = Backfill(lookup, budget: 2);
            backfill.Offer(new[] { Card("tt0000001"), Card("tt0000002") });
            await backfill.WhenIdleAsync();

            Assert.Equal(new[] { "tt0000099", "tt0000001" }, lookup.Asked);
        }

        [Fact]
        public async Task OMDb_saying_the_limit_is_reached_stops_it_for_the_day_and_caches_nothing()
        {
            var lookup = new ScriptedLookup(id => id == "tt0000002" ? ImdbLookupResult.LimitReached : ImdbLookupResult.Of(6.0));
            using var backfill = Backfill(lookup);

            backfill.Offer(new[] { Card("tt0000001"), Card("tt0000002"), Card("tt0000003") });
            await backfill.WhenIdleAsync();

            Assert.Equal(new[] { "tt0000001", "tt0000002" }, lookup.Asked);
            Assert.Equal(-1, Cached("tt0000002"));
            Assert.True(Usage().Limit);
            Assert.Equal(2, backfill.Waiting);
        }

        [Fact]
        public async Task A_refused_key_stops_it_too()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Refused);
            using var backfill = Backfill(lookup);

            backfill.Offer(new[] { Card("tt0000001"), Card("tt0000002") });
            await backfill.WhenIdleAsync();

            Assert.Single(lookup.Asked);
        }

        /// <summary>
        /// A refused key is about the key, not about the day. Saving a corrected key in settings
        /// rebuilds the backfill and the rating service, and they must be able to ask at once
        /// rather than wait for UTC midnight with nothing to say why.
        /// </summary>
        [Fact]
        public async Task A_refused_key_is_not_remembered_past_the_instance_that_saw_it()
        {
            var refused = new ScriptedLookup(_ => ImdbLookupResult.Refused);
            using (var backfill = Backfill(refused))
            {
                backfill.Offer(new[] { Card("tt0000001") });
                await backfill.WhenIdleAsync();

                // The same instance stays stopped: the key will not mend itself.
                backfill.Offer(new[] { Card("tt0000002") });
                await backfill.WhenIdleAsync();
                Assert.Single(refused.Asked);
            }

            Assert.False(Usage().Limit);

            var corrected = new ScriptedLookup(_ => ImdbLookupResult.Of(7.0));
            using (var rebuilt = Backfill(corrected))
            {
                rebuilt.Offer(new[] { Card("tt0000001") });
                await rebuilt.WhenIdleAsync();
            }

            using (var service = new ImdbRatingService(corrected, utcNow: () => _now))
            using (var conn = Database.Open(DbPath))
                Assert.Equal(7.0, await service.GetRatingAsync(conn, "tt0000003"));

            Assert.Equal(new[] { "tt0000001", "tt0000003" }, corrected.Asked);
        }

        [Fact]
        public async Task The_details_screen_stops_asking_with_a_refused_key_but_only_until_it_is_rebuilt()
        {
            var refused = new ScriptedLookup(_ => ImdbLookupResult.Refused);
            using (var service = new ImdbRatingService(refused, utcNow: () => _now))
            using (var conn = Database.Open(DbPath))
            {
                Assert.Null(await service.GetRatingAsync(conn, "tt0000001"));
                Assert.Null(await service.GetRatingAsync(conn, "tt0000002"));
            }

            Assert.Single(refused.Asked);
            Assert.Equal((1, false), Usage());
            Assert.Equal(-1, Cached("tt0000001"));

            var corrected = new ScriptedLookup(_ => ImdbLookupResult.Of(7.0));
            using (var service = new ImdbRatingService(corrected, utcNow: () => _now))
            using (var conn = Database.Open(DbPath))
                Assert.Equal(7.0, await service.GetRatingAsync(conn, "tt0000001"));
        }

        // ---------- answers older builds cached by mistake ----------

        /// <summary>
        /// Before this release every OMDb failure — a 401, a 503, a timeout — was cached as "no
        /// rating", and a cached answer is never asked again. Those rows are indistinguishable
        /// from genuine ones, so the first open after upgrading forgets every "no rating" once,
        /// and the backfill asks again within its daily budget.
        /// </summary>
        [Fact]
        public void Upgrading_forgets_every_no_rating_answer_once_and_only_once()
        {
            using (var conn = Database.Open(DbPath))
            {
                // A catalogue as an older build left it: no record of this migration.
                conn.Execute("DROP TABLE IF EXISTS applied_migrations");
                ImdbRatingService.WriteCache(conn, "tt0000001", null, null);
                ImdbRatingService.WriteCache(conn, "tt0000002", null, 7.4);
            }

            using (var _ = Database.Open(DbPath)) { }

            Assert.Equal(-1, Cached("tt0000001"));
            Assert.Equal(7.4, Cached("tt0000002"));

            // An answer this build caches is a real one, and survives every later open.
            using (var conn = Database.Open(DbPath))
                ImdbRatingService.WriteCache(conn, "tt0000003", null, null);

            using (var _ = Database.Open(DbPath)) { }
            using (var _ = Database.Open(DbPath)) { }

            Assert.Null(Cached("tt0000003"));
        }

        [Fact]
        public async Task A_title_forgotten_on_upgrade_is_asked_about_again()
        {
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute("DROP TABLE IF EXISTS applied_migrations");
                ImdbRatingService.WriteCache(conn, "tt0000001", null, null);
            }

            using (var _ = Database.Open(DbPath)) { }

            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(8.2));
            using var backfill = Backfill(lookup);
            backfill.Offer(new[] { Card("tt0000001") });
            await backfill.WhenIdleAsync();

            Assert.Equal(new[] { "tt0000001" }, lookup.Asked);
            Assert.Equal(8.2, Cached("tt0000001"));
        }

        [Fact]
        public async Task It_gives_up_after_a_run_of_failures_rather_than_spending_the_day_on_them()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Failed);
            using var backfill = Backfill(lookup);

            backfill.Offer(Enumerable.Range(10, 20).Select(i => Card($"tt00000{i}")));
            await backfill.WhenIdleAsync();

            Assert.Equal(ImdbRatingBackfill.MaxConsecutiveFailures, lookup.Asked.Count);
            Assert.All(lookup.Asked, id => Assert.Equal(-1, Cached(id)));
        }

        [Fact]
        public async Task It_waits_for_the_poster_pass_to_finish()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(6.0));
            var busy = true;
            using var backfill = Backfill(lookup, busy: () => Volatile.Read(ref busy));

            backfill.Offer(new[] { Card("tt0000001") });
            await Task.Delay(150);
            Assert.Empty(lookup.Asked);

            Volatile.Write(ref busy, false);
            await backfill.WhenIdleAsync();

            Assert.Single(lookup.Asked);
        }

        [Fact]
        public async Task Without_an_OMDb_key_it_does_nothing()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(6.0), available: false);
            var resolved = 0;
            using var backfill = Backfill(lookup, resolve: (_, _) => { resolved++; return Task.FromResult<string?>("tt0000001"); });

            Assert.False(backfill.IsEnabled);
            backfill.Offer(new[] { Card("tt0000001"), Card(tmdb: 42) });
            await backfill.WhenIdleAsync();

            Assert.Empty(lookup.Asked);
            Assert.Equal(0, resolved);
            Assert.Equal(0, Usage().Requests);
        }

        [Fact]
        public async Task A_film_known_only_by_its_TMDB_id_is_asked_of_TMDB_once_ever()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(7.2));
            var found = new List<ImdbRatingFound>();
            var resolved = new List<int>();
            Task<string?> Resolve(int id, CancellationToken _)
            {
                resolved.Add(id);
                return Task.FromResult<string?>(id == 42 ? "tt0000042" : null);
            }

            using (var backfill = Backfill(lookup, found, resolve: Resolve))
            {
                backfill.Offer(new[] { Card(tmdb: 42), Card(tmdb: 43), Card(tmdb: 44, kind: MediaKind.Series) });
                await backfill.WhenIdleAsync();
            }

            Assert.Equal(new[] { 42, 43 }, resolved);
            Assert.Equal(new[] { "tt0000042" }, lookup.Asked);
            var rated = Assert.Single(found);
            Assert.Equal(("tt0000042", 7.2, (int?)42), (rated.ImdbId, rated.Rating, rated.TmdbId));

            // A later launch: TMDB's answers, including "none", were written down.
            using (var conn = Database.Open(DbPath))
            {
                Assert.Equal((true, "tt0000042"), TmdbImdbIds.Read(conn, 42));
                Assert.Equal((true, (string?)null), TmdbImdbIds.Read(conn, 43));
            }

            using (var again = Backfill(lookup, resolve: Resolve))
            {
                again.Offer(new[] { Card(tmdb: 42), Card(tmdb: 43) });
                await again.WhenIdleAsync();
            }

            Assert.Equal(2, resolved.Count);
            Assert.Single(lookup.Asked);
        }

        [Fact]
        public async Task A_TMDB_failure_is_not_recorded_as_no_IMDb_id()
        {
            var lookup = new ScriptedLookup(_ => ImdbLookupResult.Of(7.2));
            using var backfill = Backfill(lookup, resolve: (_, _) => throw new System.Net.Http.HttpRequestException("down"));

            backfill.Offer(new[] { Card(tmdb: 42) });
            await backfill.WhenIdleAsync();

            using var conn = Database.Open(DbPath);
            Assert.False(TmdbImdbIds.Read(conn, 42).Known);
            Assert.Empty(lookup.Asked);
        }

        // ---------- the facts pass and the library read ----------

        [Fact]
        public async Task The_facts_pass_records_the_IMDb_id_and_the_library_finds_the_rating_through_it()
        {
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute("INSERT INTO movies (id, title, year, tmdb_id) VALUES (1, 'Film', 2025, 42)");

                var facts = FilmFactsStore.From(new TmdbService.TmdbDetails { Id = 42, ImdbId = "tt0000042", Runtime = 135, VoteAverage = 6.4 });
                Assert.Equal("tt0000042", facts.ImdbId);
                await FilmFactsStore.SaveAsync(conn, 1, facts);

                // The backfill's answer, cached under the IMDb id alone.
                ImdbRatingService.WriteCache(conn, "tt0000042", null, 7.6);
            }

            var film = new MovieRepository(DbPath).Query(null).Single();

            Assert.Equal("tt0000042", film.ImdbId);
            Assert.Equal(7.6, film.ImdbRating);
            Assert.Equal(7.6, film.Rating);
            Assert.Equal("2025 · 2h 15m · ★ 7.6", film.MetaLine);
        }

        [Fact]
        public void Facts_landing_on_a_card_give_it_the_IMDb_id_to_be_rated_by()
        {
            var card = Card(tmdb: 42);
            card.ApplyFacts(new FilmFacts(100, 6.0, Array.Empty<string>(), Array.Empty<string>()) { TmdbId = 42, ImdbId = "tt0000042" });

            Assert.Equal("tt0000042", card.ImdbId);
        }

        [Fact]
        public void A_local_programme_carries_its_IMDb_id_onto_its_card()
        {
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute(@"
INSERT INTO series (id, title, year, imdb_id) VALUES (1, 'Show', 2014, 'tt0000777');
INSERT INTO episodes (id, series_id, season_number, episode_number) VALUES (1, 1, 1, 1);
INSERT INTO files (episode_id, file_path) VALUES (1, '/tv/Show.S01E01.mkv');");
            }

            var show = new MovieRepository(DbPath).Query(null).Single(m => m.IsSeries);

            Assert.Equal("tt0000777", show.ImdbId);
            Assert.Null(show.ImdbRating);
        }

        [Fact]
        public async Task Server_cards_carry_their_provider_IMDb_id_and_wear_a_cached_rating()
        {
            var film = JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "a", Title = "F", ImdbId = "tt0000001", CommunityRating = 6.0 });
            var byTmdb = JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "b", Title = "G", TmdbId = "42" });
            var show = JellyfinLibrary.ToUiSeries(new JellyfinSeries { ItemId = "c", Title = "S", ImdbId = "tt0000003" });

            using (var conn = Database.Open(DbPath))
            {
                ImdbRatingService.WriteCache(conn, "tt0000001", null, 7.6);
                ImdbRatingService.WriteCache(conn, "tt0000042", null, 8.0);
                ImdbRatingService.WriteCache(conn, "tt0000003", null, 8.4);
                await TmdbImdbIds.WriteAsync(conn, 42, "tt0000042", CancellationToken.None);

                ImdbRatingBackfill.StampCached(conn, new[] { film, byTmdb, show });
            }

            Assert.Equal(7.6, film.ImdbRating);
            Assert.Equal(6.0, film.Rating);
            Assert.Equal("tt0000042", byTmdb.ImdbId);
            Assert.Equal(8.0, byTmdb.ImdbRating);
            Assert.Equal(8.0, byTmdb.Rating);
            Assert.Equal("★ 8.4", show.MetaLine);
        }

        // ---------- a rating reaching a card on screen ----------

        [Fact]
        public void A_rating_reaches_every_card_that_is_that_title_and_redraws_it()
        {
            var local = Card("tt0000001", source: MovieSource.Local, id: 1);
            local.Rating = 6.1;
            var server = Card("tt0000001", source: MovieSource.Jellyfin);
            server.Rating = 7.0;
            var other = Card("tt0000002");
            var changed = new List<string?>();
            ((INotifyPropertyChanged)local).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.True(ImdbRatingBackfill.ApplyToCards(new[] { local, server, other }, "tt0000001", 7.6, null));

            Assert.Equal(7.6, local.ImdbRating);
            Assert.Equal(7.6, local.Rating);    // what the next read gives a catalogued film
            Assert.Equal(7.6, server.ImdbRating);
            Assert.Equal(7.0, server.Rating);   // the server's own number stays its filter number
            Assert.Null(other.ImdbRating);
            Assert.Contains(nameof(UiMovie.MetaLine), changed);
            Assert.Contains(nameof(UiMovie.CardTooltip), changed);

            Assert.False(ImdbRatingBackfill.ApplyToCards(new[] { local }, "tt0000001", 7.6, null));
        }

        [Fact]
        public void A_rating_found_through_a_TMDB_id_reaches_films_only()
        {
            var film = Card(tmdb: 42);
            var show = Card(tmdb: 42, kind: MediaKind.Series);

            ImdbRatingBackfill.ApplyToCards(new[] { film, show }, "tt0000042", 7.2, 42);

            Assert.Equal(7.2, film.ImdbRating);
            Assert.Equal("tt0000042", film.ImdbId);
            Assert.Null(show.ImdbRating);
        }

        [Fact]
        public void The_card_a_details_screen_was_opened_from_is_found_even_without_an_IMDb_id()
        {
            var card = Card(source: MovieSource.Local, id: 7);
            var server = Card(source: MovieSource.Jellyfin);
            server.Rating = null;

            ImdbRatingBackfill.ApplyToCards(new[] { card, server }, "tt0000001", 6.6, null, c => c.Id == 7);

            Assert.Equal(6.6, card.ImdbRating);
            Assert.Equal("tt0000001", card.ImdbId);
            Assert.Null(server.ImdbRating);
        }

        [Fact]
        public void Every_details_screen_rating_lookup_also_updates_the_card()
        {
            var code = File.ReadAllText(Path.Combine(ShelfNavigationMarkupTests.AppSource(), "Views", "MainWindow.axaml.cs"));

            // The only remaining direct calls are inside the two wrappers that pass the rating on.
            Assert.DoesNotContain("LoadImdbRatingAsync(vm.ImdbId", code);
            Assert.DoesNotContain("loadRating: RetryMissingSeriesRatingAsync", code);
            Assert.DoesNotContain(", LoadImdbRatingAsync,", code);
            Assert.Contains("LoadMovieRatingForCardsAsync(vm, vm.ImdbId", code);
            Assert.Equal(5, CountOf(code, "LoadSeriesRatingForCardsAsync(vm.LocalId, vm.RemoteId"));
        }

        private static int CountOf(string text, string fragment)
        {
            var count = 0;
            for (var i = text.IndexOf(fragment, StringComparison.Ordinal); i >= 0; i = text.IndexOf(fragment, i + 1, StringComparison.Ordinal))
                count++;
            return count;
        }

        // ---------- the card keeps one height ----------

        [Fact]
        public void The_line_under_the_title_is_a_fixed_height_block_holding_the_resume_note()
        {
            var doc = XDocument.Load(Path.Combine(ShelfNavigationMarkupTests.AppSource(), "Views", "MainWindow.axaml"));
            var template = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "DataTemplate"
                && (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "MovieCardTemplate");

            var meta = Assert.Single(template.Descendants(), e => e.Name.LocalName == "TextBlock"
                && (string?)e.Attribute("Text") == "{Binding MetaLine}");
            var resume = Assert.Single(template.Descendants(), e => e.Name.LocalName == "TextBlock"
                && (string?)e.Attribute("Text") == "{Binding ResumeNote}");

            // Both in one block whose height does not depend on what is visible inside it, so a
            // part-watched card is the same height as its neighbours on every shelf.
            Assert.Same(meta.Parent, resume.Parent);
            var block = meta.Parent!;
            Assert.True(double.TryParse((string?)block.Attribute("Height"), out var height) && height > 0,
                "The meta block needs a fixed Height.");
            Assert.Null(block.Attribute("IsVisible"));
        }
    }
}
