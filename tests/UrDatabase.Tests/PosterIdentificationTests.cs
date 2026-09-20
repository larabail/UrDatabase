using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class PosterIdentificationTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "urdb-identification-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_directory, "movies.db");

    public PosterIdentificationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_directory, true);
    }

    private PosterAutoLoader Loader(HttpMessageHandler handler, Action<string>? onFailure = null,
        TmdbRequestScheduler? requests = null) =>
        new(new AppConfig
        {
            DatabasePath = DbPath,
            PosterCacheDir = Path.Combine(_directory, "posters"),
            TmdbApiKey = "test-placeholder",
            DownloadPosters = false
        }, DbPath, handler: handler, onFailure: onFailure,
            requests: requests ?? new TmdbRequestScheduler(TimeSpan.Zero));

    private void Seed(int count)
    {
        using var connection = Database.Open(DbPath);
        using var transaction = connection.BeginTransaction();
        for (var id = 1; id <= count; id++)
            connection.Execute("INSERT INTO movies (id, title, year) VALUES (@id, @title, 1999)",
                new { id, title = $"Film {id}" }, transaction);
        transaction.Commit();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, 12)]
    public async Task More_than_six_thousand_films_are_identified_persisted_and_reported(
        HttpStatusCode failure, int failures)
    {
        const int count = 6001;
        Seed(count);
        var attemptsAfter648 = 0;
        using var handler = new ConcurrentHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/genre/movie/list", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"genres":[{"id":18,"name":"Drama"}]}""")
                };
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            var start = query.IndexOf("query=Film ", StringComparison.Ordinal) + "query=Film ".Length;
            var end = query.IndexOf('&', start);
            var id = int.Parse(end < 0 ? query[start..] : query[start..end]);
            if (id >= 649 && Interlocked.Increment(ref attemptsAfter648) <= failures)
            {
                var unavailable = new HttpResponseMessage(failure);
                unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return unavailable;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"results":[{"id":{{id}},"title":"Film {{id}}","release_date":"1999-01-01","poster_path":"/{{id}}.jpg","genre_ids":[18]}]}""")
            };
        });
        var callbacks = 0;
        using var loader = Loader(handler);
        for (var id = 1; id <= count; id++)
            loader.Queue(id, $"Film {id}", 1999, _ => Interlocked.Increment(ref callbacks), default);

        Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(90)));
        Assert.Equal(0, loader.Pending);
        Assert.Equal(count, callbacks);
        Assert.Equal(count + failures + 1, handler.CallCount);
        Assert.Equal(count, loader.Progress.Processed);
        Assert.Equal(count, loader.Progress.Matched);
        Assert.Equal(0, loader.Progress.Pending);
        using var connection = Database.Connect(DbPath);
        Assert.Equal(count, connection.QuerySingle<int>(
            "SELECT COUNT(*) FROM movies WHERE tmdb_id IS NOT NULL AND poster_path IS NOT NULL AND genres='Drama'"));
    }

    [Fact]
    public async Task A_failed_search_reports_the_error_and_refresh_retries_it()
    {
        Seed(1);
        var online = false;
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(online ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"results":[{"id":42,"title":"Film 1","release_date":"1999-01-01","poster_path":"/42.jpg"}]}""")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        var failures = new List<string>();
        using var loader = Loader(handler, failures.Add);
        await loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, default);

        Assert.Contains("503", Assert.Single(failures));
        online = true;
        await loader.RetryMissingAsync();
        await loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, default);

        using var connection = Database.Connect(DbPath);
        Assert.Equal(42, connection.QuerySingle<int>("SELECT tmdb_id FROM movies WHERE id=1"));
    }

    [Fact]
    public async Task A_shared_rate_limit_keeps_films_pending_and_cancellation_does_not_mark_them_failed()
    {
        Seed(1);
        var requests = new TmdbRequestScheduler(TimeSpan.Zero);
        requests.Pause(new RetryConditionHeaderValue(TimeSpan.FromHours(1)), 1);
        using var handler = FakeHttpMessageHandler.Json("""{"results":[]}""");
        using var loader = Loader(handler, requests: requests);
        using var cancellation = new CancellationTokenSource();
        var lookup = loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, cancellation.Token);
        try
        {
            Assert.Equal(1, loader.Progress.Pending);
            Assert.Equal(0, loader.Progress.Processed);
            Assert.True(loader.Progress.RetryAfterSeconds > 0);
            Assert.Equal(0, handler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            await lookup;
        }
        Assert.Equal(0, loader.Progress.Pending);
        Assert.Equal(0, loader.Progress.Failed);
        Assert.Equal(0, loader.Progress.RetryAfterSeconds);
    }

    [Fact]
    public async Task Rebuilt_cards_receive_known_genres_even_without_artwork()
    {
        Seed(1);
        using var handler = FakeHttpMessageHandler.Routed(
            ("/search/movie", HttpStatusCode.OK,
                """{"results":[{"id":42,"title":"Film 1","release_date":"1999-01-01","genre_ids":[18]}]}"""),
            ("/genre/movie/list", HttpStatusCode.OK, """{"genres":[{"id":18,"name":"Drama"}]}"""));
        using var loader = Loader(handler);
        await loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, default);
        string? reported = null;

        await loader.EnsurePosterAsync(1, "Film 1", 1999, found => reported = found.Genres, default);

        Assert.Equal("Drama", reported);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Progress_counts_success_no_match_and_failure_once_per_film()
    {
        Seed(3);
        using var handler = new FakeHttpMessageHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return new HttpResponseMessage(query.Contains("Film 3") ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = new StringContent(query.Contains("Film 1")
                    ? """{"results":[{"id":42,"title":"Film 1","release_date":"1999-01-01","poster_path":"/42.jpg"}]}"""
                    : """{"results":[]}""")
            };
        });
        using var loader = Loader(handler);
        for (var round = 0; round < 2; round++)
            for (var id = 1; id <= 3; id++)
                await loader.EnsurePosterAsync(id, $"Film {id}", 1999, _ => { }, default);

        Assert.Equal(3, loader.Progress.Processed);
        Assert.Equal(0, loader.Progress.Pending);
        Assert.Equal(1, loader.Progress.Matched);
        Assert.Equal(1, loader.Progress.Unmatched);
        Assert.Equal(1, loader.Progress.Failed);

        await loader.RetryMissingAsync();
        Assert.Equal(0, loader.Progress.Total);
    }

    [Fact]
    public async Task Duplicate_shelf_queues_do_not_inflate_pending_counts_and_refresh_keeps_in_flight_work()
    {
        Seed(1);
        using var handler = new HeldHandler();
        using var loader = Loader(handler);
        loader.Queue(1, "Film 1", 1999, _ => { }, default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            loader.Queue(1, "Film 1", 1999, _ => { }, default);
            await loader.RetryMissingAsync();
            Assert.Equal(1, loader.Progress.Total);
            Assert.Equal(1, loader.Progress.Pending);
            Assert.Equal(0, loader.Progress.Processed);
        }
        finally
        {
            handler.Release.TrySetResult();
            Assert.True(await loader.DrainAsync(TimeSpan.FromSeconds(10)));
        }
        Assert.Equal(1, loader.Progress.Matched);
        Assert.Equal(0, loader.Progress.Pending);
    }

    [Fact]
    public async Task A_cancelled_lookup_is_not_a_failed_match_and_does_not_leave_progress_stuck()
    {
        Seed(1);
        using var handler = new HeldHandler();
        using var loader = Loader(handler);
        using var cancellation = new CancellationTokenSource();
        var cancelled = loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await cancelled;
        Assert.Equal(0, loader.Progress.Total);

        handler.Release.TrySetResult();
        await loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, default);

        Assert.Equal(1, loader.Progress.Matched);
        Assert.Equal(0, loader.Progress.Pending);
        Assert.Equal(0, loader.Progress.Failed);
    }

    [Fact]
    public async Task Refresh_during_a_completion_callback_does_not_leave_a_phantom_pending_film()
    {
        Seed(1);
        using var handler = FakeHttpMessageHandler.Json(
            """{"results":[{"id":42,"title":"Film 1","release_date":"1999-01-01","poster_path":"/42.jpg"}]}""");
        using var loader = Loader(handler);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var fetch = Task.Run(() => loader.EnsurePosterAsync(1, "Film 1", 1999, _ =>
        {
            callbackStarted.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }, default));
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await loader.RetryMissingAsync();
            await loader.EnsurePosterAsync(1, "Film 1", 1999, _ => { }, default);
            Assert.Equal(0, loader.Progress.Pending);
            Assert.Equal(1, loader.Progress.Matched);
        }
        finally
        {
            release.Set();
            await fetch;
        }
    }

    private sealed class HeldHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[{"id":42,"title":"Film 1","release_date":"1999-01-01","poster_path":"/42.jpg"}]}""")
            };
        }
    }

    private sealed class ConcurrentHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }
}
