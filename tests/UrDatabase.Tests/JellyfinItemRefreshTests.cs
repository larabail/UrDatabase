using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class JellyfinItemRefreshTests : IDisposable
{
    private readonly TempLog _log = new();
    private const string Server = "https://media.invalid/jellyfin";
    private const string Users = """[{"Id":"viewer","Name":"Viewer"}]""";

    private static JellyfinClient Client(HttpMessageHandler handler) => new(
        new JellyfinSettings { ServerUrl = Server, ApiKey = "test-key" },
        deviceId: "test-device", handler: handler);

    private static FakeHttpMessageHandler Handler(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        FakeHttpMessageHandler.Routed(
            ("/Items/", status, json),
            ("/Users", HttpStatusCode.OK, Users));

    [Fact]
    public async Task MovieFetchIsUserScopedAuthenticatedAndUsesTheExistingDescriptionMapping()
    {
        using var handler = Handler("""
            {"Id":"movie-id","Type":"Movie","Name":" Refreshed film ","ProductionYear":2001,
             "Overview":" Fresh plot ","Genres":["Drama"],"RunTimeTicks":60000000000,
             "ProviderIds":{"Imdb":"tt42","Tmdb":"42"},"ImageTags":{"Primary":"new-poster"},
             "People":[{"Name":"Actor","Type":"Actor","Role":"Role"},{"Name":"Director","Type":"Director"}],
             "MediaStreams":[{"Type":"Video","Width":1920,"Height":1080,"Codec":"h264"}]}
            """);
        using var client = Client(handler);

        var movie = await client.GetMovieAsync("movie-id");

        Assert.NotNull(movie);
        Assert.Equal("Refreshed film", movie.Title);
        Assert.Equal("Fresh plot", movie.Overview);
        Assert.Equal("Drama", movie.Genres);
        Assert.Equal(2001, movie.Year);
        Assert.Equal(100, movie.RuntimeMinutes);
        Assert.Equal("tt42", movie.ImdbId);
        Assert.Equal("42", movie.TmdbId);
        Assert.Equal("new-poster", movie.ImageTag);
        Assert.Equal("Actor (Role)", Assert.Single(movie.Cast));
        Assert.Equal("Director: Director", Assert.Single(movie.Crew));
        Assert.Equal(1920, movie.Media!.Width);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal($"{Server}/Users/viewer/Items/movie-id?Fields={JellyfinClient.ItemFields}", handler.Requests.Last());
        Assert.Contains("Token=\"test-key\"", handler.RawAuthorizationHeaders.Last());
        Assert.DoesNotContain(handler.Requests, url => url.Contains("/Views") || url.Contains("Recursive="));
    }

    [Fact]
    public async Task SeriesFetchSignsInAndRequestsSeriesFieldsWithoutListingEpisodes()
    {
        using var handler = FakeHttpMessageHandler.Routed(
            ("/AuthenticateByName", HttpStatusCode.OK, """{"AccessToken":"issued-token","User":{"Id":"viewer","Name":"Viewer"}}"""),
            ("/Items/", HttpStatusCode.OK, """
                {"Id":"series-id","Type":"Series","Name":"Refreshed series","Overview":"Fresh plot",
                 "ChildCount":3,"RecursiveItemCount":24,"Genres":["Drama"],"ImageTags":{"Primary":"new-poster"},
                 "ProviderIds":{"Imdb":"tt43"},"People":[{"Name":"Actor","Type":"Actor"}]}
                """));
        using var client = new JellyfinClient(
            new JellyfinSettings { ServerUrl = Server, Username = "Viewer", Password = "test-password" },
            deviceId: "test-device", handler: handler);

        var series = await client.GetSeriesDetailsAsync("series-id");

        Assert.NotNull(series);
        Assert.Equal("Refreshed series", series.Title);
        Assert.Equal("Fresh plot", series.Overview);
        Assert.Equal(3, series.SeasonCount);
        Assert.Equal(24, series.EpisodeCount);
        Assert.Equal("tt43", series.ImdbId);
        Assert.Equal("new-poster", series.ImageTag);
        Assert.Equal("Actor", Assert.Single(series.Cast));
        Assert.Equal(2, handler.CallCount);
        Assert.Equal($"{Server}/Users/viewer/Items/series-id?Fields={JellyfinClient.SeriesFields}", handler.Requests.Last());
        Assert.Contains("Token=\"issued-token\"", handler.RawAuthorizationHeaders.Last());
        Assert.DoesNotContain("MediaStreams", handler.Requests.Last());
        Assert.DoesNotContain(handler.Requests, url => url.Contains("/Episodes") || url.Contains("/Seasons") || url.Contains("/Views"));
    }

    [Theory]
    [InlineData(false, "Series")]
    [InlineData(false, "Episode")]
    [InlineData(false, "Season")]
    [InlineData(false, "")]
    [InlineData(true, "Movie")]
    [InlineData(true, "Episode")]
    [InlineData(true, "")]
    public async Task AReturnOfTheWrongKindCannotBeTreatedAsTheRequestedPage(bool series, string type)
    {
        using var handler = Handler($$"""{"Id":"item-id","Type":"{{type}}","Name":"Wrong kind"}""");
        using var client = Client(handler);

        if (series) Assert.Null(await client.GetSeriesDetailsAsync("item-id"));
        else Assert.Null(await client.GetMovieAsync("item-id"));
    }

    [Theory]
    [InlineData(false, " movie ")]
    [InlineData(true, " series ")]
    public async Task TypeValidationAcceptsOrdinaryCaseAndWhitespaceDifferences(bool series, string type)
    {
        using var handler = Handler($$"""{"Id":"item-id","Type":"{{type}}","Name":"Right kind"}""");
        using var client = Client(handler);

        if (series) Assert.NotNull(await client.GetSeriesDetailsAsync("item-id"));
        else Assert.NotNull(await client.GetMovieAsync("item-id"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingItemsReturnNullRatherThanMisdiagnosingTheServerAddress(bool series)
    {
        using var handler = Handler("{}", HttpStatusCode.NotFound);
        using var client = Client(handler);

        if (series) Assert.Null(await client.GetSeriesDetailsAsync("item-id"));
        else Assert.Null(await client.GetMovieAsync("item-id"));
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.InternalServerError)]
    public async Task ActualServerFailuresRemainVisibleToTheRefreshingPage(bool series, HttpStatusCode status)
    {
        using var handler = Handler("{}", status);
        using var client = Client(handler);

        if (series) await Assert.ThrowsAsync<JellyfinException>(() => client.GetSeriesDetailsAsync("item-id"));
        else await Assert.ThrowsAsync<JellyfinException>(() => client.GetMovieAsync("item-id"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResponseForADifferentItemIsRejected(bool series)
    {
        var type = series ? "Series" : "Movie";
        using var handler = Handler($$"""{"Id":"different-id","Type":"{{type}}","Name":"Wrong item"}""");
        using var client = Client(handler);

        if (series) Assert.Null(await client.GetSeriesDetailsAsync("wanted-id"));
        else Assert.Null(await client.GetMovieAsync("wanted-id"));
    }

    [Fact]
    public async Task RefreshFetchesAgainWithoutSigningInAgainOrListingTheLibrary()
    {
        var items = 0;
        using var handler = new FakeHttpMessageHandler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/Items/movie-id")
                ? $$"""{"Id":"movie-id","Type":"Movie","Name":"Version {{++items}}"}"""
                : Users)
        });
        using var client = Client(handler);

        Assert.Equal("Version 1", (await client.GetMovieAsync("movie-id"))!.Title);
        Assert.Equal("Version 2", (await client.GetMovieAsync("movie-id"))!.Title);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task MoviePageRefreshUsesSingleItemRetrievalWhenNoCallbackWasSupplied()
    {
        using var handler = Handler("""{"Id":"movie-id","Type":"Movie","Name":"Server name","Overview":"Fresh plot"}""");
        using var client = Client(handler);
        using var refresh = new MoviePageRefresh(null, jellyfin: client);
        var current = new MovieDetailsVm { IsRemote = true, RemoteId = "movie-id", Title = "Catalogue name" };

        var result = await refresh.LoadAsync(current);

        Assert.True(result!.TryApply(current));
        Assert.Equal("Fresh plot", current.Overview);
        Assert.Equal("Catalogue name", current.Title);
        Assert.Equal(2, handler.CallCount);
        Assert.NotNull(current.StreamUrl);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task TheItemIdIsEscapedAsOnePathSegment()
    {
        const string id = "item/with?reserved#characters";
        using var handler = Handler($$"""{"Id":"{{id}}","Type":"Movie","Name":"A film"}""");
        using var client = Client(handler);

        Assert.NotNull(await client.GetMovieAsync(id));
        Assert.Contains($"/Items/{Uri.EscapeDataString(id)}?Fields=", handler.Requests.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlankIdsAreRejectedWithoutAnyNetworkCall(bool series)
    {
        using var handler = Handler("{}");
        using var client = Client(handler);

        if (series) await Assert.ThrowsAsync<JellyfinException>(() => client.GetSeriesDetailsAsync(" "));
        else await Assert.ThrowsAsync<JellyfinException>(() => client.GetMovieAsync(" "));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreventsPublishingAnItemReturnedByAHandlerThatIgnoresIt(bool series)
    {
        using var cts = new CancellationTokenSource();
        using var handler = new FakeHttpMessageHandler(request =>
        {
            var body = Users;
            if (request.RequestUri!.AbsolutePath.Contains("/Items/"))
            {
                cts.Cancel();
                body = $$"""{"Id":"item-id","Type":"{{(series ? "Series" : "Movie")}}","Name":"Late reply"}""";
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var client = Client(handler);

        if (series) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSeriesDetailsAsync("item-id", cts.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMovieAsync("item-id", cts.Token));
    }

    public void Dispose() => _log.Dispose();
}
