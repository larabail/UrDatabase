using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What happens to poster fetches when the window that asked for them closes.
    ///
    /// The bug these are about is not a crash — it never was. Fetches were started and the tasks
    /// discarded, so a close abandoned however many were in flight: the requests had been made,
    /// the answers were on their way, and nothing was left holding them to write the result down.
    /// The library was fetched again from scratch on the next launch, and nothing anywhere said
    /// why. So these assert two things a quiet failure cannot be caught by — that the loader is
    /// still holding what it started, and that stopping genuinely waits for it.
    ///
    /// Nothing here reaches TMDB. Every loader is built with a fake handler and an obviously
    /// fake key, and no API key is needed to run them.
    /// </summary>
    public class PosterAutoLoaderDrainTests : IDisposable
    {
        private readonly string _dir;

        public PosterAutoLoaderDrainTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-drain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
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

        /// <summary>Films for the loader to look up, with no poster between them.</summary>
        private void Seed(params long[] ids)
        {
            using var conn = Database.Open(DbPath);
            foreach (var id in ids)
                conn.Execute("INSERT INTO movies (id, title, year) VALUES (@id, @title, 1999)", new { id, title = $"Film {id}" });
        }

        private string? StoredPoster(long id)
        {
            using var conn = Database.Connect(DbPath);
            return conn.QuerySingleOrDefault<string?>("SELECT poster_path FROM movies WHERE id=@id", new { id });
        }

        /// <summary>
        /// A TMDB search response for whatever film was asked about.
        /// </summary>
        /// <remarks>
        /// It echoes the title out of the query rather than returning a fixed one, because a
        /// result whose title does not agree with the catalogued film is refused by
        /// <see cref="TmdbMatch"/> — deliberately, since that is how another film's poster used to
        /// end up on a card. A fixed title here would have every one of these tests asserting on a
        /// poster the loader was right not to store, and they are about draining the queue at
        /// shutdown rather than about matching.
        /// </remarks>
        private static Func<HttpRequestMessage, string> SearchResult(int tmdbId = 550) =>
            request =>
            {
                var title = QueriedTitle(request);
                return $@"{{ ""results"": [ {{ ""id"": {tmdbId}, ""title"": ""{title}"", ""release_date"": ""1999-05-01"", ""poster_path"": ""/poster.jpg"" }} ] }}";
            };

        /// <summary>The <c>query</c> parameter TMDB was asked about, decoded.</summary>
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

        // ---------- the queue ----------

        [Fact]
        public async Task A_queued_fetch_is_held_until_it_finishes()
        {
            Seed(1);
            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 2, handler: handler);

            loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
            await handler.Entered;

            Assert.Equal(1, loader.Pending);

            handler.Release();
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, loader.Pending);
        }

        [Fact]
        public async Task Draining_an_idle_loader_returns_at_once()
        {
            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 2, handler: handler);

            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(10)));
        }

        // ---------- the drain ----------

        /// <summary>
        /// The heart of it. A close must not return while a fetch is still running, or the
        /// process is free to exit out from under it.
        /// </summary>
        [Fact]
        public async Task Stopping_waits_for_a_fetch_that_is_already_running()
        {
            Seed(1);
            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
            await handler.Entered;

            var stopping = loader.StopAsync(TimeSpan.FromSeconds(10));

            // Long enough that a stop which was going to return without waiting would have.
            await Task.Delay(200);
            Assert.False(stopping.IsCompleted);

            handler.Release();

            Assert.True(await stopping);
        }

        /// <summary>
        /// And what the waiting is for. The request had already been answered; dropping it here
        /// would mean asking TMDB the same question again on the next launch.
        /// </summary>
        [Fact]
        public async Task A_fetch_caught_by_a_close_still_records_the_poster_it_found()
        {
            Seed(1);
            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            var reported = new ConcurrentBag<string?>();
            loader.Queue(1, "Film 1", 1999, path => reported.Add(path), CancellationToken.None);
            await handler.Entered;

            var stopping = loader.StopAsync(TimeSpan.FromSeconds(10));
            handler.Release();

            Assert.True(await stopping);
            Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", StoredPoster(1));
            Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", Assert.Single(reported));
        }

        /// <summary>
        /// Several at once, which is how a real library warms: four slots, more films than slots,
        /// and a close in the middle of it.
        /// </summary>
        [Fact]
        public async Task Stopping_waits_for_every_fetch_that_was_queued()
        {
            var ids = Enumerable.Range(1, 8).Select(i => (long)i).ToArray();
            Seed(ids);

            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            foreach (var id in ids)
                loader.Queue(id, $"Film {id}", 1999, _ => { }, CancellationToken.None);

            await handler.Entered;
            handler.Release();

            Assert.True(await loader.StopAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, loader.Pending);
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(StoredPoster(id))));
        }

        /// <summary>
        /// The other half of the bargain: waiting is bounded. A poster that will not finish gets
        /// cancelled rather than holding a window open for as long as it likes.
        /// </summary>
        [Fact]
        public async Task Stopping_gives_up_on_a_fetch_that_will_not_finish()
        {
            Seed(1);
            using var handler = new GatedHandler(SearchResult(), honourCancellation: true);
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
            await handler.Entered;

            var settled = await loader.StopAsync(TimeSpan.FromMilliseconds(150));

            Assert.False(settled);
            Assert.Null(StoredPoster(1));
        }

        [Fact]
        public async Task A_stopped_loader_starts_nothing_new()
        {
            Seed(1, 2);
            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            await loader.StopAsync(TimeSpan.FromSeconds(10));

            loader.Queue(2, "Film 2", 1999, _ => Assert.Fail("should not have run"), CancellationToken.None);

            Assert.Equal(0, loader.Pending);
            Assert.Null(StoredPoster(2));
        }

        // ---------- the client ----------

        /// <summary>
        /// One client for the library, not one per film. A TmdbService was built and thrown away
        /// for every poster, and each owns an HttpClient — a few hundred films meant a few
        /// hundred sockets left in TIME_WAIT.
        ///
        /// Asserted through disposal, which is what would actually go wrong: an HttpClient
        /// disposes its handler, so a per-poster client would have taken this handler down with
        /// the first film and left the rest failing on an object that had already gone.
        /// </summary>
        [Fact]
        public async Task Every_poster_goes_through_one_shared_client()
        {
            var ids = new long[] { 1, 2, 3, 4, 5 };
            Seed(ids);

            using var handler = new GatedHandler(SearchResult());
            handler.Release();

            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            foreach (var id in ids)
                loader.Queue(id, $"Film {id}", 1999, _ => { }, CancellationToken.None);

            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(30)));

            Assert.Equal(ids.Length, handler.CallCount);
            Assert.Equal(0, handler.DisposeCount);
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(StoredPoster(id))));
        }

        [Fact]
        public async Task The_shared_client_is_let_go_once_the_last_fetch_is_out()
        {
            Seed(1);
            using var handler = new GatedHandler(SearchResult());
            using var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            loader.Queue(1, "Film 1", 1999, _ => { }, CancellationToken.None);
            await handler.Entered;

            Assert.Equal(0, handler.DisposeCount);

            handler.Release();
            await loader.StopAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, handler.DisposeCount);
        }

        /// <summary>
        /// A loader replaced because the settings changed has nobody to wait for it, so it lets
        /// the client go on its own — but only once the fetch under it is done, never from
        /// underneath one.
        /// </summary>
        [Fact]
        public async Task Disposing_under_a_running_fetch_does_not_pull_the_client_away()
        {
            Seed(1);
            using var handler = new GatedHandler(SearchResult());
            var loader = new PosterAutoLoader(Configured(), DbPath, maxConcurrency: 4, handler: handler);

            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loader.Queue(1, "Film 1", 1999, _ => finished.TrySetResult(), CancellationToken.None);
            await handler.Entered;

            loader.Dispose();
            Assert.Equal(0, handler.DisposeCount);

            handler.Release();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", StoredPoster(1));
            Assert.Equal(1, handler.DisposeCount);
        }

        /// <summary>
        /// A handler that answers only once it is told to, so a test can hold a fetch open and
        /// ask what the loader does while it is running.
        /// </summary>
        private sealed class GatedHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, string> _json;
            private readonly bool _honourCancellation;
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _calls;
            private int _disposals;

            /// <param name="honourCancellation">
            /// Whether a cancelled token cuts the wait short. False makes a fetch that ignores
            /// the deadline, which is how the timeout is tested; true is the ordinary case.
            /// </param>
            public GatedHandler(Func<HttpRequestMessage, string> json, bool honourCancellation = false)
            {
                _json = json;
                _honourCancellation = honourCancellation;
            }

            /// <summary>Completes as soon as a request has arrived and is waiting.</summary>
            public Task Entered => _entered.Task;

            public int CallCount => Volatile.Read(ref _calls);
            public int DisposeCount => Volatile.Read(ref _disposals);

            public void Release() => _released.TrySetResult();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _calls);
                _entered.TrySetResult();

                if (_honourCancellation)
                    await _released.Task.WaitAsync(cancellationToken);
                else
                    await _released.Task;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json(request), System.Text.Encoding.UTF8, "application/json")
                };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Interlocked.Increment(ref _disposals);

                // Nothing left waiting on a handler that has been disposed.
                _released.TrySetResult();

                base.Dispose(disposing);
            }
        }
    }
}
