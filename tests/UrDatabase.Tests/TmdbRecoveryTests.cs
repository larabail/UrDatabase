using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class TmdbRecoveryTests : IDisposable
{
    private readonly TempLog _log = new();
    public void Dispose() => _log.Dispose();

    private TmdbService Service(HttpMessageHandler handler) =>
        new("test-placeholder", System.IO.Path.Combine(_log.Directory, "posters"), "w342", false, handler,
            requests: new TmdbRequestScheduler(TimeSpan.Zero));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task An_exact_year_filter_is_relaxed_before_rejecting_a_near_year_match()
    {
        using var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.Query.Contains("year=")
                ? Json("""{"results":[]}""")
                : Json("""{"results":[{"id":42,"title":"The Brutalist","release_date":"2024-12-20","poster_path":"/42.jpg"}]}"""));
        using var tmdb = Service(handler);

        var (id, poster) = await tmdb.SearchPosterAsync("The Brutalist", 2025, default);

        Assert.Equal(42, id);
        Assert.Equal("/42.jpg", poster);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Relaxing_the_query_does_not_relax_the_remake_year_guard()
    {
        using var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.Query.Contains("year=")
                ? Json("""{"results":[]}""")
                : Json("""{"results":[{"id":42,"title":"Dune","release_date":"1984-12-01"}]}"""));
        using var tmdb = Service(handler);

        Assert.Null((await tmdb.SearchPosterAsync("Dune", 2021, default)).TmdbId);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task A_cleaned_release_name_is_queried_without_overriding_the_catalogued_year()
    {
        using var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.Query.Contains("query=The%20Matrix&", StringComparison.Ordinal)
                ? Json("""{"results":[{"id":42,"title":"The Matrix","release_date":"1999-03-31"}]}""")
                : Json("""{"results":[]}"""));
        using var tmdb = Service(handler);

        Assert.Equal(42, (await tmdb.SearchPosterAsync("The.Matrix.1080p [YTS]", 1999, default)).TmdbId);
        Assert.All(handler.Requests.Where(url => url.Contains("year=")), url => Assert.Contains("year=1999", url));
    }

    [Fact]
    public async Task A_localised_search_hit_is_verified_against_its_actual_alternative_titles()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/search/movie", HttpStatusCode.OK,
                """{"results":[{"id":42,"title":"Spirited Away","original_title":"千と千尋の神隠し","release_date":"2001-07-20","genre_ids":[16]}]}"""),
            ("/movie/42/alternative_titles", HttpStatusCode.OK,
                """{"titles":[{"iso_3166_1":"ES","title":"El viaje de Chihiro"}]}"""),
            ("/genre/movie/list", HttpStatusCode.OK, """{"genres":[{"id":16,"name":"Animation"}]}"""));
        using var tmdb = Service(handler);

        var (id, _, genres) = await tmdb.SearchFilmAsync("El viaje de Chihiro", 2001, default);

        Assert.Equal(42, id);
        Assert.Equal("Animation", genres);
        Assert.Single(handler.Requests, url => url.Contains("/alternative_titles"));
    }

    [Fact]
    public async Task A_sole_same_year_result_is_not_accepted_without_a_verified_title()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/search/movie", HttpStatusCode.OK,
                """{"results":[{"id":42,"title":"El Sabor del Drama","release_date":"2026-01-01"}]}"""),
            ("/alternative_titles", HttpStatusCode.OK, """{"titles":[{"title":"A Different Film"}]}"""));
        using var tmdb = Service(handler);

        Assert.Null((await tmdb.SearchFilmAsync("El Drama", 2026, default)).TmdbId);
        Assert.Single(handler.Requests, url => url.Contains("/alternative_titles"));
    }

    [Fact]
    public async Task A_verified_alias_still_needs_a_year_and_an_unambiguous_candidate()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/search/movie", HttpStatusCode.OK,
                """{"results":[{"id":41,"title":"English One","release_date":"2001-01-01"},{"id":42,"title":"English Two","release_date":"2001-01-01"}]}"""),
            ("/alternative_titles", HttpStatusCode.OK, """{"titles":[{"title":"Local Title"}]}"""));
        using var tmdb = Service(handler);

        Assert.Null((await tmdb.SearchPosterAsync("Local Title", null, default)).TmdbId);
        Assert.DoesNotContain(handler.Requests, url => url.Contains("/alternative_titles"));
        Assert.Null((await tmdb.SearchPosterAsync("Local Title", 2001, default)).TmdbId);
        Assert.Equal(2, handler.Requests.Count(url => url.Contains("/alternative_titles")));
    }

    [Fact]
    public async Task An_exact_match_needs_no_fallback_queries()
    {
        using var handler = FakeHttpMessageHandler.Json(
            """{"results":[{"id":42,"title":"The Drama","original_title":"El Drama","release_date":"2026-01-01"}]}""");
        using var tmdb = Service(handler);

        Assert.Equal(42, (await tmdb.SearchPosterAsync("El Drama", 2026, default)).TmdbId);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Transient_http_failures_are_retried_instead_of_becoming_no_match(HttpStatusCode status)
    {
        var calls = 0;
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = ++calls < 3
                ? Json("{}", status)
                : Json("""{"results":[{"id":42,"title":"Film","release_date":"1999-01-01"}]}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var tmdb = Service(handler);

        Assert.Equal(42, (await tmdb.SearchPosterAsync("Film", 1999, default)).TmdbId);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Sustained_rate_limiting_does_not_spend_the_failed_request_budget()
    {
        var calls = 0;
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = ++calls <= 5
                ? Json("{}", HttpStatusCode.TooManyRequests)
                : Json("""{"results":[{"id":42,"title":"Film","release_date":"1999-01-01"}]}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var tmdb = Service(handler);

        Assert.Equal(42, (await tmdb.SearchPosterAsync("Film", 1999, default)).TmdbId);
        Assert.Equal(6, handler.CallCount);
    }

    [Fact]
    public async Task A_rate_limited_client_pauses_other_clients_using_the_same_scheduler()
    {
        var requests = new TmdbRequestScheduler(TimeSpan.Zero);
        using var limitedHandler = new FakeHttpMessageHandler(_ =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return response;
        });
        using var otherHandler = FakeHttpMessageHandler.Json("""{"results":[]}""");
        using var limited = new TmdbService("test-placeholder", System.IO.Path.Combine(_log.Directory, "posters"),
            "w342", false, limitedHandler, requests: requests);
        using var other = new TmdbService("test-placeholder", System.IO.Path.Combine(_log.Directory, "posters"),
            "w342", false, otherHandler, requests: requests);
        using var cancellation = new CancellationTokenSource();
        var first = limited.SearchAsync("First", null, cancellation.Token);
        var second = other.SearchAsync("Second", null, cancellation.Token);
        try
        {
            Assert.True(limited.RateLimitRetryAfterSeconds > 0);
            Assert.Equal(1, limitedHandler.CallCount);
            Assert.Equal(0, otherHandler.CallCount);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        var log = System.IO.File.ReadAllText(System.IO.Path.Combine(_log.Directory, "posters.log"));
        Assert.Contains("HTTP 429", log);
        Assert.DoesNotContain("test-placeholder", log);
    }

    [Fact]
    public async Task An_exhausted_http_failure_is_reportable_and_not_an_empty_search()
    {
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = Json("{}", HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var tmdb = Service(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => tmdb.SearchAsync("Film", 1999, default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.DoesNotContain("test-placeholder", error.Message);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Authentication_errors_are_not_retried_or_disguised_as_no_results()
    {
        using var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
        using var tmdb = Service(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => tmdb.SearchAsync("Film", 1999, default));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task An_http_timeout_is_not_reported_as_caller_cancellation()
    {
        using var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout"));
        using var tmdb = Service(handler);

        await Assert.ThrowsAsync<TimeoutException>(() => tmdb.SearchAsync("Film", 1999, default));

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Dropped_connections_are_retried_with_the_same_query()
    {
        var calls = 0;
        using var handler = new FakeHttpMessageHandler(_ => ++calls < 3
            ? throw new HttpRequestException("Connection lost")
            : Json("""{"results":[{"id":42,"title":"Film","release_date":"1999-01-01"}]}"""));
        using var tmdb = Service(handler);

        Assert.Equal(42, (await tmdb.SearchPosterAsync("Film", 1999, default)).TmdbId);
        Assert.Equal(3, handler.CallCount);
        Assert.Single(handler.Requests.Distinct());
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_a_retry_after_delay()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));
            return response;
        });
        using var tmdb = Service(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tmdb.SearchPosterAsync("Film", 1999, cancellation.Token));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task A_temporary_genre_list_failure_is_not_cached_for_the_entire_library_pass()
    {
        var clock = new Clock();
        var online = false;
        using var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = online
                ? Json("""{"genres":[{"id":18,"name":"Drama"}]}""")
                : Json("{}", HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        });
        using var tmdb = new TmdbService("test-placeholder", System.IO.Path.Combine(_log.Directory, "posters"),
            "w342", false, handler, clock);

        Assert.Empty(await tmdb.GenreNamesAsync(default));
        online = true;
        var failedCalls = handler.CallCount;
        Assert.Empty(await tmdb.GenreNamesAsync(default));
        Assert.Equal(failedCalls, handler.CallCount);
        clock.Now += TimeSpan.FromMinutes(1);

        Assert.Equal("Drama", (await tmdb.GenreNamesAsync(default))[18]);
        Assert.Equal(failedCalls + 1, handler.CallCount);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
