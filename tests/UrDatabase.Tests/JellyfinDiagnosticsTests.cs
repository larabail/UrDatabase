using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Telling one connection failure from another.
    ///
    /// Written after an evening lost to a server that was working the whole time. A Tailscale
    /// hostname resolved in the shell and not in the app, and the same server behind a reverse
    /// proxy answered every request with a 404 because the proxy routes by hostname. Both came out
    /// of the app as "could not reach the Jellyfin server", which was true of neither and useless
    /// for both.
    ///
    /// Nothing here touches the network: every failure is handed to the client through the
    /// injectable handler, and every message is asserted on directly.
    /// </summary>
    public class JellyfinDiagnosticsTests : IDisposable
    {
        private const string ServerUrl = "http://media.invalid:8096";
        private const string ProxyUrl = "http://media.invalid";

        // Every test here is a failure path, and the client writes one line per failure.
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        private static JellyfinSettings Settings(string url = ServerUrl) => new()
        {
            ServerUrl = url,
            ApiKey = "not-a-real-key"
        };

        private static HttpRequestException Refused() =>
            new("connection failed", new SocketException((int)SocketError.ConnectionRefused));

        private static HttpRequestException NameNotFound() =>
            new(
                "nodename nor servname provided, or not known (media.invalid:80)",
                new SocketException((int)SocketError.HostNotFound));

        private const string PublicInfoJson = """
            { "ServerName": "the-server", "Version": "10.9.11", "Id": "abc", "ProductName": "Jellyfin Server" }
            """;

        // ---------- classification ----------

        [Fact]
        public void A_name_that_will_not_resolve_is_recognised_from_its_socket_error()
        {
            Assert.Equal(JellyfinConnectionState.NameNotResolved, JellyfinDiagnostics.Classify(NameNotFound()));
        }

        [Fact]
        public void A_name_that_will_not_resolve_is_recognised_from_the_wording_alone()
        {
            // What macOS actually said, with no socket error attached to read instead.
            var failure = new HttpRequestException("nodename nor servname provided, or not known (media.invalid:80)");

            Assert.Equal(JellyfinConnectionState.NameNotResolved, JellyfinDiagnostics.Classify(failure));
        }

        [Theory]
        [InlineData("No such host is known. (media.invalid:8096)")]
        [InlineData("Name or service not known")]
        public void The_other_platforms_wording_for_the_same_thing_is_recognised_too(string message)
        {
            Assert.Equal(JellyfinConnectionState.NameNotResolved, JellyfinDiagnostics.Classify(new HttpRequestException(message)));
        }

        [Fact]
        public void Dot_net_8s_own_name_resolution_error_is_recognised()
        {
            var failure = new HttpRequestException(HttpRequestError.NameResolutionError, "resolution failed");

            Assert.Equal(JellyfinConnectionState.NameNotResolved, JellyfinDiagnostics.Classify(failure));
        }

        [Fact]
        public void A_refused_connection_is_not_confused_with_a_name_failure()
        {
            Assert.Equal(JellyfinConnectionState.ConnectionRefused, JellyfinDiagnostics.Classify(Refused()));
        }

        [Fact]
        public void A_socket_error_beats_the_generic_connection_error_wrapped_around_it()
        {
            // HttpRequestError.ConnectionError covers refused, unreachable and reset alike, so the
            // inner socket error is the only thing that says which of them happened.
            var failure = new HttpRequestException(
                HttpRequestError.ConnectionError,
                "connection failed",
                new SocketException((int)SocketError.ConnectionRefused));

            Assert.Equal(JellyfinConnectionState.ConnectionRefused, JellyfinDiagnostics.Classify(failure));
        }

        [Theory]
        [InlineData("Connection refused (media.invalid:8096)", JellyfinConnectionState.ConnectionRefused)]
        [InlineData("No such host is known.", JellyfinConnectionState.NameNotResolved)]
        public void A_generic_connection_error_is_read_from_its_wording_when_nothing_else_says(
            string message, JellyfinConnectionState expected)
        {
            // ConnectionError on its own is not a verdict: it has to fall through to the wording
            // rather than settle for "unreachable" while the message says exactly what happened.
            var failure = new HttpRequestException(HttpRequestError.ConnectionError, message);

            Assert.Equal(expected, JellyfinDiagnostics.Classify(failure));
        }

        [Fact]
        public void The_wording_on_an_inner_exception_counts_too()
        {
            var failure = new HttpRequestException(
                "An error occurred while sending the request.",
                new InvalidOperationException("Connection refused"));

            Assert.Equal(JellyfinConnectionState.ConnectionRefused, JellyfinDiagnostics.Classify(failure));
        }

        [Fact]
        public void A_timeout_is_recognised_through_the_cancellation_it_arrives_as()
        {
            var failure = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                new TimeoutException());

            Assert.Equal(JellyfinConnectionState.TimedOut, JellyfinDiagnostics.Classify(failure));
        }

        [Fact]
        public void A_socket_timeout_is_recognised()
        {
            var failure = new HttpRequestException("timed out", new SocketException((int)SocketError.TimedOut));

            Assert.Equal(JellyfinConnectionState.TimedOut, JellyfinDiagnostics.Classify(failure));
        }

        [Fact]
        public void Anything_unrecognised_is_still_reported_rather_than_guessed_at()
        {
            Assert.Equal(JellyfinConnectionState.Unreachable, JellyfinDiagnostics.Classify(new InvalidOperationException("something else")));
            Assert.Equal(JellyfinConnectionState.Unreachable, JellyfinDiagnostics.Classify(null));
        }

        [Theory]
        [InlineData(HttpStatusCode.NotFound, JellyfinConnectionState.NotJellyfin)]
        [InlineData(HttpStatusCode.MethodNotAllowed, JellyfinConnectionState.NotJellyfin)]
        [InlineData(HttpStatusCode.Unauthorized, JellyfinConnectionState.Unauthorized)]
        [InlineData(HttpStatusCode.Forbidden, JellyfinConnectionState.Unauthorized)]
        [InlineData(HttpStatusCode.OK, JellyfinConnectionState.Reachable)]
        [InlineData(HttpStatusCode.BadGateway, JellyfinConnectionState.Unreachable)]
        public void A_status_code_is_read_as_the_problem_it_represents(HttpStatusCode status, JellyfinConnectionState expected)
        {
            Assert.Equal(expected, JellyfinDiagnostics.FromStatusCode(status));
        }

        // ---------- the messages ----------

        [Fact]
        public void A_name_failure_says_so_and_does_not_claim_the_server_is_down()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.NameNotResolved, ProxyUrl);

            Assert.Contains("media.invalid", message);
            Assert.Contains("could not be resolved", message);
            Assert.Contains("IP address", message);
            Assert.DoesNotContain("Could not reach", message);
        }

        [Fact]
        public void A_refused_connection_points_at_the_port()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.ConnectionRefused, ServerUrl);

            Assert.Contains("refused the connection", message);
            Assert.Contains("8096", message);
        }

        [Fact]
        public void A_timeout_says_it_was_neither_refused_nor_answered()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.TimedOut, ServerUrl);

            Assert.Contains("did not answer in time", message);
            Assert.Contains("firewall", message);
        }

        [Fact]
        public void A_404_says_something_is_listening_but_it_is_not_jellyfin()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.NotJellyfin, ProxyUrl, 404);

            Assert.Contains("Something is listening", message);
            Assert.Contains("does not look like Jellyfin", message);
            Assert.Contains("reverse proxy", message);
            Assert.Contains("http://media.invalid:8096", message);
        }

        [Fact]
        public void A_404_on_the_jellyfin_port_itself_suggests_something_else()
        {
            // Nothing to gain by telling a user to try the port they already configured.
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.NotJellyfin, ServerUrl, 404);

            Assert.Contains("does not look like Jellyfin", message);
            Assert.DoesNotContain("Try Jellyfin's own port", message);
        }

        [Fact]
        public void A_rejected_credential_names_both_kinds_of_credential()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.Unauthorized, ServerUrl, 401);

            Assert.Contains("rejected the credentials", message);
            Assert.Contains("401", message);
            Assert.Contains("username and password", message);
            Assert.Contains("API key", message);
        }

        [Fact]
        public void A_gateway_error_says_the_proxy_may_be_up_while_jellyfin_is_not()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.Unreachable, ProxyUrl, 502);

            Assert.Contains("502", message);
            Assert.Contains("reverse proxy", message);
        }

        [Fact]
        public void Every_failure_mode_gets_its_own_message()
        {
            var states = new[]
            {
                JellyfinConnectionState.NameNotResolved,
                JellyfinConnectionState.ConnectionRefused,
                JellyfinConnectionState.TimedOut,
                JellyfinConnectionState.NotJellyfin,
                JellyfinConnectionState.Unauthorized
            };

            var messages = states.Select(state => JellyfinDiagnostics.Describe(state, ProxyUrl, 404)).ToList();

            Assert.Equal(messages.Count, messages.Distinct(StringComparer.Ordinal).Count());
            Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        }

        [Fact]
        public void A_message_still_reads_when_no_address_is_configured()
        {
            var message = JellyfinDiagnostics.Describe(JellyfinConnectionState.Unreachable, null);

            Assert.Contains("the configured address", message);
        }

        // ---------- what the client raises ----------

        [Fact]
        public async Task A_name_that_will_not_resolve_reaches_the_user_as_a_name_problem()
        {
            var handler = new FakeHttpMessageHandler(_ => throw NameNotFound());
            using var client = new JellyfinClient(Settings(ProxyUrl), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("could not be resolved", error.Message);
            Assert.Contains("media.invalid", error.Message);
        }

        [Fact]
        public async Task A_refused_connection_reaches_the_user_as_a_refused_connection()
        {
            var handler = new FakeHttpMessageHandler(_ => throw Refused());
            using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("refused the connection", error.Message);
            Assert.Contains("media.invalid", error.Message);
        }

        [Fact]
        public async Task A_404_from_a_reverse_proxy_reaches_the_user_as_not_jellyfin()
        {
            var handler = FakeHttpMessageHandler.Json("<html>404 not found</html>", HttpStatusCode.NotFound);
            using var client = new JellyfinClient(Settings(ProxyUrl), deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("does not look like Jellyfin", error.Message);
            Assert.Contains("http://media.invalid:8096", error.Message);
        }

        [Fact]
        public async Task A_404_during_a_username_sign_in_reaches_the_user_as_not_jellyfin()
        {
            var handler = FakeHttpMessageHandler.Json("<html>404 not found</html>", HttpStatusCode.NotFound);
            var settings = new JellyfinSettings { ServerUrl = ProxyUrl, Username = "viewer", Password = "hunter2" };
            using var client = new JellyfinClient(settings, deviceId: "device-1", handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() => client.ConnectAsync());

            Assert.Contains("does not look like Jellyfin", error.Message);
        }

        // ---------- the connection test ----------

        [Fact]
        public async Task A_working_server_is_reported_with_the_name_it_gives_itself()
        {
            var handler = FakeHttpMessageHandler.Json(PublicInfoJson);
            using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.Reachable, report.State);
            Assert.True(report.IsReachable);
            Assert.Equal("the-server", report.ServerName);
            Assert.Equal("10.9.11", report.Version);
            Assert.Contains("10.9.11", report.Message);
        }

        [Fact]
        public async Task The_connection_test_asks_the_one_endpoint_that_needs_no_credential()
        {
            var handler = FakeHttpMessageHandler.Json(PublicInfoJson);
            using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

            await client.TestConnectionAsync();

            var asked = Assert.Single(handler.Requests);
            Assert.Equal("http://media.invalid:8096/System/Info/Public", asked);
        }

        [Fact]
        public async Task A_reverse_proxy_answering_404_is_reported_as_not_jellyfin()
        {
            var handler = FakeHttpMessageHandler.Json("<html>404</html>", HttpStatusCode.NotFound);
            using var client = new JellyfinClient(Settings(ProxyUrl), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.NotJellyfin, report.State);
            Assert.Equal(404, report.StatusCode);
            Assert.Contains("does not look like Jellyfin", report.Message);
        }

        [Fact]
        public async Task Something_that_answers_200_without_being_jellyfin_is_reported_as_not_jellyfin()
        {
            // A router admin page, a captive portal, or a proxy's own landing page.
            var handler = FakeHttpMessageHandler.Json("<html><body>a login page</body></html>");
            using var client = new JellyfinClient(Settings(ProxyUrl), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.NotJellyfin, report.State);
        }

        [Fact]
        public async Task A_name_that_will_not_resolve_is_reported_as_such_by_the_test()
        {
            var handler = new FakeHttpMessageHandler(_ => throw NameNotFound());
            using var client = new JellyfinClient(Settings(ProxyUrl), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.NameNotResolved, report.State);
            Assert.Contains("could not be resolved", report.Message);
        }

        [Fact]
        public async Task A_refused_connection_is_reported_as_such_by_the_test()
        {
            var handler = new FakeHttpMessageHandler(_ => throw Refused());
            using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.ConnectionRefused, report.State);
        }

        [Fact]
        public async Task A_timeout_is_reported_as_such_by_the_test()
        {
            var handler = new FakeHttpMessageHandler(_ =>
                throw new TaskCanceledException("timed out", new TimeoutException()));
            using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.TimedOut, report.State);
        }

        [Fact]
        public async Task A_rejected_credential_is_reported_as_such_by_the_test()
        {
            var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Unauthorized);
            using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.Unauthorized, report.State);
            Assert.Equal(401, report.StatusCode);
        }

        [Fact]
        public async Task A_proxy_that_is_up_while_jellyfin_is_not_is_reported_with_its_status()
        {
            var handler = FakeHttpMessageHandler.Json("<html>bad gateway</html>", HttpStatusCode.BadGateway);
            using var client = new JellyfinClient(Settings(ProxyUrl), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.Unreachable, report.State);
            Assert.Equal(502, report.StatusCode);
            Assert.Contains("502", report.Message);
        }

        [Fact]
        public async Task An_unconfigured_server_is_reported_without_a_request_being_made()
        {
            var handler = FakeHttpMessageHandler.Json(PublicInfoJson);
            using var client = new JellyfinClient(new JellyfinSettings(), deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.NotConfigured, report.State);
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task The_connection_test_never_throws_whatever_the_transport_does()
        {
            var failures = new List<Exception>
            {
                NameNotFound(),
                Refused(),
                new HttpRequestException("something nobody has seen before"),
                new InvalidOperationException("nor this")
            };

            foreach (var failure in failures)
            {
                var handler = new FakeHttpMessageHandler(_ => throw failure);
                using var client = new JellyfinClient(Settings(), deviceId: "device-1", handler: handler);

                var report = await client.TestConnectionAsync();

                Assert.False(report.IsReachable);
                Assert.False(string.IsNullOrWhiteSpace(report.Message));
            }
        }

        [Fact]
        public async Task A_report_carries_no_credential_into_the_log()
        {
            var settings = new JellyfinSettings
            {
                ServerUrl = ServerUrl,
                Username = "viewer",
                Password = "hunter2",
                ApiKey = "not-a-real-key"
            };

            var handler = FakeHttpMessageHandler.Json(PublicInfoJson);
            using var client = new JellyfinClient(settings, deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();
            var line = report.ToString();

            Assert.DoesNotContain("hunter2", line);
            Assert.DoesNotContain("not-a-real-key", line);
        }

        [Fact]
        public void A_password_written_into_the_address_never_reaches_a_message()
        {
            // Some people write http://user:password@host. Every one of these strings ends up in
            // a dialog, the status line and jellyfin.log.
            const string url = "http://viewer:hunter2@media.invalid:8096";

            var states = new[]
            {
                JellyfinConnectionState.NameNotResolved,
                JellyfinConnectionState.ConnectionRefused,
                JellyfinConnectionState.TimedOut,
                JellyfinConnectionState.NotJellyfin,
                JellyfinConnectionState.Unauthorized,
                JellyfinConnectionState.Unreachable
            };

            foreach (var state in states)
            {
                var message = JellyfinDiagnostics.Describe(state, url, 404);

                Assert.DoesNotContain("hunter2", message);
                Assert.Contains("media.invalid", message);
            }
        }

        [Fact]
        public async Task A_password_written_into_the_address_never_reaches_the_report_either()
        {
            var settings = new JellyfinSettings
            {
                ServerUrl = "http://viewer:hunter2@media.invalid:8096",
                ApiKey = "not-a-real-key"
            };
            settings.Normalize();

            var handler = FakeHttpMessageHandler.Json(PublicInfoJson);
            using var client = new JellyfinClient(settings, deviceId: "device-1", handler: handler);

            var report = await client.TestConnectionAsync();

            Assert.Equal(JellyfinConnectionState.Reachable, report.State);
            Assert.DoesNotContain("hunter2", report.ToString());
        }
    }
}
