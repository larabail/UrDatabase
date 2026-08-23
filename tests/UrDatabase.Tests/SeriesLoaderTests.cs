using System;
using System.IO;
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
    /// Fetching a programme's episodes when it is opened, and remembering them.
    ///
    /// The behaviour worth pinning down is what happens when the server is not there. A laptop
    /// away from the house must still be able to read a programme it has already opened, so an
    /// unreachable server has to come back with the cache rather than with an empty list or an
    /// exception.
    /// </summary>
    public class SeriesLoaderTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _dbPath;
        private readonly TempLog _log = new();

        public SeriesLoaderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-sl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private const string UsersJson = """[ { "Id": "u1", "Name": "viewer" } ]""";

        private const string SeasonsJson = """
            {
              "Items": [ { "Id": "s1", "Name": "Season 1", "IndexNumber": 1, "ChildCount": 2 } ],
              "TotalRecordCount": 1
            }
            """;

        private const string EpisodesJson = """
            {
              "Items": [
                { "Id": "e1", "Name": "The First One",  "ParentIndexNumber": 1, "IndexNumber": 1, "SeasonId": "s1" },
                { "Id": "e2", "Name": "The Second One", "ParentIndexNumber": 1, "IndexNumber": 2, "SeasonId": "s1" }
              ],
              "TotalRecordCount": 2
            }
            """;

        private static JellyfinClient Client(HttpMessageHandler handler) => new(
            new JellyfinSettings { ServerUrl = "http://media.invalid", ApiKey = "not-a-real-key" },
            deviceId: "device-1",
            handler: handler);

        private static FakeHttpMessageHandler Server() => new(request =>
        {
            var url = request.RequestUri?.ToString() ?? "";

            var body = url.Contains("/Seasons", StringComparison.Ordinal) ? SeasonsJson
                : url.Contains("/Episodes", StringComparison.Ordinal) ? EpisodesJson
                : UsersJson;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        });

        [Fact]
        public async Task A_programme_opened_for_the_first_time_is_fetched_and_remembered()
        {
            using var client = Client(Server());
            var loader = new SeriesLoader(_dbPath, client);

            Assert.True(loader.LoadCached("series1").IsEmpty);

            var fresh = await loader.RefreshAsync("series1");

            Assert.Single(fresh.Seasons);
            Assert.Equal(2, fresh.Episodes.Count);

            // Written through, so the next visit costs nothing and works on a train.
            var cached = loader.LoadCached("series1");
            Assert.Single(cached.Seasons);
            Assert.Equal(2, cached.Episodes.Count);
        }

        [Fact]
        public async Task An_unreachable_server_gives_back_what_was_cached()
        {
            using (var seeded = Client(Server()))
            {
                await new SeriesLoader(_dbPath, seeded).RefreshAsync("series1");
            }

            var reported = new System.Collections.Generic.List<string>();

            using var offline = Client(new FakeHttpMessageHandler(
                _ => throw new HttpRequestException("the name does not resolve")));

            var contents = await new SeriesLoader(_dbPath, offline, reported.Add).RefreshAsync("series1");

            Assert.Equal(2, contents.Episodes.Count);

            // Nothing said, because there is nothing to say: the screen is already listing the
            // episodes and a sentence about the server on every visit would be noise.
            Assert.Empty(reported);
        }

        [Fact]
        public async Task An_unreachable_server_with_nothing_cached_says_so()
        {
            var reported = new System.Collections.Generic.List<string>();

            using var offline = Client(new FakeHttpMessageHandler(
                _ => throw new HttpRequestException("the name does not resolve")));

            var contents = await new SeriesLoader(_dbPath, offline, reported.Add).RefreshAsync("series1");

            Assert.True(contents.IsEmpty);
            Assert.NotEmpty(reported);
        }

        [Fact]
        public async Task Reopening_a_programme_replaces_rather_than_accumulates()
        {
            using var client = Client(Server());
            var loader = new SeriesLoader(_dbPath, client);

            await loader.RefreshAsync("series1");
            await loader.RefreshAsync("series1");

            Assert.Equal(2, loader.LoadCached("series1").Episodes.Count);
        }

        [Fact]
        public async Task With_no_server_configured_the_loader_only_reads_the_cache()
        {
            // An install that has switched Jellyfin off should not fail here; it should simply
            // have nothing to show.
            var loader = new SeriesLoader(_dbPath);

            Assert.True((await loader.RefreshAsync("series1")).IsEmpty);
            Assert.True(loader.LoadCached("series1").IsEmpty);
        }

        [Fact]
        public async Task A_programme_with_no_id_is_not_asked_about()
        {
            var handler = Server();
            using var client = Client(handler);

            Assert.True((await new SeriesLoader(_dbPath, client).RefreshAsync("  ")).IsEmpty);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task The_cache_survives_the_loader_that_wrote_it()
        {
            using (var client = Client(Server()))
            {
                await new SeriesLoader(_dbPath, client).RefreshAsync("series1");
            }

            var groups = SeriesGrouping.Group(
                new SeriesLoader(_dbPath).LoadCached("series1").Seasons,
                new SeriesLoader(_dbPath).LoadCached("series1").Episodes);

            var season = Assert.Single(groups);
            Assert.Equal("Season 1", season.Name);
            Assert.Equal(new[] { "S01E01", "S01E02" }, season.Episodes.Select(e => e.Label).ToArray());
        }
    }
}
