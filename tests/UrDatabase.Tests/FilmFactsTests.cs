using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A local film's length, rating and people: fetched from TMDB behind the artwork, kept in the
    /// catalogue, and read back with the library so the filter row can use them offline.
    ///
    /// Against a real SQLite file in a temporary directory, and a fake network. Nothing here
    /// reaches TMDB or the real app data directory.
    /// </summary>
    public sealed class FilmFactsTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "urdb-facts-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "movies.db");

        public FilmFactsTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private const string Details = """
            {"id":42,"title":"Film","poster_path":"/p.jpg","runtime":142,"vote_average":8.66,
             "genres":[{"id":18,"name":"Drama"}],
             "credits":{"cast":[{"name":"Liv Ullmann","character":"Eva"}],
                        "crew":[{"name":"Ingmar Bergman","job":"Director"},{"name":"Someone","job":"Gaffer"}]}}
            """;

        private void Seed(long id, string? poster, string? genres, int? tmdbId)
        {
            using var conn = Database.Open(DbPath);
            conn.Execute(
                "INSERT INTO movies (id, title, year, tmdb_id, poster_path, genres) VALUES (@id, 'Film', 1999, @tmdbId, @poster, @genres)",
                new { id, tmdbId, poster, genres });
        }

        private PosterAutoLoader Loader(FakeHttpMessageHandler handler) => new(new AppConfig
        {
            DatabasePath = DbPath,
            PosterCacheDir = Path.Combine(_dir, "posters"),
            TmdbApiKey = "test-placeholder",
            DownloadPosters = false
        }, DbPath, handler: handler, requests: new TmdbRequestScheduler(TimeSpan.Zero));

        private UiMovie ReadBack(long id) =>
            new MovieRepository(DbPath).Query(null).Single(m => m.Id == id);

        // ---------- reading a TMDB record ----------

        [Fact]
        public void TMDB_saying_zero_means_it_does_not_know()
        {
            var facts = FilmFactsStore.From(new TmdbService.TmdbDetails { Runtime = 0, VoteAverage = 0 });

            Assert.Null(facts.RuntimeMinutes);
            Assert.Null(facts.Rating);
            Assert.Empty(facts.Cast);
        }

        [Fact]
        public void A_rating_is_kept_to_one_decimal_place()
        {
            Assert.Equal(8.7, FilmFactsStore.NormaliseRating(8.66));
            Assert.Null(FilmFactsStore.NormaliseRating(double.NaN));
        }

        [Fact]
        public async Task The_credits_come_in_the_same_request_as_the_details()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/movie/42?", HttpStatusCode.OK, Details));
            using var tmdb = new TmdbService("test-placeholder", "", "w342", false, handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

            var facts = FilmFactsStore.From(await tmdb.GetFilmFactsAsync(42, CancellationToken.None));

            var asked = Assert.Single(handler.Requests);
            Assert.Contains("append_to_response=credits", asked);
            Assert.Equal(142, facts.RuntimeMinutes);
            Assert.Equal(8.7, facts.Rating);
            Assert.Equal(new[] { "Liv Ullmann (Eva)" }, facts.Cast);
            Assert.Equal(new[] { "Director: Ingmar Bergman" }, facts.Crew);
        }

        // ---------- keeping them ----------

        [Fact]
        public async Task Saved_facts_come_back_with_the_library()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
            {
                await FilmFactsStore.SaveAsync(conn, 1, new FilmFacts(142, 8.7,
                    new List<string> { "Liv Ullmann (Eva)" }, new List<string> { "Director: Ingmar Bergman" }));
            }

            var film = ReadBack(1);

            Assert.True(film.FactsKnown);
            Assert.Equal(142, film.RuntimeMinutes);
            Assert.Equal(8.7, film.Rating);
            Assert.Equal(new[] { "Liv Ullmann", "Ingmar Bergman" }, film.People);
        }

        [Fact]
        public void A_film_nobody_has_asked_about_says_so()
        {
            Seed(1, null, null, null);

            var film = ReadBack(1);

            Assert.False(film.FactsKnown);
            Assert.Null(film.RuntimeMinutes);
            Assert.Null(film.Rating);
            Assert.Empty(film.People);
        }

        [Fact]
        public async Task An_empty_answer_is_still_recorded_as_asked()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
                await FilmFactsStore.SaveAsync(conn, 1, FilmFacts.None);

            Assert.True(ReadBack(1).FactsKnown);
        }

        [Fact]
        public async Task The_IMDb_rating_is_preferred_once_the_catalogue_has_one()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
            {
                await FilmFactsStore.SaveAsync(conn, 1, new FilmFacts(100, 6.5, new List<string>(), new List<string>()));
                conn.Execute("INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt1', 1, 7.4, '2026-01-01')");
            }

            Assert.Equal(7.4, ReadBack(1).Rating);
        }

        // ---------- correcting the match ----------

        /// <summary>
        /// The bug: the wrongly matched film's IMDb rating stayed linked, and the library prefers a
        /// linked IMDb rating, so the correction's own TMDB rating was never used.
        /// </summary>
        [Fact]
        public async Task A_correction_stops_using_the_wrong_films_IMDb_rating()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute("INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt-wrong', 1, 3.1, '2026-01-01')");

                // OMDb has nothing for the right film, or there is no OMDb key: no row for tt-right.
                await FilmFactsStore.SaveCorrectedAsync(conn, 1,
                    new FilmFacts(120, 8.2, new List<string>(), new List<string>()), "tt-right");

                // The answer about tt-wrong is kept — it is still true of that title — just not
                // credited to this film.
                Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM imdb_ratings WHERE imdb_id='tt-wrong' AND movie_id IS NULL"));
            }

            var film = ReadBack(1);
            Assert.Equal(8.2, film.Rating);
            Assert.Equal(120, film.RuntimeMinutes);
        }

        [Fact]
        public async Task A_correction_picks_up_a_rating_already_cached_for_the_right_film()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute(@"
INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt-wrong', 1, 3.1, '2026-01-01');
INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt-right', NULL, 8.9, '2025-01-01');");

                await FilmFactsStore.SaveCorrectedAsync(conn, 1,
                    new FilmFacts(120, 8.2, new List<string>(), new List<string>()), "tt-right");
            }

            Assert.Equal(8.9, ReadBack(1).Rating);
        }

        [Fact]
        public async Task A_correction_does_not_take_a_rating_another_film_owns()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute("INSERT INTO movies (id, title, year) VALUES (2, 'Other', 2001)");
                conn.Execute("INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt-right', 2, 8.9, '2025-01-01')");

                await FilmFactsStore.SaveCorrectedAsync(conn, 1,
                    new FilmFacts(120, 8.2, new List<string>(), new List<string>()), "tt-right");
            }

            Assert.Equal(8.2, ReadBack(1).Rating);
            Assert.Equal(8.9, ReadBack(2).Rating);
        }

        [Fact]
        public async Task A_correction_to_a_film_with_no_IMDb_id_unlinks_every_old_rating()
        {
            Seed(1, null, null, 42);
            using (var conn = Database.Open(DbPath))
            {
                conn.Execute("INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt-wrong', 1, 3.1, '2026-01-01')");

                await FilmFactsStore.SaveCorrectedAsync(conn, 1,
                    new FilmFacts(null, 6.4, new List<string>(), new List<string>()), null);
            }

            Assert.Equal(6.4, ReadBack(1).Rating);
        }

        /// <summary>
        /// The read path names columns only a migration adds, and on an install with no server
        /// nothing else opens the catalogue before it — so the first launch after an upgrade has
        /// to bring the catalogue up to date itself rather than reporting it unreadable.
        /// </summary>
        [Fact]
        public void A_catalogue_from_before_the_filters_is_brought_up_to_date_by_the_first_read()
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString()))
            {
                conn.Open();
                conn.Execute(@"
CREATE TABLE movies (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, year INTEGER, genres TEXT, poster_path TEXT);
CREATE TABLE files (id INTEGER PRIMARY KEY AUTOINCREMENT, movie_id INTEGER, file_path TEXT NOT NULL, size_bytes INTEGER, created_at TEXT, updated_at TEXT);
INSERT INTO movies (id, title, year) VALUES (1, 'Ran', 1985);");
            }

            var films = new MovieRepository(DbPath).Query(null);

            var film = Assert.Single(films);
            Assert.Equal("Ran", film.Title);
            Assert.False(film.FactsKnown);

            using var check = Database.Connect(DbPath);
            Assert.True(Database.ColumnExists(check, "movies", "runtime_minutes"));
            Assert.True(Database.ColumnExists(check, "movies", "facts_fetched_at"));
        }

        // ---------- the poster pass asking for them ----------

        [Fact]
        public async Task A_film_complete_but_for_its_facts_costs_one_request_and_is_not_asked_twice()
        {
            Seed(1, "https://example.invalid/chosen.jpg", "Drama", 42);
            using var handler = FakeHttpMessageHandler.Routed(("/movie/42?", HttpStatusCode.OK, Details));
            using var loader = Loader(handler);
            var reported = new List<Enrichment>();

            loader.Queue(1, "Film", 1999, reported.Add, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.True(loader.HasAskedForFacts(1));

            // A rebuild offers the film again; it must not be asked about a second time.
            loader.Queue(1, "Film", 1999, reported.Add, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var asked = Assert.Single(handler.Requests);
            Assert.Contains("append_to_response=credits", asked);

            var withFacts = Assert.Single(reported, r => r.Facts is not null);
            Assert.Equal(142, withFacts.Facts!.RuntimeMinutes);
            Assert.Equal("https://example.invalid/chosen.jpg", withFacts.PosterPath);
            Assert.Equal("Drama", withFacts.Genres);

            var film = ReadBack(1);
            Assert.Equal(142, film.RuntimeMinutes);
            Assert.Equal("https://example.invalid/chosen.jpg", film.PosterPath);
        }

        [Fact]
        public async Task A_film_identified_by_a_search_has_its_facts_fetched_afterwards()
        {
            Seed(1, null, null, null);
            using var handler = FakeHttpMessageHandler.Routed(
                ("/search/movie", HttpStatusCode.OK,
                    """{"results":[{"id":42,"title":"Film","release_date":"1999-01-01","poster_path":"/p.jpg","genre_ids":[18]}]}"""),
                ("/genre/movie/list", HttpStatusCode.OK, """{"genres":[{"id":18,"name":"Drama"}]}"""),
                ("/movie/42?", HttpStatusCode.OK, Details));
            using var loader = Loader(handler);

            loader.Queue(1, "Film", 1999, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var film = ReadBack(1);
            Assert.Equal(42, film.TmdbId);
            Assert.Equal("Drama", film.Genres);
            Assert.True(film.FactsKnown);
            Assert.Equal(142, film.RuntimeMinutes);
            Assert.Contains("Ingmar Bergman", film.People);
        }

        [Fact]
        public async Task A_film_with_a_stored_identity_gets_its_facts_from_the_same_request_as_its_poster()
        {
            Seed(1, null, null, 42);
            using var handler = FakeHttpMessageHandler.Routed(("/movie/42?", HttpStatusCode.OK, Details));
            using var loader = Loader(handler);

            await loader.EnsurePosterAsync(1, "Film", 1999, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.Single(handler.Requests);
            var film = ReadBack(1);
            Assert.EndsWith("/p.jpg", film.PosterPath);
            Assert.Equal(142, film.RuntimeMinutes);
            Assert.Equal(8.7, film.Rating);
        }

        [Fact]
        public async Task A_film_with_no_identity_is_not_asked_about_and_not_offered_again()
        {
            Seed(1, "https://example.invalid/chosen.jpg", "Drama", null);
            using var handler = FakeHttpMessageHandler.Routed();
            using var loader = Loader(handler);

            loader.Queue(1, "Film", 1999, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.Empty(handler.Requests);
            Assert.True(loader.HasAskedForFacts(1));
            Assert.False(ReadBack(1).FactsKnown);
        }
    }
}
