using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// What happens when a whole library is handed over at once.
    ///
    /// <c>WarmPosters</c> offers the loader every film the library has no artwork for, in one loop,
    /// on the interface thread. That used to start a task apiece — several thousand of them, each
    /// with a linked cancellation registration, all created before the window could paint and then
    /// immediately parked on a semaphore four of them could hold. The work was correctly limited;
    /// the bookkeeping for it was not.
    ///
    /// Nothing here reaches TMDB.
    /// </summary>
    public class PosterAutoLoaderQueueTests : IDisposable
    {
        private readonly string _dir;
        private readonly TempLog _log = new();

        public PosterAutoLoaderQueueTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-queue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string DbPath => Path.Combine(_dir, "movies.db");

        private AppConfig Configured() => new()
        {
            TmdbApiKey = "not-a-real-key",
            DatabasePath = DbPath,
            PosterCacheDir = Path.Combine(_dir, "posters"),
            DownloadPosters = false
        };

        private void Seed(int count)
        {
            using var conn = Database.Open(DbPath);
            for (var id = 1; id <= count; id++)
            {
                conn.Execute(
                    "INSERT INTO movies (id, title, year) VALUES (@id, @title, 1999)",
                    new { id, title = $"Film {id}" });
            }
        }

        private int StoredPosters()
        {
            using var conn = Database.Connect(DbPath);
            return conn.QuerySingle<int>("SELECT COUNT(*) FROM movies WHERE poster_path IS NOT NULL");
        }

        private string? StoredPoster(long id)
        {
            using var conn = Database.Connect(DbPath);
            return conn.QuerySingleOrDefault<string?>("SELECT poster_path FROM movies WHERE id=@id", new { id });
        }

        private string? StoredGenres(long id)
        {
            using var conn = Database.Connect(DbPath);
            return conn.QuerySingleOrDefault<string?>("SELECT genres FROM movies WHERE id=@id", new { id });
        }

        /// <summary>
        /// Answers every search immediately, and records how many were in flight at once.
        /// </summary>
        private sealed class CountingHandler : HttpMessageHandler
        {
            private readonly string _genreIds;
            private readonly string _genreNames;
            private readonly string? _posterPath;
            private int _inFlight;
            private int _peak;
            private int _searches;

            /// <param name="posterPath">
            /// Null makes a film TMDB identifies but holds no artwork for, which is an ordinary
            /// answer and not an error.
            /// </param>
            public CountingHandler(string genreIds = "", string genreNames = "", string? posterPath = "/poster.jpg")
            {
                _genreIds = genreIds;
                _genreNames = genreNames;
                _posterPath = posterPath;
            }

            public int Peak => Volatile.Read(ref _peak);

            /// <summary>How many times TMDB was asked to identify a film.</summary>
            public int Searches => Volatile.Read(ref _searches);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var now = Interlocked.Increment(ref _inFlight);

                // Highest seen so far, without losing a concurrent raise.
                int seen;
                while (now > (seen = Volatile.Read(ref _peak)))
                {
                    if (Interlocked.CompareExchange(ref _peak, now, seen) == seen) break;
                }

                try
                {
                    // Long enough that fetches genuinely overlap, so the peak means something.
                    await Task.Delay(5, ct);

                    var url = request.RequestUri?.ToString() ?? "";

                    if (url.Contains("/search/movie", StringComparison.Ordinal))
                        Interlocked.Increment(ref _searches);

                    var poster = _posterPath is null ? "null" : $@"""{_posterPath}""";

                    var body = url.Contains("/genre/movie/list", StringComparison.Ordinal)
                        ? $@"{{ ""genres"": [ {_genreNames} ] }}"
                        : $@"{{ ""results"": [ {{ ""id"": 550, ""title"": ""{QueriedTitle(request)}"",
                              ""release_date"": ""1999-05-01"", ""poster_path"": {poster},
                              ""genre_ids"": [{_genreIds}] }} ] }}";

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
                    };
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }

            private static string QueriedTitle(HttpRequestMessage request)
            {
                foreach (var pair in (request.RequestUri?.Query ?? "").TrimStart('?').Split('&'))
                {
                    var split = pair.IndexOf('=');
                    if (split <= 0) continue;
                    if (pair[..split] != "query") continue;

                    return Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));
                }

                return "";
            }
        }

        /// <summary>
        /// The heart of it: handing over a library returns at once, because a film becomes a record
        /// in a queue rather than a task of its own.
        /// </summary>
        [Fact]
        public void Handing_over_a_whole_library_does_not_block_the_caller()
        {
            const int films = 4000;
            Seed(films);

            using var handler = new CountingHandler();
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            var clock = Stopwatch.StartNew();

            for (var id = 1; id <= films; id++)
                loader.Queue(id, $"Film {id}", 1999, _ => { }, CancellationToken.None);

            clock.Stop();

            // Generous: this is about the shape of the work, not about how fast the machine is.
            // Starting four thousand tasks and their cancellation registrations took far longer.
            Assert.True(
                clock.Elapsed < TimeSpan.FromSeconds(2),
                $"Queueing {films} films took {clock.ElapsedMilliseconds}ms, which suggests it is doing per-film work.");

            Assert.Equal(films, loader.Pending);
        }

        /// <summary>
        /// And the queue is genuinely drained: everything handed over is looked up, a few at a
        /// time, with nothing stranded.
        /// </summary>
        [Fact]
        public async Task Everything_queued_is_eventually_fetched()
        {
            const int films = 200;
            Seed(films);

            using var handler = new CountingHandler();
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            for (var id = 1; id <= films; id++)
                loader.Queue(id, $"Film {id}", 1999, _ => { }, CancellationToken.None);

            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(60)));

            Assert.Equal(0, loader.Pending);
            Assert.Equal(films, StoredPosters());
        }

        /// <summary>
        /// Never more at once than the loader was built for, however many films are waiting. This
        /// is what stops a library warming exhausting the machine's sockets, and it is asserted
        /// from the network's side rather than from the gate's so it measures what actually left.
        /// </summary>
        [Fact]
        public async Task No_more_fetches_run_at_once_than_the_loader_was_built_for()
        {
            const int films = 200;
            Seed(films);

            using var handler = new CountingHandler();
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 3, handler: handler);

            for (var id = 1; id <= films; id++)
                loader.Queue(id, $"Film {id}", 1999, _ => { }, CancellationToken.None);

            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(60)));

            Assert.InRange(handler.Peak, 1, 3);
        }

        /// <summary>
        /// A library handed over twice — which is what a rebuilt shelf does — is not fetched twice.
        /// </summary>
        [Fact]
        public async Task Handing_the_same_films_over_again_queues_nothing_new()
        {
            const int films = 50;
            Seed(films);

            using var handler = new CountingHandler();
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            for (var round = 0; round < 3; round++)
            {
                for (var id = 1; id <= films; id++)
                    loader.Queue(id, $"Film {id}", 1999, _ => { }, CancellationToken.None);

                Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(60)));
            }

            Assert.Equal(films, StoredPosters());
        }

        /// <summary>
        /// And it is still told what the catalogue knows.
        /// </summary>
        /// <remarks>
        /// This is the other half of asking about a film only once, and it is not a detail. Every
        /// library read builds fresh <c>UiMovie</c> objects — a Jellyfin sync does one seconds
        /// after launch, and so does every keystroke in the search box — and the window then hands
        /// the loader every poster-less film again, with callbacks closing over the new objects.
        /// A loader that recognised the film and simply returned would leave those callbacks
        /// unfired: the artwork would reach the database and never the card, so posters appeared
        /// only after a restart. That is most of the original bug report, and it would have been
        /// reintroduced by the fix for the rest of it.
        /// </remarks>
        [Fact]
        public async Task A_film_asked_about_again_is_told_what_the_catalogue_already_knows()
        {
            Seed(1);

            using var handler = new CountingHandler();
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            // The first pass, standing in for the library read that warmed the card now gone.
            loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            // The second, standing in for the fresh object a re-read builds.
            var reported = new List<Enrichment>();
            loader.Queue(1, "Film 1", 1999, reported.Add, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var found = Assert.Single(reported);
            Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", found.PosterPath);

            // Still only one search: the answer came out of the catalogue, not off the network.
            Assert.Equal(1, handler.Searches);
        }

        /// <summary>
        /// Genres travel back on that path too, or a card rebuilt by a later read sits in the
        /// Uncategorised bucket while the database says what it is.
        /// </summary>
        [Fact]
        public async Task A_film_asked_about_again_is_told_its_genres_as_well()
        {
            Seed(1);

            using var handler = new CountingHandler(genreIds: "18", genreNames: @"{ ""id"": 18, ""name"": ""Drama"" }");
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            // A first pass that genuinely goes to TMDB, so the film is in _attempted and the
            // second pass has to answer from the catalogue rather than from the network.
            loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var reported = new List<Enrichment>();
            loader.Queue(1, "Film 1", 1999, reported.Add, CancellationToken.None);
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var found = Assert.Single(reported);
            Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", found.PosterPath);
            Assert.Equal("Drama", found.Genres);
            Assert.Equal(1, handler.Searches);
        }

        /// <summary>
        /// A film TMDB knows but holds no artwork for still gets filed.
        /// </summary>
        /// <remarks>
        /// The guard here used to refuse anything without a poster, which threw away an id and a
        /// set of genres already fetched and paid for. Since a film is asked about only once, that
        /// left it uncategorised permanently — the column would never be written, on this launch
        /// or any other, because nothing else writes it. It is the deterministic version of the
        /// very complaint this change is about.
        /// </remarks>
        [Fact]
        public async Task A_film_with_no_artwork_still_learns_what_it_is()
        {
            Seed(1);

            using var handler = new CountingHandler(
                genreIds: "18",
                genreNames: @"{ ""id"": 18, ""name"": ""Drama"" }",
                posterPath: null);

            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            var reported = new List<Enrichment>();
            loader.Queue(1, "Film 1", 1999, reported.Add, CancellationToken.None);

            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            var found = Assert.Single(reported);
            Assert.False(found.HasPoster);
            Assert.Equal("Drama", found.Genres);

            Assert.Equal("Drama", StoredGenres(1));
            Assert.Null(StoredPoster(1));

            // And it was identified, so the details screen has something to describe.
            using var conn = Database.Connect(DbPath);
            Assert.Equal(550, conn.QuerySingle<int?>("SELECT tmdb_id FROM movies WHERE id=1"));
        }
    }
}
