using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Where genres end up: the catalogue, and the rules about what may overwrite what.
    ///
    /// Two different answers can fill this column and they are not equally good. The automatic
    /// loader runs on every launch for every film still missing a poster, so it fills only what is
    /// empty; a person choosing the right film in "Wrong film?" is correcting something, so their
    /// answer replaces. Getting that the wrong way round means either a correction that never takes
    /// or a guess that overrules one for ever.
    /// </summary>
    public class MovieGenreTests : IDisposable
    {
        private readonly string _dir;
        private readonly TempLog _log = new();

        public MovieGenreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-genres-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string DbPath => Path.Combine(_dir, "movies.db");

        private void Seed(long id, string? genres = null)
        {
            using var conn = Database.Open(DbPath);
            conn.Execute(
                "INSERT INTO movies (id, title, year, genres) VALUES (@id, @title, 1999, @genres)",
                new { id, title = $"Film {id}", genres });
        }

        private string? StoredGenres(long id)
        {
            using var conn = Database.Connect(DbPath);
            return conn.QuerySingleOrDefault<string?>("SELECT genres FROM movies WHERE id=@id", new { id });
        }

        private string? StoredPoster(long id)
        {
            using var conn = Database.Connect(DbPath);
            return conn.QuerySingleOrDefault<string?>("SELECT poster_path FROM movies WHERE id=@id", new { id });
        }

        // ---------- the automatic loader's write ----------

        [Fact]
        public async Task A_match_records_the_genres_it_came_with()
        {
            Seed(1);

            using var conn = Database.Open(DbPath);
            await MovieMatch.SaveAsync(conn, 1, tmdbId: 550, posterPath: "/p.jpg", genres: "Action, Drama");

            Assert.Equal("Action, Drama", StoredGenres(1));
        }

        /// <summary>
        /// The guard that keeps the automatic match from arguing with a better answer, every
        /// launch, for ever.
        /// </summary>
        [Fact]
        public async Task A_match_does_not_overwrite_genres_that_are_already_there()
        {
            Seed(1, genres: "Film Noir");

            using var conn = Database.Open(DbPath);
            await MovieMatch.SaveAsync(conn, 1, tmdbId: 550, posterPath: "/p.jpg", genres: "Action, Drama");

            Assert.Equal("Film Noir", StoredGenres(1));
        }

        /// <summary>
        /// An empty string is not an answer. A row left blank by an older build has to be fillable.
        /// </summary>
        [Fact]
        public async Task A_blank_column_counts_as_empty_rather_than_as_an_answer()
        {
            Seed(1, genres: "");

            using var conn = Database.Open(DbPath);
            await MovieMatch.SaveAsync(conn, 1, tmdbId: 550, posterPath: "/p.jpg", genres: "Action");

            Assert.Equal("Action", StoredGenres(1));
        }

        [Fact]
        public async Task A_match_with_no_genres_leaves_the_column_alone()
        {
            Seed(1, genres: "Film Noir");

            using var conn = Database.Open(DbPath);
            await MovieMatch.SaveAsync(conn, 1, tmdbId: 550, posterPath: "/p.jpg", genres: null);

            Assert.Equal("Film Noir", StoredGenres(1));
        }

        // ---------- a person's correction ----------

        /// <summary>
        /// The other half. By the time somebody corrects a match, the genres stored are the wrong
        /// film's — so leaving them would file the film under them permanently, since nothing asks
        /// TMDB again about a film that already has a poster.
        /// </summary>
        [Fact]
        public async Task Correcting_a_film_replaces_the_genres_of_the_one_it_was_mistaken_for()
        {
            Seed(1, genres: "Horror");

            using var conn = Database.Open(DbPath);
            await MovieMatch.SaveGenresAsync(conn, 1, "Drama, Romance");

            Assert.Equal("Drama, Romance", StoredGenres(1));
        }

        [Fact]
        public async Task Correcting_to_a_film_TMDB_files_under_nothing_clears_the_column()
        {
            Seed(1, genres: "Horror");

            using var conn = Database.Open(DbPath);
            await MovieMatch.SaveGenresAsync(conn, 1, "");

            Assert.Null(StoredGenres(1));
        }

        // ---------- through the loader ----------

        /// <summary>
        /// End to end, because the point of all of it is that a scanned film stops being
        /// Uncategorised without anybody doing anything.
        /// </summary>
        [Fact]
        public async Task Warming_a_scanned_film_fills_in_its_genres_as_well_as_its_poster()
        {
            Seed(1);

            using var handler = FakeHttpMessageHandler.Routed(
                ("/genre/movie/list", HttpStatusCode.OK,
                    @"{ ""genres"": [ { ""id"": 18, ""name"": ""Drama"" } ] }"),
                ("/search/movie", HttpStatusCode.OK,
                    @"{ ""results"": [ { ""id"": 550, ""title"": ""Film 1"", ""release_date"": ""1999-05-01"",
                        ""poster_path"": ""/poster.jpg"", ""genre_ids"": [18] } ] }"));

            var config = new AppConfig
            {
                TmdbApiKey = "not-a-real-key",
                DatabasePath = DbPath,
                PosterCacheDir = Path.Combine(_dir, "posters"),
                DownloadPosters = false
            };

            using var loader = new PosterAutoLoader(config, DbPath, maxConcurrency: 2, handler: handler);

            var reported = new ConcurrentBag<Enrichment>();
            loader.Queue(1, "Film 1", 1999, reported.Add, CancellationToken.None);

            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal("Drama", StoredGenres(1));
            Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", StoredPoster(1));

            var found = Assert.Single(reported);
            Assert.Equal("Drama", found.Genres);
            Assert.True(found.HasPoster);
        }

        /// <summary>
        /// The shelves now rebuild themselves as genres arrive, and every rebuild offers the loader
        /// the whole library again. A loader that forgot each film as it finished would re-ask TMDB
        /// about every one it had refused, on every rebuild — thousands of requests for an answer
        /// already known, and the surest way to be rate limited out of the ones that would work.
        /// </summary>
        [Fact]
        public async Task A_film_is_only_ever_asked_about_once()
        {
            Seed(1);

            using var handler = FakeHttpMessageHandler.Routed(
                ("/genre/movie/list", HttpStatusCode.OK, @"{ ""genres"": [] }"),
                // Names a different film, so the match is refused and nothing is stored — which is
                // exactly the case that used to be asked again on every rebuild.
                ("/search/movie", HttpStatusCode.OK,
                    @"{ ""results"": [ { ""id"": 99, ""title"": ""Something Else"", ""release_date"": ""1970-01-01"" } ] }"));

            var config = new AppConfig
            {
                TmdbApiKey = "not-a-real-key",
                DatabasePath = DbPath,
                PosterCacheDir = Path.Combine(_dir, "posters"),
                DownloadPosters = false
            };

            using var loader = new PosterAutoLoader(config, DbPath, maxConcurrency: 2, handler: handler);

            for (var rebuild = 0; rebuild < 5; rebuild++)
            {
                loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
                Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));
            }

            Assert.Null(StoredPoster(1));
            Assert.Equal(1, handler.Requests.Count(r => r.Contains("/search/movie", StringComparison.Ordinal)));
        }
    }
}
