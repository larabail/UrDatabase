using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{

    public class DetailLoadingTests : IDisposable
    {
        private readonly TempLog _log = new();

        [Fact]
        public async Task The_page_is_visible_while_an_optional_request_is_still_pending()
        {
            var closed = new TaskCompletionSource();
            var reply = new TaskCompletionSource<string>();
            var shown = false;
            string? answer = null;
            using var loading = new DetailLoading(default, _ => { });

            var page = loading.ShowAsync(
                () => { shown = true; return closed.Task; },
                load => load.RunAsync("Awards", _ =>
                {
                    Assert.True(shown);
                    return reply.Task;
                }, value => answer = value));

            Assert.True(shown);
            Assert.Null(answer);
            Assert.False(page.IsCompleted);
            reply.SetResult("Awards");
            Assert.Equal("Awards", answer);
            Assert.False(page.IsCompleted);
            closed.SetResult();
            await page;
        }

        [Fact]
        public async Task A_timeout_does_not_discard_details_or_cancel_an_independent_lookup()
        {
            var notices = new List<string>();
            var slow = new TaskCompletionSource<string>();
            var other = new TaskCompletionSource<string>();
            using var loading = new DetailLoading(default, notices.Add);
            var shown = new List<string> { "Cached title and plot" };
            CancellationToken otherToken = default;

            var awards = loading.RunAsync("Awards", _ => slow.Task, shown.Add);
            var rating = loading.RunAsync("IMDb rating", ct =>
            {
                otherToken = ct;
                return other.Task;
            }, shown.Add);

            // This is what HttpClient throws on a timeout, without cancelling the caller's token.
            slow.SetException(new TaskCanceledException("A task was canceled."));
            Assert.False(await awards);
            Assert.False(otherToken.IsCancellationRequested);
            other.SetResult("8.1");
            Assert.True(await rating);
            Assert.Equal(new[] { "Cached title and plot", "8.1" }, shown);
            Assert.Contains(notices, text => text.Contains("Awards timed out."));
        }

        [Fact]
        public async Task Every_request_gets_a_fresh_deadline()
        {
            using var loading = new DetailLoading(default, _ => { }, TimeSpan.FromMilliseconds(30));
            CancellationToken expired = default;
            Assert.False(await loading.RunAsync("TMDB", ct =>
            {
                expired = ct;
                return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => "", TaskScheduler.Default);
            }, _ => Assert.Fail("An expired answer must not be applied.")));

            Assert.True(expired.IsCancellationRequested);
            Assert.True(await loading.RunAsync("IMDb rating", ct =>
            {
                Assert.False(ct.IsCancellationRequested);
                return Task.FromResult(8.1);
            }, rating => Assert.Equal(8.1, rating)));
        }

        [Fact]
        public async Task Leaving_the_page_cancels_requests_and_refuses_late_answers()
        {
            var closed = new TaskCompletionSource();
            var reply = new TaskCompletionSource<string>();
            var notices = new List<string>();
            var applied = false;
            CancellationToken requestToken = default;
            using var loading = new DetailLoading(default, notices.Add);

            var page = loading.ShowAsync(() => closed.Task, load =>
                load.RunAsync("TMDB", ct => { requestToken = ct; return reply.Task; }, _ => applied = true));

            closed.SetResult();
            await page.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(requestToken.IsCancellationRequested);
            reply.SetResult("A film we are no longer looking at");
            Assert.False(applied);
            Assert.DoesNotContain(notices, text => text.Contains("timed out"));
        }

        [Fact]
        public async Task A_correction_cancels_enrichment_without_closing_the_page()
        {
            var closed = new TaskCompletionSource();
            var reply = new TaskCompletionSource<string>();
            using var loading = new DetailLoading(default, _ => { });
            var applied = false;
            var page = loading.ShowAsync(() => closed.Task, load =>
                load.RunAsync("TMDB", _ => reply.Task, _ => applied = true));

            loading.Cancel();
            reply.SetResult("The old match");
            Assert.False(applied);
            Assert.False(page.IsCompleted);
            closed.SetResult();
            await page;
        }

        [Fact]
        public async Task Network_errors_are_reported_without_disclosing_request_urls()
        {
            var notices = new List<string>();
            using var loading = new DetailLoading(default, notices.Add);

            Assert.False(await loading.RunAsync<string>("TMDB",
                _ => throw new HttpRequestException("https://example.invalid?api_key=not-a-real-key"),
                _ => Assert.Fail("A failed request has no answer.")));

            Assert.Contains(notices, text => text.Contains("TMDB could not be loaded."));
            Assert.DoesNotContain(notices, text => text.Contains("api_key"));
        }

        [Fact]
        public async Task Programming_errors_are_not_treated_as_missing_metadata()
        {
            using var loading = new DetailLoading(default, _ => { });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                loading.RunAsync<string>("TMDB", _ => throw new InvalidOperationException(), _ => { }));
        }

        [Fact]
        public void A_pending_file_or_connection_is_not_reported_as_missing_or_offline()
        {
            var movie = new MovieDetailsVm { IsLoadingFile = true };
            Assert.Equal("Finding the linked file...", PlayPrompts.FileNote(movie));
            Assert.Equal(PlayPrompts.FileNote(movie), PlayPrompts.DescribeRefusal(movie));

            movie.IsRemote = true;
            Assert.Equal("Looking for a downloaded copy...", PlayPrompts.FileNote(movie));
            movie.IsLoadingFile = false;
            movie.IsConnecting = true;
            Assert.Equal("Connecting to your Jellyfin server...", PlayPrompts.FileNote(movie));
            Assert.Equal(PlayPrompts.FileNote(movie), PlayPrompts.DescribeRefusal(movie));

            movie.DownloadedPath = "downloaded.mkv";
            Assert.StartsWith("Downloaded to ", PlayPrompts.FileNote(movie));
            Assert.Null(PlayPrompts.DescribeRefusal(movie, _ => true));
        }

        public void Dispose() => _log.Dispose();
    }
}
