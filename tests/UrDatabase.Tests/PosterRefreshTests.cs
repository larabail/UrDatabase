using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class PosterRefreshTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urdb-refresh-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "movies.db");

    public PosterRefreshTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private void Seed(string? poster, string? genres = null)
    {
        using var conn = Database.Open(DbPath);
        conn.Execute("INSERT INTO movies (id, title, year, tmdb_id, poster_path, genres) VALUES (1, 'Film', 1999, 42, @poster, @genres)",
            new { poster, genres });
    }

    private PosterAutoLoader Loader(HttpMessageHandler handler) => new(new AppConfig
    {
        DatabasePath = DbPath,
        PosterCacheDir = Path.Combine(_dir, "posters"),
        TmdbApiKey = "test-placeholder",
        DownloadPosters = false
    }, DbPath, handler: handler);

    [Fact]
    public async Task A_download_restored_to_the_same_path_explicitly_reloads_the_card()
    {
        var path = Path.Combine(_dir, "posters", "1.jpg");
        Seed(path, "Drama");
        using var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "image.tmdb.org")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="))
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"poster_path":"/fresh.jpg","genres":[{"id":18,"name":"Drama"}]}""")
            };
        });
        using var loader = new PosterAutoLoader(new AppConfig
        {
            DatabasePath = DbPath,
            PosterCacheDir = Path.Combine(_dir, "posters"),
            TmdbApiKey = "test-placeholder",
            DownloadPosters = true
        }, DbPath, handler: handler);
        Enrichment result = default;

        await loader.EnsurePosterAsync(1, "Film", 1999, found => result = found, default);

        Assert.Equal(path, result.PosterPath);
        Assert.True(File.Exists(path));
        Assert.True(result.ArtworkRepaired);
    }

    [Fact]
    public async Task A_missing_cached_file_is_repaired_using_the_stored_identity()
    {
        Seed(Path.Combine(_dir, "deleted.jpg"), "Drama");
        using var handler = FakeHttpMessageHandler.Routed(
            ("/movie/42", HttpStatusCode.OK, """{"id":42,"poster_path":"/fresh.jpg","genres":[{"id":18,"name":"Drama"}]}"""));
        using var loader = Loader(handler);
        Enrichment result = default;

        await loader.EnsurePosterAsync(1, "Film", 1999, found => result = found, default);

        Assert.EndsWith("/fresh.jpg", result.PosterPath);
        Assert.Single(handler.Requests);
        Assert.Contains("/movie/42", handler.Requests[0]);
        using var conn = Database.Open(DbPath);
        Assert.Equal(result.PosterPath, conn.QuerySingle<string>("SELECT poster_path FROM movies WHERE id=1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \t\r\n")]
    public async Task Existing_artwork_does_not_prevent_missing_genres_from_being_filled(string? genres)
    {
        const string poster = "https://example.invalid/chosen.jpg";
        Seed(poster, genres);
        using var handler = FakeHttpMessageHandler.Routed(
            ("/movie/42", HttpStatusCode.OK, """{"id":42,"poster_path":"/different.jpg","genres":[{"id":18,"name":"Drama"}]}"""));
        using var loader = Loader(handler);
        Enrichment result = default;

        await loader.EnsurePosterAsync(1, "Film", 1999, found => result = found, default);

        Assert.Equal("Drama", result.Genres);
        Assert.Equal(poster, result.PosterPath);
        using var conn = Database.Open(DbPath);
        Assert.Equal("Drama", conn.QuerySingle<string>("SELECT genres FROM movies WHERE id=1"));
        Assert.Equal(poster, conn.QuerySingle<string>("SELECT poster_path FROM movies WHERE id=1"));
    }

    [Fact]
    public async Task Refresh_retries_a_failed_lookup_but_an_ordinary_reload_does_not()
    {
        Seed(null);
        var online = false;
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(online ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"id":42,"poster_path":"/fresh.jpg","genres":[{"id":18,"name":"Drama"}]}""")
        });
        using var loader = Loader(handler);
        await loader.EnsurePosterAsync(1, "Film", 1999, _ => { }, default);
        online = true;
        await loader.EnsurePosterAsync(1, "Film", 1999, _ => { }, default);
        Assert.Single(handler.Requests);

        await loader.RetryMissingAsync();
        Enrichment result = default;
        await loader.EnsurePosterAsync(1, "Film", 1999, found => result = found, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/fresh.jpg", result.PosterPath);
        Assert.Equal("Drama", result.Genres);
    }

    [Fact]
    public async Task Refresh_does_not_duplicate_an_in_flight_lookup()
    {
        Seed(null);
        using var handler = new HeldRequest();
        using var loader = Loader(handler);
        var first = loader.EnsurePosterAsync(1, "Film", 1999, _ => { }, default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await loader.RetryMissingAsync();
            await loader.EnsurePosterAsync(1, "Film", 1999, _ => { }, default);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            handler.Release.TrySetResult();
            await first;
        }
    }

    private sealed class HeldRequest : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":42,"poster_path":"/fresh.jpg","genres":[{"id":18,"name":"Drama"}]}""")
            };
        }
    }
}
