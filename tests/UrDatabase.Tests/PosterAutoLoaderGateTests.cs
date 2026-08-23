using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The gate that bounds how many posters are fetched at once, and what happens to it when the
    /// window closes under one.
    ///
    /// None of these reach TMDB. A key has to be present for the loader to get as far as the gate
    /// at all — it returns early without one — so these set an obviously fake key and hand the
    /// loader a handler that refuses to make a request, then cancel.
    /// </summary>
    public class PosterAutoLoaderGateTests : IDisposable
    {
        private readonly string _dir;

        public PosterAutoLoaderGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-posters-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        private string DbPath => Path.Combine(_dir, "movies.db");

        private static AppConfig Configured() => new() { TmdbApiKey = "not-a-real-key" };

        /// <summary>
        /// Refuses every request. Nothing in this file should get as far as one, and saying so
        /// loudly is better than a test that quietly starts depending on a network.
        /// </summary>
        private static FakeHttpMessageHandler Silent() =>
            new(_ => throw new InvalidOperationException("no request should have been made"));

        private PosterAutoLoader Loader(int maxConcurrency) =>
            new(Configured(), DbPath, maxConcurrency, onFailure: null, handler: Silent());

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// Cancelling before the slot is taken used to hand one back anyway, so every poster still
        /// queued when a window closed widened the gate by one. Do it a few times and the next
        /// library warms with no limit on how many fetches run at once.
        /// </summary>
        [Fact]
        public async Task A_fetch_cancelled_before_it_starts_does_not_hand_back_a_slot_it_never_took()
        {
            using var loader = Loader(maxConcurrency: 2);
            Assert.Equal(2, loader.AvailableSlots);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await loader.EnsurePosterAsync(1, "Any Film", 1999, _ => { }, cts.Token);

            Assert.Equal(2, loader.AvailableSlots);
        }

        [Fact]
        public async Task A_window_closing_on_several_queued_fetches_leaves_the_gate_where_it_started()
        {
            using var loader = Loader(maxConcurrency: 4);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            for (var movieId = 1; movieId <= 25; movieId++)
                await loader.EnsurePosterAsync(movieId, $"Film {movieId}", 2000, _ => { }, cts.Token);

            Assert.Equal(4, loader.AvailableSlots);
        }

        /// <summary>
        /// Disposing used to dispose the gate, which is the other half of the same bug: a fetch can
        /// now sit in the write lane for several seconds, so closing the window under one left the
        /// release in its finally throwing ObjectDisposedException on a task nobody was watching.
        /// </summary>
        [Fact]
        public async Task Disposing_the_loader_while_a_fetch_is_queued_does_not_throw()
        {
            var loader = Loader(maxConcurrency: 1);

            using var cts = new CancellationTokenSource();
            var queued = loader.EnsurePosterAsync(1, "Any Film", 1999, _ => { }, cts.Token);

            loader.Dispose();
            cts.Cancel();

            await queued;
        }

        [Fact]
        public async Task A_disposed_loader_starts_nothing_new()
        {
            var loader = Loader(maxConcurrency: 2);
            loader.Dispose();

            await loader.EnsurePosterAsync(1, "Any Film", 1999, _ => Assert.Fail("should not have run"), CancellationToken.None);

            Assert.Equal(2, loader.AvailableSlots);
        }
    }
}
