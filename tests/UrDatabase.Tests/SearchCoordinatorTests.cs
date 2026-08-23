using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The rules that decide which search a user actually sees.
    ///
    /// All of this used to live in <c>MainWindow.SearchBox_TextChanged</c>, where it could not be
    /// tested without a UI thread, and so was never tested at all. The out-of-order case below is
    /// the one that matters: it is invisible in manual use until a library is large enough for two
    /// queries to overtake each other, and then it silently shows the wrong film list.
    /// </summary>
    public class SearchCoordinatorTests
    {
        private static readonly string[] Word = { "m", "ma", "mat", "matr", "matri", "matrix" };

        /// <summary>
        /// What the window did before this class existed: every keystroke queried, and every answer
        /// was written to the screen the moment it arrived.
        ///
        /// Not a test of production code — it is the bug, written down, so that the test below it
        /// is demonstrably asserting something that was previously false. A slow query for "ma"
        /// finishing after a fast one for "matrix" leaves the user looking at the results for a
        /// word they finished typing two keystrokes ago.
        /// </summary>
        [Fact]
        public async Task Applying_each_answer_as_it_arrives_shows_the_wrong_search()
        {
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new List<string>();

            async Task Naive(string query)
            {
                if (query == "ma") await slow.Task;
                lock (shown) shown.Add(query);
            }

            var broad = Naive("ma");
            var narrow = Naive("matrix");

            await narrow;
            slow.SetResult();
            await broad;

            Assert.Equal(new[] { "matrix", "ma" }, shown);
        }

        [Fact]
        public async Task A_slow_search_that_finishes_last_does_not_overwrite_a_later_one()
        {
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new List<string>();

            using var coordinator = new SearchCoordinator<string>(
                run: async (query, ct) =>
                {
                    // "ma" matches most of the library, so it is the slow one. It ignores its
                    // cancellation token on purpose: a SQLite statement already running cannot be
                    // interrupted, which is why cancellation alone cannot decide this.
                    if (query == "ma") await slow.Task;
                    return query ?? "";
                },
                apply: (_, result) => shown.Add(result),
                delay: NoWait);

            var broad = coordinator.PostAsync("ma");
            var narrow = coordinator.PostAsync("matrix");

            await narrow;
            Assert.Equal(new[] { "matrix" }, shown);

            slow.SetResult();
            await broad;

            // The late answer arrived holding rows and was refused, because by then it was no
            // longer the newest request.
            Assert.Equal(new[] { "matrix" }, shown);
        }

        [Fact]
        public async Task A_burst_of_keystrokes_costs_one_query()
        {
            var debounce = new ManualDebounce();
            var queried = new List<string?>();
            var shown = new List<string>();

            using var coordinator = new SearchCoordinator<string>(
                run: (query, ct) =>
                {
                    lock (queried) queried.Add(query);
                    return Task.FromResult(query ?? "");
                },
                apply: (_, result) => shown.Add(result),
                delay: debounce.Wait);

            var typing = Word.Select(coordinator.PostAsync).ToArray();

            Assert.Empty(queried);
            Assert.Equal(Word.Length, debounce.Started);

            debounce.ReleaseAll();
            await Task.WhenAll(typing);

            Assert.Equal(new string?[] { "matrix" }, queried);
            Assert.Equal(new[] { "matrix" }, shown);
        }

        [Fact]
        public async Task A_search_runs_once_the_box_goes_quiet()
        {
            var shown = new List<string>();

            // The real Task.Delay this time, so the wiring the app ships is exercised at least once.
            using var coordinator = new SearchCoordinator<string>(
                run: (query, ct) => Task.FromResult(query ?? ""),
                apply: (_, result) => shown.Add(result),
                debounce: TimeSpan.FromMilliseconds(30));

            await coordinator.PostAsync("matrix");

            Assert.Equal(new[] { "matrix" }, shown);
        }

        [Fact]
        public async Task A_refresh_does_not_wait_for_typing_to_stop()
        {
            var debounce = new ManualDebounce();
            var shown = new List<string?>();

            using var coordinator = new SearchCoordinator<string?>(
                run: (query, ct) => Task.FromResult(query),
                apply: (_, result) => shown.Add(result),
                delay: debounce.Wait);

            // A scan finishing is not somebody typing, so there is nothing to coalesce with and
            // no reason to make the library sit still for a fifth of a second first.
            await coordinator.RefreshAsync();

            Assert.Equal(0, debounce.Started);
            Assert.Equal(new string?[] { null }, shown);
        }

        [Fact]
        public async Task A_superseded_search_is_cancelled()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new List<string>();
            CancellationToken broadToken = default;

            using var coordinator = new SearchCoordinator<string>(
                run: async (query, ct) =>
                {
                    if (query == "ma")
                    {
                        broadToken = ct;
                        await release.Task;
                        ct.ThrowIfCancellationRequested();
                    }

                    return query ?? "";
                },
                apply: (_, result) => shown.Add(result),
                delay: NoWait);

            var broad = coordinator.PostAsync("ma");
            Assert.False(broadToken.IsCancellationRequested);

            var narrow = coordinator.PostAsync("matrix");
            Assert.True(broadToken.IsCancellationRequested);

            release.SetResult();
            await Task.WhenAll(broad, narrow);

            Assert.Equal(new[] { "matrix" }, shown);
        }

        [Fact]
        public async Task A_search_still_running_when_the_owner_closes_is_never_applied()
        {
            using var lifetime = new CancellationTokenSource();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shown = new List<string>();

            using var coordinator = new SearchCoordinator<string>(
                // Ignores its token entirely, so nothing but the coordinator can stop this result
                // reaching a window that has gone.
                run: async (query, ct) => { await release.Task; return query ?? ""; },
                apply: (_, result) => shown.Add(result),
                lifetime: lifetime.Token,
                delay: NoWait);

            var search = coordinator.PostAsync("matrix");

            lifetime.Cancel();
            release.SetResult();
            await search;

            Assert.Empty(shown);
        }

        [Fact]
        public async Task A_query_that_throws_is_reported_rather_than_escaping()
        {
            var failures = new List<Exception>();
            var shown = new List<string>();

            using var coordinator = new SearchCoordinator<string>(
                run: (query, ct) => Task.FromException<string>(new InvalidOperationException("no such table: movies")),
                apply: (_, result) => shown.Add(result),
                onError: failures.Add,
                delay: NoWait);

            // Must not throw. The caller is an event handler, and in the window it discards this
            // task; an exception escaping there would end the process rather than the search.
            await coordinator.PostAsync("matrix");

            Assert.Empty(shown);
            var failure = Assert.Single(failures);
            Assert.Equal("no such table: movies", failure.Message);
        }

        [Fact]
        public async Task A_failure_nobody_is_waiting_for_is_not_reported()
        {
            var broke = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failures = new List<Exception>();
            var shown = new List<string>();

            using var coordinator = new SearchCoordinator<string>(
                run: (query, ct) => query == "ma" ? broke.Task : Task.FromResult(query ?? ""),
                apply: (_, result) => shown.Add(result),
                onError: failures.Add,
                delay: NoWait);

            var broad = coordinator.PostAsync("ma");
            var narrow = coordinator.PostAsync("matrix");
            await narrow;

            broke.SetException(new InvalidOperationException("fts5: syntax error"));
            await broad;

            // The status line already says something true about "matrix". Replacing it with a
            // failure from a word the user has moved on from would be a lie about what is on screen.
            Assert.Empty(failures);
            Assert.Equal(new[] { "matrix" }, shown);
        }

        [Fact]
        public async Task Nothing_runs_after_the_coordinator_is_disposed()
        {
            var queried = 0;
            var shown = new List<string>();

            var coordinator = new SearchCoordinator<string>(
                run: (query, ct) => { Interlocked.Increment(ref queried); return Task.FromResult(query ?? ""); },
                apply: (_, result) => shown.Add(result),
                delay: NoWait);

            coordinator.Dispose();
            coordinator.Dispose();

            await coordinator.PostAsync("matrix");
            await coordinator.RefreshAsync();

            Assert.Equal(0, queried);
            Assert.Empty(shown);
        }

        [Fact]
        public void A_negative_debounce_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SearchCoordinator<string>(
                run: (query, ct) => Task.FromResult(""),
                apply: (_, __) => { },
                debounce: TimeSpan.FromMilliseconds(-1)));
        }

        private static Task NoWait(TimeSpan wait, CancellationToken ct) => Task.CompletedTask;
    }
}
