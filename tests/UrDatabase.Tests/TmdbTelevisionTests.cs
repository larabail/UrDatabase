using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// TMDB's television catalogue: searched, judged by the film rules, and described.
    ///
    /// Nothing here reaches TMDB; every response is a <see cref="FakeHttpMessageHandler"/>.
    /// </summary>
    public class TmdbTelevisionTests : IDisposable
    {
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        internal const string FargoSearch = @"{ ""results"": [
            { ""id"": 60622, ""name"": ""Fargo"", ""original_name"": ""Fargo"", ""first_air_date"": ""2014-04-15"",
              ""poster_path"": ""/fargo.jpg"", ""overview"": ""Crime."", ""genre_ids"": [80, 18] } ] }";

        internal const string FargoDetails = @"{
            ""id"": 60622, ""name"": ""Fargo"", ""overview"": ""A crime anthology set in the snow."",
            ""poster_path"": ""/fargo.jpg"", ""backdrop_path"": ""/fargo-backdrop.jpg"",
            ""first_air_date"": ""2014-04-15"", ""vote_average"": 8.27,
            ""genres"": [ { ""id"": 80, ""name"": ""Crime"" }, { ""id"": 10765, ""name"": ""Sci-Fi & Fantasy"" } ],
            ""created_by"": [ { ""name"": ""Noah Hawley"" } ],
            ""credits"": { ""cast"": [ { ""name"": ""Billy Bob Thornton"", ""character"": ""Lorne Malvo"" } ], ""crew"": [] },
            ""external_ids"": { ""imdb_id"": ""tt2802850"" } }";

        internal const string FargoSeasonOne = @"{ ""season_number"": 1, ""episodes"": [
            { ""season_number"": 1, ""episode_number"": 1, ""name"": ""The Crocodile's Dilemma"",
              ""overview"": ""A stranger arrives in Bemidji."", ""runtime"": 68, ""air_date"": ""2014-04-15"", ""still_path"": ""/s1e1.jpg"" },
            { ""season_number"": 1, ""episode_number"": 2, ""name"": ""The Rooster Prince"", ""runtime"": 47 } ] }";

        /// <summary>Two programmes of one name, four years apart, which only a year can tell apart.</summary>
        private const string TwoOffices = @"{ ""results"": [
            { ""id"": 2316, ""name"": ""The Office"", ""first_air_date"": ""2005-03-24"" },
            { ""id"": 2996, ""name"": ""The Office"", ""first_air_date"": ""2001-07-09"" } ] }";

        private static TmdbService Service(FakeHttpMessageHandler handler, string key = "not-a-real-key") =>
            new(apiKey: key, posterCacheDir: "", imageSize: "w342", downloadPosters: false, handler: handler,
                requests: new TmdbRequestScheduler(TimeSpan.Zero));

        [Fact]
        public async Task A_television_result_is_read_as_a_candidate_named_by_its_programme_and_first_air_date()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK, FargoSearch));
            using var tmdb = Service(handler);

            var found = Assert.Single(await tmdb.SearchTvAsync("Fargo", 2014, CancellationToken.None));

            Assert.Equal(60622, found.Id);
            Assert.Equal("Fargo", found.Title);
            Assert.Equal("Fargo", found.OriginalTitle);
            Assert.Equal(2014, found.Year);
            Assert.Equal("/fargo.jpg", found.PosterPath);
            Assert.Equal(new[] { 80, 18 }, found.GenreIds);
        }

        [Fact]
        public void The_search_is_scoped_by_the_year_the_programme_first_aired()
        {
            using var handler = FakeHttpMessageHandler.Json("{}");
            using var tmdb = Service(handler);

            var url = tmdb.BuildTvSearchUrl("Fargo", 2014);

            Assert.StartsWith($"{TmdbService.ApiBaseUrl}/search/tv?", url, StringComparison.Ordinal);
            Assert.Contains("query=Fargo", url, StringComparison.Ordinal);
            Assert.Contains("first_air_date_year=2014", url, StringComparison.Ordinal);
            Assert.DoesNotContain("first_air_date_year", tmdb.BuildTvSearchUrl("Fargo", null), StringComparison.Ordinal);
        }

        /// <summary>The film catalogue would answer "Fargo" with the 1996 film.</summary>
        [Fact]
        public async Task A_programme_is_found_in_the_television_catalogue_and_never_the_film_one()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK, FargoSearch));
            using var tmdb = Service(handler);

            var found = await tmdb.FindSeriesAsync("Fargo", 2014, CancellationToken.None);

            Assert.Equal(60622, found?.Id);
            Assert.DoesNotContain(handler.Requests, r => r.Contains("/search/movie", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Without_a_year_two_programmes_of_one_name_are_refused_rather_than_guessed()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK, TwoOffices));
            using var tmdb = Service(handler);

            Assert.Null(await tmdb.FindSeriesAsync("The Office", null, CancellationToken.None));
        }

        [Fact]
        public async Task A_year_tells_two_programmes_of_one_name_apart()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK, TwoOffices));
            using var tmdb = Service(handler);

            Assert.Equal(2996, (await tmdb.FindSeriesAsync("The Office", 2001, CancellationToken.None))?.Id);
            Assert.Equal(2316, (await tmdb.FindSeriesAsync("The Office", 2005, CancellationToken.None))?.Id);
        }

        /// <summary>The film rule, inherited: a longer title that merely contains this one is another programme.</summary>
        [Fact]
        public async Task A_programme_whose_name_only_contains_the_title_is_refused()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/search/tv", HttpStatusCode.OK,
                @"{ ""results"": [ { ""id"": 9, ""name"": ""The Return of Fargo"", ""first_air_date"": ""2014-01-01"" } ] }"));
            using var tmdb = Service(handler);

            Assert.Null(await tmdb.FindSeriesAsync("Fargo", 2014, CancellationToken.None));
        }

        [Fact]
        public async Task A_programme_is_described_with_its_genres_people_and_imdb_id_in_one_request()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/tv/60622", HttpStatusCode.OK, FargoDetails));
            using var tmdb = Service(handler);

            var details = await tmdb.GetSeriesDetailsAsync(60622, CancellationToken.None);

            Assert.NotNull(details);
            Assert.Equal("A crime anthology set in the snow.", details!.Overview);
            Assert.Equal("/fargo-backdrop.jpg", details.BackdropPath);
            Assert.Equal("tt2802850", details.ExternalIds?.ImdbId);
            Assert.Equal("Billy Bob Thornton", details.Credits?.Cast.Single().Name);
            Assert.Equal("Noah Hawley", details.CreatedBy?.Single().Name);
            Assert.Equal(new[] { "Crime", "Sci-Fi & Fantasy" }, details.Genres!.Select(g => g.Name));

            var request = Assert.Single(handler.Requests);
            Assert.Contains("append_to_response=credits,external_ids", request, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_season_lists_its_episodes()
        {
            using var handler = FakeHttpMessageHandler.Routed(("/tv/60622/season/1", HttpStatusCode.OK, FargoSeasonOne));
            using var tmdb = Service(handler);

            var episodes = await tmdb.GetSeasonAsync(60622, 1, CancellationToken.None);

            Assert.Equal(2, episodes.Count);
            Assert.Equal("The Crocodile's Dilemma", episodes[0].Name);
            Assert.Equal(68, episodes[0].Runtime);
            Assert.Equal("/s1e1.jpg", episodes[0].StillPath);
        }

        /// <summary>A season this disk numbers that TMDB does not is an answer, not an outage.</summary>
        [Fact]
        public async Task A_season_TMDB_does_not_have_is_empty_and_a_failed_one_throws()
        {
            using var missing = FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
            using (var tmdb = Service(missing))
                Assert.Empty(await tmdb.GetSeasonAsync(60622, 9, CancellationToken.None));

            using var failing = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
            using (var tmdb = Service(failing))
                await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(
                    () => tmdb.GetSeasonAsync(60622, 1, CancellationToken.None));
        }

        [Fact]
        public async Task No_key_means_no_request()
        {
            using var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no request expected"));
            using var tmdb = Service(handler, key: "");

            Assert.Null(await tmdb.FindSeriesAsync("Fargo", 2014, CancellationToken.None));
            Assert.Null(await tmdb.GetSeriesDetailsAsync(60622, CancellationToken.None));
            Assert.Empty(await tmdb.GetSeasonAsync(60622, 1, CancellationToken.None));
            Assert.Equal(0, handler.CallCount);
        }
    }
}
