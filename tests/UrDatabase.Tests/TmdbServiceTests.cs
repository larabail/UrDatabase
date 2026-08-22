using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class TmdbServiceTests
    {
        private static TmdbService Create(FakeHttpMessageHandler handler, string apiKey = "test-key", string imageSize = "w342")
            => new(apiKey, posterCacheDir: "", imageSize: imageSize, downloadPosters: false, handler: handler);

        // ---------- URL construction ----------

        [Fact]
        public void Search_url_targets_the_tmdb_search_endpoint()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"));

            var url = svc.BuildSearchUrl("The Movie", null);

            Assert.StartsWith("https://api.themoviedb.org/3/search/movie?", url);
            Assert.Contains("api_key=test-key", url);
            Assert.Contains("query=The%20Movie", url);
        }

        [Fact]
        public void Search_url_includes_a_plausible_year_and_omits_an_implausible_one()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"));

            Assert.Contains("&year=1999", svc.BuildSearchUrl("The Movie", 1999));
            Assert.DoesNotContain("year=", svc.BuildSearchUrl("The Movie", 1200));
            Assert.DoesNotContain("year=", svc.BuildSearchUrl("The Movie", null));
        }

        [Fact]
        public void Search_url_escapes_special_characters()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"), apiKey: "key/with+chars");

            var url = svc.BuildSearchUrl("Am\u00e9lie & Co", null);

            Assert.Contains("key%2Fwith%2Bchars", url);
            Assert.Contains("%26", url);
            Assert.DoesNotContain(" ", url);
        }

        [Fact]
        public void Details_and_credits_urls_are_built_from_the_tmdb_id()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"));

            Assert.StartsWith("https://api.themoviedb.org/3/movie/550?", svc.BuildDetailsUrl(550));
            Assert.StartsWith("https://api.themoviedb.org/3/movie/550/credits?", svc.BuildCreditsUrl(550));
        }

        [Fact]
        public void Image_url_uses_the_configured_size_and_tolerates_a_leading_slash()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"), imageSize: "w500");

            Assert.Equal("https://image.tmdb.org/t/p/w500/abc123.jpg", svc.BuildImageUrl("/abc123.jpg"));
            Assert.Equal("https://image.tmdb.org/t/p/w500/abc123.jpg", svc.BuildImageUrl("abc123.jpg"));
        }

        [Fact]
        public void Image_url_falls_back_to_a_default_size_when_none_is_configured()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"), imageSize: "   ");

            Assert.Contains("/w342/", svc.BuildImageUrl("abc.jpg"));
        }

        // ---------- JSON parsing ----------

        [Fact]
        public async Task Search_parses_the_first_result()
        {
            var handler = FakeHttpMessageHandler.Json(@"{
                ""results"": [
                    { ""id"": 550, ""poster_path"": ""/first.jpg"" },
                    { ""id"": 551, ""poster_path"": ""/second.jpg"" }
                ]
            }");
            using var svc = Create(handler);

            var (id, poster) = await svc.SearchPosterAsync("Fight Club", 1999, CancellationToken.None);

            Assert.Equal(550, id);
            Assert.Equal("/first.jpg", poster);
        }

        [Fact]
        public async Task Search_returns_nothing_for_an_empty_result_set()
        {
            using var svc = Create(FakeHttpMessageHandler.Json(@"{ ""results"": [] }"));

            var (id, poster) = await svc.SearchPosterAsync("Nothing", null, CancellationToken.None);

            Assert.Null(id);
            Assert.Null(poster);
        }

        [Fact]
        public async Task Search_makes_no_request_without_an_api_key()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""results"": [] }");
            using var svc = Create(handler, apiKey: "");

            var (id, _) = await svc.SearchPosterAsync("Fight Club", null, CancellationToken.None);

            Assert.Null(id);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task Search_returns_nothing_on_an_http_error()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError));

            var (id, _) = await svc.SearchPosterAsync("Fight Club", null, CancellationToken.None);

            Assert.Null(id);
        }

        [Fact]
        public async Task Details_parse_snake_case_fields_that_case_insensitive_matching_alone_would_miss()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 550, ""poster_path"": ""/p.jpg"" } ] }"),
                ("movie/550", HttpStatusCode.OK, @"{
                    ""id"": 550,
                    ""title"": ""Fight Club"",
                    ""overview"": ""A ticking-time-bomb insomniac."",
                    ""backdrop_path"": ""/backdrop.jpg"",
                    ""imdb_id"": ""tt0137523"",
                    ""runtime"": 139,
                    ""vote_average"": 8.4,
                    ""genres"": [ { ""id"": 18, ""name"": ""Drama"" } ]
                }"));
            using var svc = Create(handler);

            var details = await svc.GetDetailsByTitleAsync("Fight Club", 1999, CancellationToken.None);

            Assert.NotNull(details);
            Assert.Equal(550, details!.Id);
            Assert.Equal("Fight Club", details.Title);
            Assert.Equal(139, details.Runtime);
            Assert.Equal("/backdrop.jpg", details.BackdropPath);
            Assert.Equal(8.4, details.VoteAverage);
            Assert.Equal("Drama", Assert.Single(details.Genres).Name);
        }

        [Fact]
        public async Task Details_parse_the_imdb_id_so_ratings_can_match_exactly()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 550 } ] }"),
                ("movie/550", HttpStatusCode.OK, @"{ ""id"": 550, ""imdb_id"": ""tt0137523"" }"));
            using var svc = Create(handler);

            var details = await svc.GetDetailsByTitleAsync("Fight Club", null, CancellationToken.None);

            Assert.Equal("tt0137523", details!.ImdbId);
        }

        [Fact]
        public async Task Details_report_no_imdb_id_when_tmdb_omits_it()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 550 } ] }"),
                ("movie/550", HttpStatusCode.OK, @"{ ""id"": 550, ""title"": ""Untitled"" }"));
            using var svc = Create(handler);

            var details = await svc.GetDetailsByTitleAsync("Untitled", null, CancellationToken.None);

            Assert.NotNull(details);
            Assert.Null(details!.ImdbId);
        }

        [Fact]
        public async Task Details_return_null_when_the_search_finds_nothing()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""results"": [] }");
            using var svc = Create(handler);

            Assert.Null(await svc.GetDetailsByTitleAsync("Unknown", null, CancellationToken.None));
        }

        [Fact]
        public async Task Credits_parse_cast_and_crew()
        {
            var handler = FakeHttpMessageHandler.Json(@"{
                ""cast"": [ { ""name"": ""Edward Norton"", ""character"": ""The Narrator"" } ],
                ""crew"": [ { ""name"": ""David Fincher"", ""job"": ""Director"" } ]
            }");
            using var svc = Create(handler);

            var credits = await svc.GetCreditsByIdAsync(550, CancellationToken.None);

            Assert.Equal("Edward Norton", Assert.Single(credits!.Cast).Name);
            Assert.Equal("Director", Assert.Single(credits.Crew).Job);
        }

        [Fact]
        public async Task Credits_return_null_on_an_http_error()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));

            Assert.Null(await svc.GetCreditsByIdAsync(550, CancellationToken.None));
        }
    }
}
