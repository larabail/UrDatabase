using System.Net;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class SeriesManualRefreshTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "urdb-series-refresh-" + Guid.NewGuid().ToString("N"));
        private readonly TempLog _log = new();
        private string DatabasePath => Path.Combine(_directory, "movies.db");

        public SeriesManualRefreshTests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task An_offline_manual_refresh_is_not_reported_as_cached_success()
        {
            SeedEpisodes();
            using var client = Client(new FakeHttpMessageHandler(_ => throw new HttpRequestException("offline")));
            var loader = new SeriesLoader(DatabasePath, client);

            await Assert.ThrowsAsync<JellyfinException>(() => loader.RefreshFromServerAsync("series1"));

            Assert.Equal("cached episode", Assert.Single(loader.LoadCached("series1").Episodes).Name);
        }

        [Fact]
        public async Task Manual_refresh_without_a_server_explains_why_it_cannot_refresh()
        {
            SeedEpisodes();
            var loader = new SeriesLoader(DatabasePath);

            await Assert.ThrowsAsync<JellyfinException>(() => loader.RefreshFromServerAsync("series1"));

            Assert.Single(loader.LoadCached("series1").Episodes);
        }

        [Fact]
        public async Task Successful_manual_refresh_replaces_the_episode_cache()
        {
            SeedEpisodes();
            using var client = Client(Server());
            var loader = new SeriesLoader(DatabasePath, client);

            var result = await loader.RefreshFromServerAsync("series1");

            Assert.Equal("fresh episode", Assert.Single(result.Episodes).Name);
            Assert.Equal("fresh episode", Assert.Single(loader.LoadCached("series1").Episodes).Name);
        }

        [Fact]
        public async Task Cancellation_leaves_the_previous_episode_cache_intact()
        {
            SeedEpisodes();
            using var client = Client(Server());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var loader = new SeriesLoader(DatabasePath, client);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => loader.RefreshFromServerAsync("series1", cancellation.Token));

            Assert.Equal("cached episode", Assert.Single(loader.LoadCached("series1").Episodes).Name);
        }

        [Fact]
        public async Task Refresh_fetches_new_metadata_artwork_urls_and_episodes_without_mutating_the_displayed_details()
        {
            var handler = Server();
            using var client = Client(handler);
            var current = CurrentDetails();
            var ratingCalls = 0;
            var result = await SeriesDetailsRefresh.LoadAsync(current, new SeriesLoader(DatabasePath, client), client,
                (id, _) =>
                {
                    Assert.Equal("tt7654321", id);
                    ratingCalls++;
                    return Task.FromResult<double?>(8.5);
                });

            Assert.NotSame(current, result.Details);
            Assert.Equal("cached title", current.Title);
            Assert.Equal("cached poster", current.PosterPath);
            Assert.Equal("fresh title", result.Details.Title);
            Assert.Equal("fresh overview", result.Details.Overview);
            Assert.Equal("Drama", result.Details.Genres);
            Assert.Equal(2024, result.Details.Year);
            Assert.Equal(7.8, result.Details.CommunityRating);
            Assert.Equal(8.5, result.Details.ImdbRating);
            Assert.Equal(1, ratingCalls);
            Assert.Contains("Fresh Actor", Assert.Single(result.Details.TopCast));
            Assert.Contains("Fresh Director", Assert.Single(result.Details.KeyCrew));
            Assert.Equal(1, result.Details.SeasonCount);
            Assert.Equal(1, result.Details.EpisodeCount);
            Assert.Contains("/Items/series1/Images/Primary", result.Details.PosterPath);
            Assert.Contains("tag=new-poster", result.Details.PosterPath);
            Assert.Contains("/Items/series1/Images/Backdrop/0", result.Details.BackdropUrl);
            Assert.Equal("fresh episode", Assert.Single(result.Episodes.Episodes).Name);
            Assert.Contains(handler.Requests, url => url.Contains("/Users/viewer/Items/series1"));
            Assert.Contains(handler.Requests, url => url.Contains("/Seasons"));
            Assert.Contains(handler.Requests, url => url.Contains("/Episodes"));
        }

        [Fact]
        public async Task An_episode_failure_does_not_publish_partial_metadata_or_a_cached_success()
        {
            SeedEpisodes();
            using var client = Client(Server(failEpisodes: true));
            var current = CurrentDetails();
            var loader = new SeriesLoader(DatabasePath, client);

            await Assert.ThrowsAsync<JellyfinException>(
                () => SeriesDetailsRefresh.LoadAsync(current, loader, client));

            Assert.Equal("cached title", current.Title);
            Assert.Equal("cached episode", Assert.Single(loader.LoadCached("series1").Episodes).Name);
        }

        [Fact]
        public async Task A_removed_programme_reports_failure_and_keeps_existing_details()
        {
            SeedEpisodes();
            using var client = Client(Server(detailStatus: HttpStatusCode.NotFound));
            var current = CurrentDetails();
            var loader = new SeriesLoader(DatabasePath, client);

            await Assert.ThrowsAsync<JellyfinException>(
                () => SeriesDetailsRefresh.LoadAsync(current, loader, client));

            Assert.Equal("cached title", current.Title);
            Assert.Equal("cached episode", Assert.Single(loader.LoadCached("series1").Episodes).Name);
        }

        [Fact]
        public async Task Cancellation_after_details_arrive_does_not_fetch_episodes_or_publish_details()
        {
            SeedEpisodes();
            using var cancellation = new CancellationTokenSource();
            var handler = Server(beforeDetails: cancellation.Cancel);
            using var client = Client(handler);
            var current = CurrentDetails();
            var loader = new SeriesLoader(DatabasePath, client);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => SeriesDetailsRefresh.LoadAsync(current, loader, client, ct: cancellation.Token));

            Assert.DoesNotContain(handler.Requests, url => url.Contains("/Episodes"));
            Assert.Equal("cached title", current.Title);
            Assert.Equal("cached episode", Assert.Single(loader.LoadCached("series1").Episodes).Name);
        }

        [Fact]
        public async Task Correcting_an_Imdb_id_does_not_reuse_a_different_programmes_rating()
        {
            using var client = Client(Server());
            var current = CurrentDetails();
            var result = await SeriesDetailsRefresh.LoadAsync(current, new SeriesLoader(DatabasePath, client), client);

            Assert.Equal("tt7654321", result.Details.ImdbId);
            Assert.Null(result.Details.ImdbRating);
            Assert.Equal(6.5, current.ImdbRating);
        }

        [Fact]
        public async Task An_unavailable_rating_does_not_erase_a_known_rating_for_the_same_programme()
        {
            using var client = Client(Server());
            var current = CurrentDetails();
            current.ImdbId = "tt7654321";
            var result = await SeriesDetailsRefresh.LoadAsync(current, new SeriesLoader(DatabasePath, client), client,
                (_, _) => Task.FromResult<double?>(null));

            Assert.Equal(6.5, result.Details.ImdbRating);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_rating_failure_does_not_discard_fresh_server_details(bool timeout)
        {
            using var client = Client(Server());
            var current = CurrentDetails();
            current.ImdbId = "tt7654321";
            var result = await SeriesDetailsRefresh.LoadAsync(current, new SeriesLoader(DatabasePath, client), client,
                (_, _) => Task.FromException<double?>(timeout
                    ? new TaskCanceledException("rating timeout")
                    : new HttpRequestException("rating offline")));

            Assert.Equal("fresh title", result.Details.Title);
            Assert.Equal("fresh episode", Assert.Single(result.Episodes.Episodes).Name);
            Assert.Equal(6.5, result.Details.ImdbRating);
            Assert.Contains("rating could not be refreshed", result.Notice);
        }

        [Fact]
        public async Task Closing_the_page_during_a_rating_lookup_still_cancels_the_refresh()
        {
            using var client = Client(Server());
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SeriesDetailsRefresh.LoadAsync(
                CurrentDetails(), new SeriesLoader(DatabasePath, client), client,
                (_, ct) =>
                {
                    cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<double?>(7);
                }, cancellation.Token));
        }

        [Fact]
        public void Refresh_keeps_the_selected_season_when_an_earlier_season_is_added()
        {
            var seasons = new[]
            {
                new JellyfinSeason { ItemId = "specials", Name = "Specials", Number = 0 },
                new JellyfinSeason { ItemId = "season1", Name = "Season 1", Number = 1 },
                new JellyfinSeason { ItemId = "season2", Name = "Season 2", Number = 2 }
            };
            var groups = SeriesGrouping.Group(seasons, Array.Empty<JellyfinEpisode>());

            var selected = SeriesGrouping.SeasonToShow(groups, "Season 2", openAtNumber: 1);

            Assert.Equal("Season 2", selected?.Name);
        }

        private static SeriesDetailsVm CurrentDetails() => new()
        {
            RemoteId = "series1", Title = "cached title", PosterPath = "cached poster",
            ImdbId = "tt0000001", ImdbRating = 6.5
        };

        private void SeedEpisodes()
        {
            using var connection = Database.Open(DatabasePath);
            JellyfinCache.ReplaceEpisodes(connection, "series1",
                new[] { new JellyfinSeason { ItemId = "season1", SeriesId = "series1", Number = 1 } },
                new[] { new JellyfinEpisode { ItemId = "old", SeriesId = "series1", Name = "cached episode" } });
        }

        private static JellyfinClient Client(HttpMessageHandler handler) => new(
            new JellyfinSettings { ServerUrl = "http://media.invalid", ApiKey = "fake-test-key" },
            deviceId: "series-refresh-tests", handler: handler);

        private static FakeHttpMessageHandler Server(
            HttpStatusCode detailStatus = HttpStatusCode.OK, bool failEpisodes = false, Action? beforeDetails = null) =>
            new(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var status = HttpStatusCode.OK;
                string body;
                if (path.Contains("/Users/viewer/Items/series1"))
                {
                    beforeDetails?.Invoke();
                    status = detailStatus;
                    body = """
                        {"Id":"series1","Name":"fresh title","Type":"Series","Overview":"fresh overview",
                         "ProductionYear":2024,"Genres":["Drama"],"CommunityRating":7.8,
                         "ProviderIds":{"Imdb":"tt7654321"},"ImageTags":{"Primary":"new-poster"},
                         "ChildCount":1,"RecursiveItemCount":1,
                         "People":[{"Name":"Fresh Actor","Type":"Actor","Role":"Witness"},
                                   {"Name":"Fresh Director","Type":"Director"}]}
                        """;
                }
                else if (path.EndsWith("/Users"))
                    body = """[{"Id":"viewer","Name":"viewer"}]""";
                else if (path.EndsWith("/Seasons"))
                    body = """{"Items":[{"Id":"season1","Name":"Season 1","IndexNumber":1}],"TotalRecordCount":1}""";
                else if (path.EndsWith("/Episodes"))
                {
                    if (failEpisodes) throw new HttpRequestException("episode request failed");
                    body = """{"Items":[{"Id":"new","Name":"fresh episode","SeasonId":"season1","ParentIndexNumber":1,"IndexNumber":1}],"TotalRecordCount":1}""";
                }
                else
                    throw new InvalidOperationException("Unexpected request: " + path);
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            });

        public void Dispose()
        {
            _log.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
