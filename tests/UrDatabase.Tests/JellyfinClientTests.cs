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
    /// Everything this app asks of a Jellyfin server, driven through a fake handler.
    ///
    /// No test here reaches a server, and none needs a credential: a suite that talked to a real
    /// Jellyfin would fail on any machine that is not in the owner's house, which is most of them
    /// and all of CI. The fixtures below are invented — the film titles, the ids and the user
    /// names are all made up, because a real library is private.
    /// </summary>
    public class JellyfinClientTests
    {
        private const string ServerUrl = "http://media.invalid:8096";

        private static JellyfinSettings UserSettings() => new()
        {
            ServerUrl = ServerUrl,
            Username = "viewer",
            Password = "hunter2"
        };

        private static JellyfinSettings KeySettings() => new()
        {
            ServerUrl = ServerUrl,
            ApiKey = "not-a-real-key"
        };

        private const string UsersJson = """
            [
              { "Id": "11111111111111111111111111111111", "Name": "owner" },
              { "Id": "22222222222222222222222222222222", "Name": "viewer" }
            ]
            """;

        private const string ViewsJson = """
            {
              "Items": [
                { "Id": "aaaa0000aaaa0000aaaa0000aaaa0000", "Name": "Films",  "CollectionType": "movies" },
                { "Id": "bbbb0000bbbb0000bbbb0000bbbb0000", "Name": "Series", "CollectionType": "tvshows" }
              ],
              "TotalRecordCount": 2
            }
            """;

        private const string AuthJson = """
            {
              "AccessToken": "issued-session-token",
              "User": { "Id": "22222222222222222222222222222222", "Name": "viewer" }
            }
            """;

        private static string ItemsJson(int total, int firstIndex, params string[] titles)
        {
            var items = titles.Select((title, offset) =>
            {
                var index = firstIndex + offset;
                return $$"""
                    {
                      "Id": "item{{index}}",
                      "Name": "{{title}}",
                      "ProductionYear": {{1990 + index}},
                      "Genres": ["Drama", "Comedy"],
                      "Overview": "An invented film.",
                      "RunTimeTicks": 56754979999,
                      "CommunityRating": 6.8,
                      "ProviderIds": { "Imdb": "tt000000{{index}}", "Tmdb": "{{1000 + index}}" },
                      "ImageTags": { "Primary": "tag{{index}}" }
                    }
                    """;
            });

            return $$"""{ "Items": [ {{string.Join(",", items)}} ], "TotalRecordCount": {{total}} }""";
        }

        // ---------- the authorization header ----------

        [Fact]
        public void The_authorization_header_identifies_the_client_and_carries_the_token()
        {
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", deviceName: "laptop", version: "0.2.0");

            var header = client.BuildAuthorizationHeader("a-token");

            Assert.StartsWith("MediaBrowser ", header);
            Assert.Contains("Client=\"UrDatabase\"", header);
            Assert.Contains("Device=\"laptop\"", header);
            Assert.Contains("DeviceId=\"device-1\"", header);
            Assert.Contains("Version=\"0.2.0\"", header);
            Assert.Contains("Token=\"a-token\"", header);
        }

        [Fact]
        public void The_authorization_header_omits_the_token_before_there_is_one()
        {
            // The sign-in request itself has no token yet, and Jellyfin rejects a header that
            // claims an empty one.
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1");

            Assert.DoesNotContain("Token=", client.BuildAuthorizationHeader(null));
            Assert.DoesNotContain("Token=", client.BuildAuthorizationHeader("   "));
        }

        [Theory]
        [InlineData("Ada's MacBook Pro", "Adas-MacBook-Pro")]
        [InlineData("büro-rechner", "bro-rechner")]
        [InlineData("\"quoted, name\"", "quoted-name")]
        [InlineData("", "fallback")]
        [InlineData("   ", "fallback")]
        public void A_device_name_is_reduced_to_something_the_header_can_carry(string input, string expected)
        {
            // Jellyfin splits this header on quotes and commas, and a machine name routinely
            // contains both along with characters HTTP headers cannot carry at all.
            Assert.Equal(expected, JellyfinClient.SanitizeHeaderValue(input, "fallback"));
        }

        [Fact]
        public async Task A_request_sends_the_authorization_header()
        {
            var handler = FakeHttpMessageHandler.Json(UsersJson);
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            await client.ConnectAsync();

            Assert.Contains("Token=\"not-a-real-key\"", handler.RawAuthorizationHeaders[0]);
        }

        // ---------- URLs ----------

        [Fact]
        public void The_stream_url_asks_for_the_original_file()
        {
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1");

            var url = client.BuildStreamUrl("item0");

            Assert.StartsWith($"{ServerUrl}/Videos/item0/stream?static=true", url);
        }

        [Fact]
        public async Task The_stream_url_carries_the_token_because_a_player_cannot_send_a_header()
        {
            var handler = FakeHttpMessageHandler.Json(UsersJson);
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);
            await client.ConnectAsync();

            Assert.Contains("api_key=not-a-real-key", client.BuildStreamUrl("item0"));
        }

        [Fact]
        public void The_poster_url_needs_no_credential_and_carries_none()
        {
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1");

            var url = client.BuildPrimaryImageUrl("item0", "tag0");

            Assert.Equal($"{ServerUrl}/Items/item0/Images/Primary?maxWidth=342&tag=tag0", url);
            Assert.DoesNotContain("api_key", url);
        }

        [Fact]
        public void The_poster_url_works_without_an_image_tag()
        {
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1");

            Assert.Equal($"{ServerUrl}/Items/item0/Images/Primary?maxWidth=342", client.BuildPrimaryImageUrl("item0", null));
        }

        [Fact]
        public void A_url_cannot_be_built_without_a_server()
        {
            using var client = new JellyfinClient(new JellyfinSettings(), deviceId: "device-1");

            Assert.Throws<JellyfinException>(() => client.BuildUri("Users"));
        }

        // ---------- redaction ----------

        [Theory]
        [InlineData("http://x/Videos/1/stream?static=true&api_key=abc123", "http://x/Videos/1/stream?static=true&api_key=REDACTED")]
        [InlineData("MediaBrowser Client=\"UrDatabase\", Token=\"abc123\"", "MediaBrowser Client=\"UrDatabase\", Token=\"REDACTED\"")]
        [InlineData("nothing secret here", "nothing secret here")]
        [InlineData("", "")]
        public void A_token_never_survives_into_a_log_line(string input, string expected)
        {
            Assert.Equal(expected, JellyfinClient.Redact(input));
        }

        [Fact]
        public void Redaction_survives_a_token_in_the_middle_of_a_url()
        {
            var redacted = JellyfinClient.Redact("http://x/s?api_key=abc123&static=true");

            Assert.DoesNotContain("abc123", redacted);
            Assert.Contains("static=true", redacted);
        }

        // ---------- signing in ----------

        [Fact]
        public async Task A_username_and_password_are_exchanged_for_a_session()
        {
            var handler = FakeHttpMessageHandler.Routed(("AuthenticateByName", HttpStatusCode.OK, AuthJson));
            using var client = new JellyfinClient(UserSettings(), deviceId: "device-1", handler: handler);

            await client.ConnectAsync();

            Assert.Equal("22222222222222222222222222222222", client.UserId);
            Assert.Contains("\"Username\":\"viewer\"", handler.RequestBodies[0]);
            Assert.Contains("\"Pw\":\"hunter2\"", handler.RequestBodies[0]);
        }

        [Fact]
        public async Task A_rejected_password_says_so_rather_than_reporting_a_status_code()
        {
            var handler = FakeHttpMessageHandler.Routed(("AuthenticateByName", HttpStatusCode.Unauthorized, "{}"));
            using var client = new JellyfinClient(UserSettings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("username and password", error.Message);
        }

        [Fact]
        public async Task A_key_resolves_the_user_by_name_rather_than_assuming_an_id()
        {
            var handler = FakeHttpMessageHandler.Json(UsersJson);
            var settings = KeySettings();
            settings.Username = "viewer";
            settings.Password = "";

            using var client = new JellyfinClient(settings, deviceId: "device-1", handler: handler);

            await client.ConnectAsync();

            Assert.Equal("22222222222222222222222222222222", client.UserId);
        }

        [Fact]
        public async Task A_username_that_does_not_exist_on_the_server_names_itself_in_the_error()
        {
            var handler = FakeHttpMessageHandler.Json(UsersJson);
            var settings = KeySettings();
            settings.Username = "nobody";

            using var client = new JellyfinClient(settings, deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("nobody", error.Message);
        }

        [Fact]
        public async Task Connecting_without_configuration_is_refused_before_any_request()
        {
            var handler = FakeHttpMessageHandler.Json("{}");
            using var client = new JellyfinClient(new JellyfinSettings(), deviceId: "device-1", handler: handler);

            await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task A_second_connect_reuses_the_first_session()
        {
            var handler = FakeHttpMessageHandler.Routed(("AuthenticateByName", HttpStatusCode.OK, AuthJson));
            using var client = new JellyfinClient(UserSettings(), deviceId: "device-1", handler: handler);

            await client.ConnectAsync();
            await client.ConnectAsync();

            Assert.Equal(1, handler.CallCount);
        }

        // ---------- finding the library ----------

        [Fact]
        public async Task The_movie_library_is_found_by_collection_type_not_by_id()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("/Views", HttpStatusCode.OK, ViewsJson),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);
            await client.ConnectAsync();

            var libraryId = await client.ResolveMovieLibraryIdAsync("22222222222222222222222222222222");

            Assert.Equal("aaaa0000aaaa0000aaaa0000aaaa0000", libraryId);
        }

        [Fact]
        public async Task A_server_with_no_movie_library_says_so()
        {
            const string onlySeries = """
                { "Items": [ { "Id": "b", "Name": "Series", "CollectionType": "tvshows" } ] }
                """;

            var handler = FakeHttpMessageHandler.Routed(
                ("/Views", HttpStatusCode.OK, onlySeries),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);
            await client.ConnectAsync();

            var error = await Assert.ThrowsAsync<JellyfinException>(
                () => client.ResolveMovieLibraryIdAsync("22222222222222222222222222222222"));

            Assert.Contains("no movie library", error.Message);
        }

        [Fact]
        public async Task A_named_library_is_honoured_when_a_server_has_several()
        {
            const string twoMovieLibraries = """
                {
                  "Items": [
                    { "Id": "first",  "Name": "Films",       "CollectionType": "movies" },
                    { "Id": "second", "Name": "Documentaries", "CollectionType": "movies" }
                  ]
                }
                """;

            var settings = KeySettings();
            settings.LibraryName = "Documentaries";

            var handler = FakeHttpMessageHandler.Routed(
                ("/Views", HttpStatusCode.OK, twoMovieLibraries),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(settings, deviceId: "device-1", handler: handler);
            await client.ConnectAsync();

            Assert.Equal("second", await client.ResolveMovieLibraryIdAsync("22222222222222222222222222222222"));
        }

        // ---------- the setup screen's test button ----------

        [Fact]
        public async Task Testing_a_connection_reports_the_library_it_found_and_how_big_it_is()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, """{ "Items": [], "TotalRecordCount": 42 }"""),
                ("/Views", HttpStatusCode.OK, ViewsJson),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            Assert.Equal("Connected. 42 films in \"Films\".", await client.DescribeLibraryAsync());

            // The count comes from the server's own total, so testing a huge library is as cheap
            // as testing an empty one and no film is fetched to answer the question.
            var itemsRequest = handler.Requests.Last(r => r.Contains("/Items", StringComparison.Ordinal));
            Assert.Contains("Limit=0", itemsRequest);
        }

        [Fact]
        public async Task Testing_a_connection_counts_one_film_in_the_singular()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, """{ "Items": [], "TotalRecordCount": 1 }"""),
                ("/Views", HttpStatusCode.OK, ViewsJson),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            Assert.Equal("Connected. 1 film in \"Films\".", await client.DescribeLibraryAsync());
        }

        [Fact]
        public async Task Testing_a_connection_says_which_step_failed()
        {
            // The whole point of the button is to distinguish a wrong address from a wrong
            // password from a server with no films on it, before anything is saved.
            var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);

            using var client = new JellyfinClient(UserSettings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.DescribeLibraryAsync());

            Assert.Contains("username and password", error.Message);
        }

        // ---------- listing ----------

        [Fact]
        public async Task The_whole_library_is_fetched_a_page_at_a_time()
        {
            var page = 0;
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";

                if (url.Contains("/Users?", StringComparison.Ordinal) || url.EndsWith("/Users", StringComparison.Ordinal))
                    return Json(UsersJson);

                if (url.Contains("/Views", StringComparison.Ordinal))
                    return Json(ViewsJson);

                // Three films across two pages, so the loop has to ask twice and then stop.
                var body = page++ == 0
                    ? ItemsJson(3, 0, "A Wholly Invented Film", "Another Made Up Picture")
                    : ItemsJson(3, 2, "The Third Fiction");

                return Json(body);
            });

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var movies = await client.GetMoviesAsync();

            Assert.Equal(3, movies.Count);
            Assert.Equal(new[] { "A Wholly Invented Film", "Another Made Up Picture", "The Third Fiction" },
                movies.Select(m => m.Title).ToArray());
        }

        [Fact]
        public async Task Listing_asks_for_the_fields_that_make_a_second_lookup_unnecessary()
        {
            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, ItemsJson(1, 0, "A Wholly Invented Film")),
                ("/Views", HttpStatusCode.OK, ViewsJson),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var movies = await client.GetMoviesAsync();

            var itemsRequest = handler.Requests.Last(r => r.Contains("/Items", StringComparison.Ordinal));
            Assert.Contains("IncludeItemTypes=Movie", itemsRequest);
            Assert.Contains("Recursive=true", itemsRequest);
            Assert.Contains("Genres", itemsRequest);
            Assert.Contains("ProviderIds", itemsRequest);

            var film = Assert.Single(movies);
            Assert.Equal("Drama, Comedy", film.Genres);
            Assert.Equal("tt0000000", film.ImdbId);
            Assert.Equal(6.8, film.CommunityRating);
            Assert.Equal(95, film.RuntimeMinutes);
        }

        [Fact]
        public async Task A_page_that_repeats_an_item_does_not_repeat_the_film()
        {
            // A library edited while it is being paged can shift under the cursor and hand back
            // an item twice. Two cards for one film is a worse answer than one.
            var handler = new FakeHttpMessageHandler(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";
                if (url.Contains("/Views", StringComparison.Ordinal)) return Json(ViewsJson);
                if (url.Contains("/Items", StringComparison.Ordinal)) return Json(ItemsJson(4, 0, "A Wholly Invented Film", "Another Made Up Picture"));
                return Json(UsersJson);
            });

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var movies = await client.GetMoviesAsync();

            Assert.Equal(2, movies.Count);
        }

        [Fact]
        public async Task Progress_is_reported_so_a_slow_server_still_says_something()
        {
            var reported = new List<string>();
            var handler = FakeHttpMessageHandler.Routed(
                ("/Items", HttpStatusCode.OK, ItemsJson(1, 0, "A Wholly Invented Film")),
                ("/Views", HttpStatusCode.OK, ViewsJson),
                ("/Users", HttpStatusCode.OK, UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            await client.GetMoviesAsync(new ImmediateProgress<string>(reported.Add));

            Assert.Contains(reported, message => message.Contains("Jellyfin", StringComparison.Ordinal));
        }

        /// <summary>
        /// <see cref="Progress{T}"/> posts to a synchronization context, so a test that used it
        /// would be asserting against a race. This reports on the calling thread instead.
        /// </summary>
        private sealed class ImmediateProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;
            public ImmediateProgress(Action<T> report) => _report = report;
            public void Report(T value) => _report(value);
        }

        // ---------- failure ----------

        [Fact]
        public async Task A_server_that_cannot_be_reached_produces_a_sentence_not_a_socket_error()
        {
            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            // Which failure it was is the point: see JellyfinDiagnosticsTests for the full set.
            Assert.Contains("refused the connection", error.Message);
            Assert.Contains("media.invalid", error.Message);
            Assert.DoesNotContain("SocketException", error.Message);
        }

        [Fact]
        public async Task A_rejected_key_asks_the_user_to_check_it()
        {
            var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("API key", error.Message);
        }

        [Fact]
        public async Task A_response_that_is_not_json_fails_without_taking_the_app_down()
        {
            var handler = FakeHttpMessageHandler.Json("<html>a proxy error page</html>");
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());
        }

        [Fact]
        public async Task An_error_message_never_contains_the_credential()
        {
            var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.DoesNotContain("not-a-real-key", error.Message);
        }

        /// <summary>
        /// The second half of an upload. A film that has appeared on the server's disk is not a
        /// film Jellyfin knows about until something has scanned for it, and this is the only way
        /// to ask.
        /// </summary>
        [Fact]
        public async Task A_library_rescan_is_a_post_carrying_the_token_in_a_header()
        {
            var handler = new FakeHttpMessageHandler(request =>
                request.RequestUri!.ToString().Contains("/Library/Refresh", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new StringContent("") }
                    : Json(UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            await client.RefreshLibraryAsync();

            var refresh = handler.Requests.Single(url => url.Contains("/Library/Refresh", StringComparison.Ordinal));
            Assert.Equal($"{ServerUrl}/Library/Refresh", refresh);
            Assert.DoesNotContain("api_key", refresh, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MediaBrowser", handler.RawAuthorizationHeaders[^1]!, StringComparison.Ordinal);
        }

        /// <summary>
        /// Scanning a library is an administrative action and a perfectly ordinary Jellyfin
        /// account cannot do it. That is not a broken upload, so the message says what actually
        /// happens next rather than treating it as a failure.
        /// </summary>
        [Fact]
        public async Task A_server_that_will_not_rescan_says_when_the_film_will_appear_anyway()
        {
            var handler = new FakeHttpMessageHandler(request =>
                request.RequestUri!.ToString().Contains("/Library/Refresh", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") }
                    : Json(UsersJson));

            using var client = new JellyfinClient(KeySettings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.RefreshLibraryAsync());

            Assert.Contains("administrator", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("scheduled scan", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
