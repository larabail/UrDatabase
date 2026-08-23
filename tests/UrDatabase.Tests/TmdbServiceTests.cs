using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class TmdbServiceTests : IDisposable
    {
        // A failed recommendations lookup is logged rather than thrown.
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

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
        public async Task Search_returns_the_result_whose_title_and_year_agree_rather_than_the_first()
        {
            // TMDB's own order, with its most popular near miss in front. Taking that one is
            // exactly the bug: El Drama got El Sabor del Drama's poster and kept it.
            var handler = FakeHttpMessageHandler.Json(@"{
                ""results"": [
                    { ""id"": 900, ""title"": ""Fight Club Confidential"", ""release_date"": ""1999-01-01"", ""poster_path"": ""/wrong.jpg"" },
                    { ""id"": 550, ""title"": ""Fight Club"", ""release_date"": ""1999-10-15"", ""poster_path"": ""/right.jpg"" }
                ]
            }");
            using var svc = Create(handler);

            var (id, poster) = await svc.SearchPosterAsync("Fight Club", 1999, CancellationToken.None);

            Assert.Equal(550, id);
            Assert.Equal("/right.jpg", poster);
        }

        [Fact]
        public async Task Search_returns_nothing_when_no_result_is_this_film()
        {
            var handler = FakeHttpMessageHandler.Json(@"{
                ""results"": [
                    { ""id"": 900, ""title"": ""El Sabor del Drama"", ""release_date"": ""2019-01-01"", ""poster_path"": ""/wrong.jpg"" }
                ]
            }");
            using var svc = Create(handler);

            var (id, poster) = await svc.SearchPosterAsync("El Drama", 2026, CancellationToken.None);

            Assert.Null(id);
            Assert.Null(poster);
        }

        [Fact]
        public async Task Every_result_is_offered_to_the_picker_including_the_ones_the_match_rules_refuse()
        {
            var handler = FakeHttpMessageHandler.Json(@"{
                ""results"": [
                    { ""id"": 900, ""title"": ""El Sabor del Drama"", ""original_title"": ""El Sabor del Drama"", ""release_date"": ""2019-01-01"", ""overview"": ""Not this one."" },
                    { ""id"": 901, ""title"": ""The Drama"", ""original_title"": ""El Drama"", ""release_date"": ""2026-03-02"" }
                ]
            }");
            using var svc = Create(handler);

            var results = await svc.SearchAsync("El Drama", 2026, CancellationToken.None);

            Assert.Equal(2, results.Count);
            Assert.Equal("El Sabor del Drama", results[0].Title);
            Assert.Equal("El Drama", results[1].OriginalTitle);
            Assert.Equal(2026, results[1].Year);
            Assert.Equal("Not this one.", results[0].Overview);
        }

        [Fact]
        public async Task The_picker_gets_nothing_and_asks_nothing_without_an_api_key()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""results"": [ { ""id"": 1, ""title"": ""Anything"" } ] }");
            using var svc = Create(handler, apiKey: "");

            Assert.Empty(await svc.SearchAsync("Anything", null, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
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
                ("search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 550, ""title"": ""Fight Club"", ""release_date"": ""1999-10-15"", ""poster_path"": ""/p.jpg"" } ] }"),
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
                ("search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 550, ""title"": ""Fight Club"" } ] }"),
                ("movie/550", HttpStatusCode.OK, @"{ ""id"": 550, ""imdb_id"": ""tt0137523"" }"));
            using var svc = Create(handler);

            var details = await svc.GetDetailsByTitleAsync("Fight Club", null, CancellationToken.None);

            Assert.Equal("tt0137523", details!.ImdbId);
        }

        [Fact]
        public async Task Details_report_no_imdb_id_when_tmdb_omits_it()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 550, ""title"": ""Untitled"" } ] }"),
                ("movie/550", HttpStatusCode.OK, @"{ ""id"": 550, ""title"": ""Untitled"" }"));
            using var svc = Create(handler);

            var details = await svc.GetDetailsByTitleAsync("Untitled", null, CancellationToken.None);

            Assert.NotNull(details);
            Assert.Null(details!.ImdbId);
        }

        [Fact]
        public async Task Details_by_id_ask_tmdb_for_that_film_and_never_search()
        {
            // What a corrected match reads. Searching again would re-derive the wrong film and
            // silently undo the correction the user had just made.
            var handler = FakeHttpMessageHandler.Routed(
                ("movie/901", HttpStatusCode.OK, @"{ ""id"": 901, ""title"": ""The Drama"", ""runtime"": 96 }"));
            using var svc = Create(handler);

            var details = await svc.GetDetailsByIdAsync(901, CancellationToken.None);

            Assert.Equal(901, details!.Id);
            Assert.Equal(96, details.Runtime);
            Assert.DoesNotContain(handler.Requests, url => url.Contains("search/movie"));
        }

        [Fact]
        public async Task Details_by_id_keep_the_requested_id_when_tmdb_omits_it()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""title"": ""The Drama"" }");
            using var svc = Create(handler);

            var details = await svc.GetDetailsByIdAsync(901, CancellationToken.None);

            Assert.Equal(901, details!.Id);
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

        // ---------- recommendations ----------

        /// <summary>
        /// <c>/recommendations</c>, not <c>/similar</c>. The two are easy to confuse and answer
        /// different questions: similar is shared genres and keywords, recommendations is what
        /// people who rated this film also rated, and "what next" is the second one.
        /// </summary>
        [Fact]
        public void Recommendations_ask_the_recommendations_endpoint()
        {
            using var svc = Create(FakeHttpMessageHandler.Json("{}"));

            var url = svc.BuildRecommendationsUrl(550);

            Assert.Contains("/movie/550/recommendations", url);
            Assert.DoesNotContain("/similar", url);
            Assert.Contains("api_key=test-key", url);
        }

        [Fact]
        public async Task Recommendations_are_read_in_the_order_tmdb_returned_them()
        {
            var handler = FakeHttpMessageHandler.Json(@"{
                ""results"": [
                    { ""id"": 807, ""title"": ""Se7en"", ""release_date"": ""1995-09-22"" },
                    { ""id"": 1422, ""title"": ""The Departed"", ""release_date"": ""2006-10-05"" }
                ]
            }");
            using var svc = Create(handler);

            var found = await svc.GetRecommendationsAsync(550, CancellationToken.None);

            Assert.Equal(new[] { 807, 1422 }, found.Select(f => f.Id));
            Assert.Equal(1995, found[0].Year);
        }

        [Fact]
        public async Task No_key_means_no_request_for_recommendations()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""results"": [ { ""id"": 807 } ] }");
            using var svc = new TmdbService("", "", "w342", false, handler);

            Assert.Empty(await svc.GetRecommendationsAsync(550, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task A_film_nothing_has_identified_is_never_asked_about()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""results"": [ { ""id"": 807 } ] }");
            using var svc = Create(handler);

            Assert.Empty(await svc.GetRecommendationsAsync(0, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
        }

        /// <summary>
        /// The shelf is an offer, not a fact. Every failure here is empty rather than thrown,
        /// because none of them is worth stopping a film from opening.
        /// </summary>
        [Fact]
        public async Task Recommendations_are_empty_rather_than_fatal_when_tmdb_fails()
        {
            using var broken = Create(FakeHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError));
            Assert.Empty(await broken.GetRecommendationsAsync(550, CancellationToken.None));

            using var nonsense = Create(FakeHttpMessageHandler.Json("not json"));
            Assert.Empty(await nonsense.GetRecommendationsAsync(550, CancellationToken.None));
        }
    }
}
