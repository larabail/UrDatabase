using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class OmdbServiceTests : IDisposable
    {
        // The three "yields no rating rather than throwing" tests below log the reason it did.
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        [Fact]
        public void Lookup_url_targets_omdb_with_the_imdb_id_and_key()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json("{}"));

            var url = svc.BuildLookupUrl("tt0137523");

            Assert.StartsWith("https://www.omdbapi.com/?", url);
            Assert.Contains("i=tt0137523", url);
            Assert.Contains("apikey=test-key", url);
        }

        [Fact]
        public async Task Parses_the_rating_string_into_a_number()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""Response"": ""True"", ""imdbRating"": ""7.3"" }");
            using var svc = new OmdbService("test-key", handler);

            var rating = await svc.LookupAsync("tt0137523");

            Assert.Equal(7.3, rating);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task Parses_the_rating_invariantly_regardless_of_the_current_locale()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal locale must not turn "7.3" into 73.
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

                using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json(@"{ ""imdbRating"": ""7.3"" }"));

                Assert.Equal(7.3, await svc.LookupAsync("tt0137523"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Fact]
        public async Task A_rating_of_not_available_means_no_rating()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json(@"{ ""Response"": ""True"", ""imdbRating"": ""N/A"" }"));

            Assert.Null(await svc.LookupAsync("tt0137523"));
        }

        [Fact]
        public async Task A_missing_rating_field_means_no_rating()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json(@"{ ""Response"": ""True"", ""Title"": ""Fight Club"" }"));

            Assert.Null(await svc.LookupAsync("tt0137523"));
        }

        [Fact]
        public async Task A_false_response_yields_no_rating_rather_than_throwing()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json(@"{ ""Response"": ""False"", ""Error"": ""Incorrect IMDb ID."" }"));

            Assert.Null(await svc.LookupAsync("tt9999999"));
        }

        [Fact]
        public async Task An_http_error_yields_no_rating_rather_than_throwing()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable));

            Assert.Null(await svc.LookupAsync("tt0137523"));
        }

        [Fact]
        public async Task Malformed_json_yields_no_rating_rather_than_throwing()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json("not json at all"));

            Assert.Null(await svc.LookupAsync("tt0137523"));
        }

        [Fact]
        public async Task An_unparseable_rating_yields_no_rating()
        {
            using var svc = new OmdbService("test-key", FakeHttpMessageHandler.Json(@"{ ""imdbRating"": ""excellent"" }"));

            Assert.Null(await svc.LookupAsync("tt0137523"));
        }

        [Fact]
        public async Task No_key_means_no_http_call_is_attempted_at_all()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""imdbRating"": ""7.3"" }");
            using var svc = new OmdbService("", handler);

            var rating = await svc.LookupAsync("tt0137523");

            Assert.Null(rating);
            Assert.False(svc.IsAvailable);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task A_blank_imdb_id_means_no_http_call_is_attempted()
        {
            var handler = FakeHttpMessageHandler.Json(@"{ ""imdbRating"": ""7.3"" }");
            using var svc = new OmdbService("test-key", handler);

            Assert.Null(await svc.LookupAsync("   "));
            Assert.Equal(0, handler.CallCount);
        }
    }
}
