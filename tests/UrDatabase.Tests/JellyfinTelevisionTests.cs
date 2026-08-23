using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Everything this app asks a Jellyfin server about television, driven through a fake handler.
    ///
    /// No test here reaches a server and none needs a credential, on the same terms as
    /// <see cref="JellyfinClientTests"/>. The programmes, the ids and the episode titles are all
    /// invented — a real library is private.
    /// </summary>
    public class JellyfinTelevisionTests
    {
        private const string ServerUrl = "http://media.invalid:8096";

        private static JellyfinSettings KeySettings() => new()
        {
            ServerUrl = ServerUrl,
            ApiKey = "not-a-real-key"
        };

        private const string UsersJson = """
            [ { "Id": "22222222222222222222222222222222", "Name": "viewer" } ]
            """;

        private const string ViewsJson = """
            {
              "Items": [
                { "Id": "films", "Name": "Films", "CollectionType": "movies" },
                { "Id": "shows", "Name": "Shows", "CollectionType": "tvshows" }
              ]
            }
            """;

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

        private static string SeriesJson(int total, int firstIndex, params string[] titles)
        {
            var items = titles.Select((title, offset) =>
            {
                var index = firstIndex + offset;
                return $$"""
                    {
                      "Id": "series{{index}}",
                      "Name": "{{title}}",
                      "Type": "Series",
                      "ProductionYear": {{2000 + index}},
                      "Genres": ["Drama"],
                      "Overview": "An invented programme.",
                      "CommunityRating": 8.4,
                      "ChildCount": 3,
                      "RecursiveItemCount": 27,
                      "ProviderIds": { "Imdb": "tt900000{{index}}", "Tmdb": "{{500 + index}}" },
                      "ImageTags": { "Primary": "showtag{{index}}" }
                    }
                    """;
            });

            return $$"""{ "Items": [ {{string.Join(",", items)}} ], "TotalRecordCount": {{total}} }""";
        }

        // ---------- listing series ----------

        [Fact]
        public async Task Series_are_fetched_a_page_at_a_time_like_films()
        {
            var page = 0;
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";

                if (url.Contains("/Views", StringComparison.Ordinal)) return Json(ViewsJson);
                if (!url.Contains("/Items", StringComparison.Ordinal)) return Json(UsersJson);

                return Json(page++ == 0
                    ? SeriesJson(3, 0, "A Wholly Invented Programme", "Another Made Up Show")
                    : SeriesJson(3, 2, "The Third Fiction"));
            });

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var series = await client.GetSeriesAsync();

            Assert.Equal(3, series.Count);
            Assert.Equal(
                new[] { "A Wholly Invented Programme", "Another Made Up Show", "The Third Fiction" },
                series.Select(s => s.Title).ToArray());
        }

        [Fact]
        public async Task Listing_series_asks_for_the_counts_a_card_needs()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, SeriesJson(1, 0, "A Wholly Invented Programme")),
                ("/Views", HttpStatusCode.OK, ViewsJson),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var series = await client.GetSeriesAsync();

            var request = handler.Requests.Last(r => r.Contains("/Items", StringComparison.Ordinal));
            Assert.Contains("IncludeItemTypes=Series", request);
            Assert.Contains("ParentId=shows", request);
            Assert.Contains("ChildCount", request);
            Assert.Contains("RecursiveItemCount", request);

            var show = Assert.Single(series);
            Assert.Equal(3, show.SeasonCount);
            Assert.Equal(27, show.EpisodeCount);
            Assert.Equal("Drama", show.Genres);
            Assert.Equal(8.4, show.CommunityRating);
        }

        [Fact]
        public async Task Television_is_read_from_every_television_library_and_never_twice()
        {
            const string twoShowLibraries = """
                {
                  "Items": [
                    { "Id": "shows", "Name": "TV Shows", "CollectionType": "tvshows" },
                    { "Id": "anime", "Name": "Anime",    "CollectionType": "tvshows" }
                  ]
                }
                """;

            // A server that files one programme under both libraries is ordinary. Two identical
            // cards on the shelf is a worse answer than one.
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";

                if (url.Contains("/Views", StringComparison.Ordinal)) return Json(twoShowLibraries);
                if (!url.Contains("/Items", StringComparison.Ordinal)) return Json(UsersJson);

                return Json(SeriesJson(1, 0, "A Wholly Invented Programme"));
            });

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var series = await client.GetSeriesAsync();

            Assert.Single(series);
            Assert.Contains(handler.Requests, r => r.Contains("ParentId=shows", StringComparison.Ordinal));
            Assert.Contains(handler.Requests, r => r.Contains("ParentId=anime", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_server_with_only_television_still_syncs()
        {
            const string seriesOnly = """
                { "Items": [ { "Id": "shows", "Name": "Shows", "CollectionType": "tvshows" } ] }
                """;

            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, SeriesJson(1, 0, "A Wholly Invented Programme")),
                ("/Views", HttpStatusCode.OK, seriesOnly),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var contents = await client.GetLibraryAsync();

            Assert.Empty(contents.Movies);
            Assert.Single(contents.Series);
        }

        [Fact]
        public async Task A_server_with_only_films_syncs_exactly_as_it_did_before()
        {
            const string filmsOnly = """
                { "Items": [ { "Id": "films", "Name": "Films", "CollectionType": "movies" } ] }
                """;

            const string film = """
                {
                  "Items": [ { "Id": "film1", "Name": "A Wholly Invented Film", "ProductionYear": 1994 } ],
                  "TotalRecordCount": 1
                }
                """;

            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, film),
                ("/Views", HttpStatusCode.OK, filmsOnly),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var contents = await client.GetLibraryAsync();

            Assert.Single(contents.Movies);
            Assert.Empty(contents.Series);

            // Nothing was asked of a television library, because there was not one to ask.
            Assert.DoesNotContain(handler.Requests, r => r.Contains("IncludeItemTypes=Series", StringComparison.Ordinal));
        }

        // ---------- seasons and episodes ----------

        [Fact]
        public async Task Seasons_come_from_the_shows_endpoint_for_one_series()
        {
            const string seasons = """
                {
                  "Items": [
                    { "Id": "s1", "Name": "Season 1", "IndexNumber": 1, "ChildCount": 9, "ImageTags": { "Primary": "s1tag" } },
                    { "Id": "s0", "Name": "Specials", "IndexNumber": 0 }
                  ],
                  "TotalRecordCount": 2
                }
                """;

            var handler = FakeHttpMessageHandler.Routed(
                ("/Shows/", HttpStatusCode.OK, seasons),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var result = await client.GetSeasonsAsync("series0");

            Assert.Equal(2, result.Count);
            Assert.Equal("Season 1", result[0].Name);
            Assert.Equal(1, result[0].Number);
            Assert.Equal(9, result[0].EpisodeCount);

            // The series id is supplied by the caller, because the endpoint is asked about one
            // series and does not always repeat which.
            Assert.All(result, season => Assert.Equal("series0", season.SeriesId));

            var request = handler.Requests.Last(r => r.Contains("/Shows/", StringComparison.Ordinal));
            Assert.Contains("/Shows/series0/Seasons", request);
        }

        [Fact]
        public async Task Episodes_come_back_for_the_whole_series_in_one_request()
        {
            // All seasons at once rather than one request per season: each episode carries its own
            // season number, so opening a show with twelve of them costs one round trip.
            const string episodes = """
                {
                  "Items": [
                    { "Id": "e1", "Name": "The First One",  "ParentIndexNumber": 1, "IndexNumber": 1, "SeasonId": "s1", "RunTimeTicks": 28800000000, "Overview": "Something happens." },
                    { "Id": "e2", "Name": "The Second One", "ParentIndexNumber": 1, "IndexNumber": 2, "SeasonId": "s1" }
                  ],
                  "TotalRecordCount": 2
                }
                """;

            var handler = FakeHttpMessageHandler.Routed(
                ("/Shows/", HttpStatusCode.OK, episodes),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var result = await client.GetEpisodesAsync("series0");

            Assert.Equal(2, result.Count);
            Assert.Equal("The First One", result[0].Name);
            Assert.Equal(1, result[0].SeasonNumber);
            Assert.Equal(1, result[0].Number);
            Assert.Equal("s1", result[0].SeasonId);
            Assert.Equal(48, result[0].RuntimeMinutes);
            Assert.All(result, episode => Assert.Equal("series0", episode.SeriesId));

            var request = handler.Requests.Last(r => r.Contains("/Shows/", StringComparison.Ordinal));
            Assert.Contains("/Shows/series0/Episodes", request);
            Assert.DoesNotContain("SeasonId=", request);
        }

        [Fact]
        public async Task An_episode_page_that_repeats_an_item_does_not_repeat_the_episode()
        {
            const string episodes = """
                {
                  "Items": [
                    { "Id": "e1", "Name": "The First One",  "ParentIndexNumber": 1, "IndexNumber": 1 },
                    { "Id": "e2", "Name": "The Second One", "ParentIndexNumber": 1, "IndexNumber": 2 }
                  ],
                  "TotalRecordCount": 4
                }
                """;

            var handler = FakeHttpMessageHandler.Routed(
                ("/Shows/", HttpStatusCode.OK, episodes),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            Assert.Equal(2, (await client.GetEpisodesAsync("series0")).Count);
        }

        [Fact]
        public async Task A_series_with_no_id_is_refused_rather_than_asked_about()
        {
            var handler = FakeHttpMessageHandler.Json(UsersJson);
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            await Assert.ThrowsAsync<JellyfinException>(() => client.GetSeasonsAsync(" "));
            await Assert.ThrowsAsync<JellyfinException>(() => client.GetEpisodesAsync(""));

            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public void An_episode_is_streamed_by_the_same_url_a_film_is()
        {
            // The one thing that makes an episode playable at all: Jellyfin serves every video
            // from /Videos/{id}/stream, and an episode is a video like any other.
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1");

            var url = client.BuildStreamUrl("episode-1");

            Assert.Contains("/Videos/episode-1/stream", url);
            Assert.Contains("static=true", url);
        }

        // ---------- the wire shape ----------

        [Fact]
        public void A_series_item_becomes_a_series()
        {
            var dto = new JellyfinItemDto
            {
                Id = " series1 ",
                Name = " A Wholly Invented Programme ",
                Type = "Series",
                ProductionYear = 2011,
                Genres = new List<string> { "Drama", " Crime " },
                Overview = " Nothing that happened to anybody. ",
                CommunityRating = 8.4,
                ChildCount = 5,
                RecursiveItemCount = 62,
                ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt9000001", ["Tmdb"] = "1396" },
                ImageTags = new Dictionary<string, string> { ["Primary"] = "showtag" },
                People = new List<JellyfinPersonDto>
                {
                    new() { Name = "An Invented Actor", Role = "A Part", Type = "Actor" },
                    new() { Name = "An Invented Director", Type = "Director" }
                }
            };

            var series = dto.ToSeries()!;

            Assert.Equal("series1", series.ItemId);
            Assert.Equal("A Wholly Invented Programme", series.Title);
            Assert.Equal(2011, series.Year);
            Assert.Equal("Drama, Crime", series.Genres);
            Assert.Equal("Nothing that happened to anybody.", series.Overview);
            Assert.Equal(5, series.SeasonCount);
            Assert.Equal(62, series.EpisodeCount);
            Assert.Equal("tt9000001", series.ImdbId);
            Assert.Equal("An Invented Actor (A Part)", Assert.Single(series.Cast));
            Assert.Equal("Director: An Invented Director", Assert.Single(series.Crew));
        }

        [Fact]
        public void A_count_the_server_did_not_send_is_absent_rather_than_zero()
        {
            // "No seasons" and "nobody counted" are different facts, and only one of them belongs
            // on a card. A server that answered "0 seasons" about a programme it is streaming is
            // failing to answer rather than telling the truth.
            var series = new JellyfinItemDto { Id = "s", Name = "A Programme", ChildCount = 0 }.ToSeries()!;

            Assert.Null(series.SeasonCount);
            Assert.Null(series.EpisodeCount);
        }

        [Fact]
        public void A_series_with_no_id_or_no_name_is_dropped()
        {
            Assert.Null(new JellyfinItemDto { Id = "", Name = "A Programme" }.ToSeries());
            Assert.Null(new JellyfinItemDto { Id = "s", Name = "  " }.ToSeries());
        }

        [Fact]
        public void A_season_with_no_name_is_named_after_its_number()
        {
            // Plenty of servers send an empty name. Dropping the season would leave its episodes
            // with nowhere to go; calling it "Season 2" is what the number already says.
            var season = new JellyfinItemDto { Id = "s2", Name = "", IndexNumber = 2 }.ToSeason("series1")!;

            Assert.Equal("Season 2", season.Name);
            Assert.Equal(2, season.Number);
            Assert.Equal("series1", season.SeriesId);
        }

        [Fact]
        public void A_nameless_episode_is_kept_where_a_nameless_film_is_dropped()
        {
            // A server that has not identified an episode still holds a file that plays, and
            // dropping it would make the list disagree with the season's own count.
            var episode = new JellyfinItemDto { Id = "e7", Name = "", ParentIndexNumber = 2, IndexNumber = 7 }
                .ToEpisode("series1")!;

            Assert.Equal("e7", episode.ItemId);
            Assert.Equal("", episode.Name);
            Assert.Equal(2, episode.SeasonNumber);
            Assert.Equal(7, episode.Number);
        }

        [Fact]
        public void An_item_that_names_its_own_series_is_believed_over_the_caller()
        {
            var episode = new JellyfinItemDto { Id = "e1", Name = "One", SeriesId = "real-series" }
                .ToEpisode("guessed-series")!;

            Assert.Equal("real-series", episode.SeriesId);
        }
    }
}
