using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Genres, which nothing in this app used to write for a scanned film.
    ///
    /// Every film from a scan therefore sat in a single Uncategorised bucket for the life of the
    /// library, which on a library of a few thousand is one shelf holding all of it. The fix takes
    /// the genres out of the search TMDB is already asked in order to find a poster, so it costs no
    /// extra request per film — and that is the part most worth protecting, because the obvious
    /// implementation asks <c>/movie/{id}</c> per film and doubles a rate-limited budget.
    ///
    /// Nothing here reaches TMDB.
    /// </summary>
    public class TmdbGenreTests : IDisposable
    {
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        private const string GenreList = @"{ ""genres"": [
            { ""id"": 28, ""name"": ""Action"" },
            { ""id"": 18, ""name"": ""Drama"" },
            { ""id"": 878, ""name"": ""Science Fiction"" } ] }";

        private static string SearchHit(string title, string genreIds) =>
            $@"{{ ""results"": [ {{ ""id"": 550, ""title"": ""{title}"", ""release_date"": ""1999-05-01"",
                 ""poster_path"": ""/poster.jpg"", ""genre_ids"": [{genreIds}] }} ] }}";

        private static TmdbService Service(FakeHttpMessageHandler handler) =>
            new(apiKey: "not-a-real-key", posterCacheDir: "", imageSize: "w342", downloadPosters: false, handler: handler);

        private static FakeHttpMessageHandler Routed(string searchJson) =>
            FakeHttpMessageHandler.Routed(
                ("/genre/movie/list", HttpStatusCode.OK, GenreList),
                ("/search/movie", HttpStatusCode.OK, searchJson));

        // ---------- naming the ids ----------

        [Fact]
        public async Task A_film_comes_back_with_its_genres_spelled_out()
        {
            using var handler = Routed(SearchHit("Any Film", "28, 18"));
            using var tmdb = Service(handler);

            var (id, poster, genres) = await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

            Assert.Equal(550, id);
            Assert.Equal("/poster.jpg", poster);
            Assert.Equal("Action, Drama", genres);
        }

        /// <summary>
        /// TMDB adds genres from time to time. A shelf headed "10752" is worse than a film filed
        /// under one fewer genre than it has.
        /// </summary>
        [Fact]
        public async Task An_id_the_list_does_not_explain_is_left_out()
        {
            using var handler = Routed(SearchHit("Any Film", "28, 10752"));
            using var tmdb = Service(handler);

            var (_, _, genres) = await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

            Assert.Equal("Action", genres);
        }

        /// <summary>
        /// Null rather than an empty string, because the caller writes null straight through as
        /// "leave the column alone". An empty string would blank a film somebody had filed by hand.
        /// </summary>
        [Fact]
        public async Task A_film_TMDB_files_under_nothing_reports_no_genres()
        {
            using var handler = Routed(SearchHit("Any Film", ""));
            using var tmdb = Service(handler);

            var (id, _, genres) = await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

            Assert.Equal(550, id);
            Assert.Null(genres);
        }

        [Fact]
        public async Task A_film_TMDB_cannot_confirm_reports_nothing_at_all()
        {
            // The result names a different film, which TmdbMatch refuses — the rule that stops one
            // film wearing another's poster, and which must stop it wearing its genres too.
            using var handler = Routed(SearchHit("A Different Film Entirely", "28"));
            using var tmdb = Service(handler);

            var (id, poster, genres) = await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

            Assert.Null(id);
            Assert.Null(poster);
            Assert.Null(genres);
        }

        // ---------- the budget ----------

        /// <summary>
        /// The whole reason genres are affordable. The names are the same for every film, so they
        /// are fetched once; asking per film would double the requests a library makes.
        /// </summary>
        [Fact]
        public async Task The_genre_list_is_fetched_once_for_the_whole_library()
        {
            using var handler = Routed(SearchHit("Any Film", "28"));
            using var tmdb = Service(handler);

            for (var i = 0; i < 10; i++)
                await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

            Assert.Equal(1, handler.Requests.Count(r => r.Contains("/genre/movie/list", StringComparison.Ordinal)));
            Assert.Equal(10, handler.Requests.Count(r => r.Contains("/search/movie", StringComparison.Ordinal)));
        }

        /// <summary>
        /// Four fetches run at once by design, and on the first launch all four reach this at the
        /// same moment. Without the gate behind it each of them asked.
        /// </summary>
        [Fact]
        public async Task Several_fetches_at_once_share_one_request_for_the_list()
        {
            using var handler = Routed(SearchHit("Any Film", "28"));
            using var tmdb = Service(handler);

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                (Task)tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None)));

            Assert.Equal(1, handler.Requests.Count(r => r.Contains("/genre/movie/list", StringComparison.Ordinal)));
        }

        /// <summary>
        /// A library warming with no network would otherwise retry this once per film. The failure
        /// is not per-film information, so it is remembered until the app is restarted.
        /// </summary>
        [Fact]
        public async Task A_genre_list_that_could_not_be_read_is_not_asked_for_again()
        {
            using var handler = FakeHttpMessageHandler.Routed(
                ("/genre/movie/list", HttpStatusCode.InternalServerError, "{}"),
                ("/search/movie", HttpStatusCode.OK, SearchHit("Any Film", "28")));

            using var tmdb = Service(handler);

            for (var i = 0; i < 5; i++)
            {
                var (id, _, genres) = await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

                // The film is still identified and still gets its poster.
                Assert.Equal(550, id);
                Assert.Null(genres);
            }

            Assert.Equal(1, handler.Requests.Count(r => r.Contains("/genre/movie/list", StringComparison.Ordinal)));
        }

        /// <summary>
        /// A build with no key makes no request at all, here as everywhere else.
        /// </summary>
        [Fact]
        public async Task No_key_means_no_request()
        {
            using var handler = new FakeHttpMessageHandler(
                _ => throw new InvalidOperationException("no request should have been made"));

            using var tmdb = new TmdbService(
                apiKey: "", posterCacheDir: "", imageSize: "w342", downloadPosters: false, handler: handler);

            var (id, _, genres) = await tmdb.SearchFilmAsync("Any Film", 1999, CancellationToken.None);

            Assert.Null(id);
            Assert.Null(genres);
            Assert.Equal(0, handler.CallCount);
        }

        // ---------- the url ----------

        [Fact]
        public void The_genre_list_url_carries_the_key_and_asks_for_films()
        {
            using var handler = FakeHttpMessageHandler.Json("{}");
            using var tmdb = Service(handler);

            var url = tmdb.BuildGenreListUrl();

            Assert.StartsWith($"{TmdbService.ApiBaseUrl}/genre/movie/list", url, StringComparison.Ordinal);
            Assert.Contains("api_key=not-a-real-key", url, StringComparison.Ordinal);
        }
    }
}
