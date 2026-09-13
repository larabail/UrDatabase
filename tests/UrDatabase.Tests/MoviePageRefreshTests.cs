using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class MoviePageRefreshTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, ".movie-refresh-tests", Guid.NewGuid().ToString("N"));

    private AppConfig Config => new()
    {
        TmdbApiKey = "test-key",
        PosterCacheDir = Path.Combine(_root, "posters"),
        DatabasePath = Path.Combine(_root, "movies.db"),
        DownloadFolder = Path.Combine(_root, "downloads"),
        TmdbImageSize = "w780"
    };

    [Fact]
    public async Task CorrectedIdIsUsedWithoutSearchingOrRenamingTheMovie()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/movie/42/credits", HttpStatusCode.OK, """{"cast":[{"name":"Actor","character":"Role"}],"crew":[]}"""),
            ("/movie/42?", HttpStatusCode.OK, """{"id":42,"title":"Different spelling","overview":"Fresh plot","poster_path":"/new.jpg","backdrop_path":"/wide.jpg","imdb_id":"tt42","runtime":123,"genres":[{"name":"Drama"}]}"""));
        using var refresh = new MoviePageRefresh(Config, handler: handler);
        var movie = new MovieDetailsVm { LocalId = 7, Title = "My corrected title", Year = 2001, TmdbId = 42 };

        var result = await refresh.LoadAsync(movie);

        Assert.NotNull(result);
        Assert.Equal("", movie.Overview);
        Assert.True(result.TryApply(movie));
        Assert.Equal("Fresh plot", movie.Overview);
        Assert.Equal("My corrected title", movie.Title);
        Assert.Equal(2001, movie.Year);
        Assert.Equal(42, movie.TmdbId);
        Assert.Equal(7, movie.LocalId);
        Assert.Equal("https://image.tmdb.org/t/p/w780/new.jpg", movie.PosterPath);
        Assert.Equal("Actor (Role)", Assert.Single(movie.TopCast));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("search/movie"));
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task FailedMetadataAndCreditsKeepExistingContentAndReportFailure()
    {
        using var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable);
        using var refresh = new MoviePageRefresh(Config, handler: handler);
        var movie = new MovieDetailsVm
        {
            TmdbId = 42, Overview = "Keep this plot", PosterPath = "existing.jpg",
            TopCast = new() { "Known actor" }, ImdbRating = 8.1
        };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal("Keep this plot", movie.Overview);
        Assert.Equal("existing.jpg", movie.PosterPath);
        Assert.Equal("Known actor", Assert.Single(movie.TopCast));
        Assert.Equal(8.1, movie.ImdbRating);
        Assert.Contains(result.Notices, notice => notice.Contains("TMDB"));
    }

    [Fact]
    public async Task PartialCreditsFailureDoesNotEraseCastOrDiscardFreshPlot()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/credits", HttpStatusCode.ServiceUnavailable, "{}"),
            ("/movie/42?", HttpStatusCode.OK, """{"overview":"New plot"}"""));
        using var refresh = new MoviePageRefresh(Config, handler: handler);
        var movie = new MovieDetailsVm { TmdbId = 42, TopCast = new() { "Known actor" } };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal("New plot", movie.Overview);
        Assert.Equal("Known actor", Assert.Single(movie.TopCast));
        Assert.Contains(result.Notices, notice => notice.Contains("credits"));
    }

    [Fact]
    public async Task RemoteReloadKeepsCatalogueAndTransferIdentity()
    {
        using var refresh = new MoviePageRefresh(Config, movieLookup: (id, _) =>
        {
            Assert.Equal("server-id", id);
            return Task.FromResult<JellyfinMovie?>(new()
            {
                ItemId = id, Title = "Server spelling", Year = 2020, Overview = "Fresh server plot",
                Cast = new() { "Server actor" }, ImageTag = "new-tag"
            });
        });
        var movie = new MovieDetailsVm
        {
            IsRemote = true, LocalId = 7, RemoteId = "server-id", Title = "Catalogue name",
            Year = 2001, DownloadedPath = "my-copy.mkv", FilePath = "linked.mkv",
            FileMatch = PlayTargetKind.Linked, ResumePositionTicks = 123456789, ResumeNote = "Keep resume"
        };

        var result = await refresh.LoadAsync(movie);
        movie.DownloadedPath = "finished-while-loading.mkv";
        Assert.True(result!.TryApply(movie));
        Assert.Equal("Fresh server plot", movie.Overview);
        Assert.Equal("Catalogue name", movie.Title);
        Assert.Equal(2001, movie.Year);
        Assert.Equal("server-id", movie.RemoteId);
        Assert.Equal(7, movie.LocalId);
        Assert.Equal("finished-while-loading.mkv", movie.DownloadedPath);
        Assert.Equal("linked.mkv", movie.FilePath);
        Assert.Equal(PlayTargetKind.Linked, movie.FileMatch);
        Assert.Equal(123456789, movie.ResumePositionTicks);
        Assert.Equal("Keep resume", movie.ResumeNote);
    }

    [Fact]
    public async Task DuplicateRefreshIsCoalescedAndNavigationRejectsLateResult()
    {
        var response = new TaskCompletionSource<JellyfinMovie?>();
        var calls = 0;
        using var refresh = new MoviePageRefresh(Config, movieLookup: (_, _) =>
        {
            calls++;
            return response.Task;
        });
        var movie = new MovieDetailsVm { IsRemote = true, RemoteId = "old", Overview = "Old plot" };
        var first = refresh.LoadAsync(movie);

        Assert.Null(await refresh.LoadAsync(movie));
        Assert.Equal(1, calls);
        response.SetResult(new JellyfinMovie { ItemId = "old", Overview = "Late result" });
        var result = await first;

        Assert.False(result!.TryApply(new MovieDetailsVm { IsRemote = true, RemoteId = "new" }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(result.TryApply(movie, cts.Token));
        Assert.Equal("Old plot", movie.Overview);
    }

    [Fact]
    public async Task CorrectionDuringRequestRejectsStaleMetadata()
    {
        using var handler = FakeHttpMessageHandler.Json("""{"overview":"Old identity's plot","cast":[],"crew":[]}""");
        using var refresh = new MoviePageRefresh(Config, handler: handler);
        var movie = new MovieDetailsVm { TmdbId = 42, Overview = "Current plot" };
        var result = await refresh.LoadAsync(movie);

        movie.TmdbId = 99;

        Assert.False(result!.TryApply(movie));
        Assert.Equal("Current plot", movie.Overview);
    }

    [Fact]
    public async Task CancelledRequestNeverPublishesEvenWhenLookupIgnoresCancellation()
    {
        var response = new TaskCompletionSource<JellyfinMovie?>();
        using var refresh = new MoviePageRefresh(Config, movieLookup: (_, _) => response.Task);
        using var cts = new CancellationTokenSource();
        var movie = new MovieDetailsVm { IsRemote = true, RemoteId = "id", Overview = "Existing" };
        var pending = refresh.LoadAsync(movie, cts.Token);

        cts.Cancel();
        response.SetResult(new JellyfinMovie { ItemId = "id", Overview = "Late" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("Existing", movie.Overview);
        Assert.False(refresh.IsRunning);
    }

    [Fact]
    public async Task LocalMovieCanRecoverServerArtworkWithoutOverwritingCorrectedMetadata()
    {
        var config = Config;
        config.TmdbApiKey = "";
        using var jellyfin = new JellyfinClient(new JellyfinSettings { ServerUrl = "https://server.example" });
        using var refresh = new MoviePageRefresh(config, jellyfin: jellyfin, movieLookup: (id, _) =>
            Task.FromResult<JellyfinMovie?>(new()
            {
                ItemId = id, Overview = "Other server plot", Genres = "Comedy",
                ImageTag = "new-tag", Cast = new() { "Server actor" }
            }));
        var movie = new MovieDetailsVm
        {
            IsOnServer = true, RemoteId = "copy-id", TmdbId = 42,
            Overview = "Corrected TMDB plot", Genres = "Drama"
        };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal("Corrected TMDB plot", movie.Overview);
        Assert.Equal("Drama", movie.Genres);
        Assert.Equal(42, movie.TmdbId);
        Assert.Equal(jellyfin.BuildPrimaryImageUrl("copy-id", "new-tag"), movie.PosterPath);
        Assert.Equal("Server actor", Assert.Single(movie.TopCast));
    }

    [Fact]
    public async Task RetryReadsPersistedIdentityAndBypassesBrokenDiskArtwork()
    {
        Directory.CreateDirectory(_root);
        var config = Config;
        var broken = Path.Combine(_root, "poster.jpg");
        File.WriteAllText(broken, "not a poster");
        using (var conn = Database.Open(config.DatabasePath))
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO movies(id,title,tmdb_id) VALUES(7,'My title',42)";
            insert.ExecuteNonQuery();
        }
        using var handler = FakeHttpMessageHandler.Routed(
            ("/credits", HttpStatusCode.OK, """{"cast":[],"crew":[]}"""),
            ("/movie/42?", HttpStatusCode.OK, """{"poster_path":"/fixed.jpg","overview":"Fresh"}"""));
        using var refresh = new MoviePageRefresh(config, config.DatabasePath, handler: handler);
        var movie = new MovieDetailsVm { LocalId = 7, Title = "My title", PosterPath = broken };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal(42, movie.TmdbId);
        Assert.Equal("https://image.tmdb.org/t/p/w780/fixed.jpg", movie.PosterPath);
        Assert.Equal("not a poster", File.ReadAllText(broken));
        Assert.DoesNotContain(handler.Requests, url => url.Contains("search/movie"));
    }

    [Fact]
    public async Task MissingCachedRatingIsRetriedButARealRatingIsNeverEvicted()
    {
        Directory.CreateDirectory(_root);
        var config = Config;
        config.TmdbApiKey = "";
        config.OmdbApiKey = "test-key";
        using (var conn = Database.Open(config.DatabasePath))
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO imdb_ratings(imdb_id,rating,fetched_at,source) VALUES
                ('tt-missing',NULL,'2026-01-01','omdb'),('tt-rated',8.3,'2026-01-01','omdb');
                """;
            insert.ExecuteNonQuery();
        }
        var calls = 0;
        using var refresh = new MoviePageRefresh(config, config.DatabasePath, ratingLookup: (id, _, _) =>
        {
            calls++;
            using var conn = Database.Connect(config.DatabasePath);
            using var read = conn.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM imdb_ratings WHERE imdb_id='tt-missing'";
            Assert.Equal(0L, read.ExecuteScalar());
            read.CommandText = "SELECT rating FROM imdb_ratings WHERE imdb_id='tt-rated'";
            Assert.Equal(8.3, read.ExecuteScalar());
            return Task.FromResult<double?>(7.4);
        });
        var movie = new MovieDetailsVm { ImdbId = "tt-missing" };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal(1, calls);
        Assert.Equal(7.4, movie.ImdbRating);
    }

    [Fact]
    public async Task FailedRemoteRequestCanBeRetriedAndCannotReplaceTheRequestedItem()
    {
        var calls = 0;
        using var refresh = new MoviePageRefresh(Config, movieLookup: (_, _) =>
        {
            calls++;
            return Task.FromResult<JellyfinMovie?>(calls == 1
                ? new() { ItemId = "wrong", Overview = "Wrong movie" }
                : new() { ItemId = "wanted", Overview = "Retried plot" });
        });
        var movie = new MovieDetailsVm { IsRemote = true, RemoteId = "wanted", Overview = "Existing" };

        var first = await refresh.LoadAsync(movie);
        Assert.True(first!.TryApply(movie));
        Assert.Equal("Existing", movie.Overview);
        Assert.NotEmpty(first.Notices);
        var second = await refresh.LoadAsync(movie);

        Assert.True(second!.TryApply(movie));
        Assert.Equal(2, calls);
        Assert.Equal("Retried plot", movie.Overview);
        Assert.Empty(second.Notices);
    }

    [Fact]
    public async Task NewImdbIdentityCannotInheritThePreviousFilmsRating()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/credits", HttpStatusCode.OK, """{"cast":[],"crew":[]}"""),
            ("/movie/42?", HttpStatusCode.OK, """{"imdb_id":"tt-new"}"""));
        using var refresh = new MoviePageRefresh(Config, handler: handler,
            ratingLookup: (_, _, _) => Task.FromResult<double?>(null));
        var movie = new MovieDetailsVm { TmdbId = 42, ImdbId = "tt-old", ImdbRating = 8.8 };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal("tt-new", movie.ImdbId);
        Assert.Null(movie.ImdbRating);
    }

    [Fact]
    public async Task FailedEnrichmentKeepsKnownValuesAndReportsEachFailure()
    {
        var config = Config;
        config.TmdbApiKey = "";
        using var refresh = new MoviePageRefresh(config,
            ratingLookup: (_, _, _) => throw new HttpRequestException("Offline"),
            awardsLookup: (_, _, _) => throw new HttpRequestException("Offline"),
            relatedLookup: (_, _) => throw new HttpRequestException("Offline"));
        var movie = new MovieDetailsVm { ImdbId = "tt42", ImdbRating = 8.1 };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal(8.1, movie.ImdbRating);
        Assert.Null(result.Related);
        Assert.Contains(result.Notices, notice => notice.Contains("IMDb"));
        Assert.Contains(result.Notices, notice => notice.Contains("Academy Awards"));
        Assert.Contains(result.Notices, notice => notice.Contains("Related films"));
    }

    [Fact]
    public async Task MissingKeyDoesNotMakeARequestAndExplainsWhyDetailsWereKept()
    {
        var config = Config;
        config.TmdbApiKey = "";
        using var handler = FakeHttpMessageHandler.Json("{}");
        using var refresh = new MoviePageRefresh(config, handler: handler);
        var movie = new MovieDetailsVm { Overview = "Cached plot" };

        var result = await refresh.LoadAsync(movie);

        Assert.True(result!.TryApply(movie));
        Assert.Equal(0, handler.CallCount);
        Assert.Equal("Cached plot", movie.Overview);
        Assert.Contains(result.Notices, notice => notice.Contains("not configured"));
    }

    public void Dispose()
    {
        _log.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
