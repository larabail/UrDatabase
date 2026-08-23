using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UpdateServiceTests : IDisposable
    {
        private readonly string _logDir;
        private readonly IDisposable _log;

        public UpdateServiceTests()
        {
            _logDir = Path.Combine(Path.GetTempPath(), "urdb-update-service-" + Guid.NewGuid().ToString("N"));

            // Half of these tests provoke a failure on purpose — a rate limit, a captive portal
            // answering HTML, a truncated body — and every one of those paths logs. The real log
            // belongs to whoever ran the suite.
            _log = AppLog.Redirect(_logDir);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_logDir, recursive: true); } catch { }
        }

        private const string TwoReleases = @"[
            {
                ""tag_name"": ""v0.11.0"",
                ""html_url"": ""https://github.com/larabail/UrDatabase/releases/tag/v0.11.0"",
                ""draft"": false,
                ""prerelease"": false,
                ""assets"": [
                    { ""name"": ""UrDatabase-0.11.0-osx-arm64.dmg"",
                      ""browser_download_url"": ""https://github.com/larabail/UrDatabase/releases/download/v0.11.0/UrDatabase-0.11.0-osx-arm64.dmg"",
                      ""size"": 83886080 },
                    { ""name"": ""UrDatabase-0.11.0-win-x64.zip"",
                      ""browser_download_url"": ""https://github.com/larabail/UrDatabase/releases/download/v0.11.0/UrDatabase-0.11.0-win-x64.zip"",
                      ""size"": 71303168 }
                ]
            },
            {
                ""tag_name"": ""v0.10.0"",
                ""draft"": false,
                ""prerelease"": false,
                ""assets"": [
                    { ""name"": ""UrDatabase-0.10.0-osx-arm64.dmg"",
                      ""browser_download_url"": ""https://github.com/larabail/UrDatabase/releases/download/v0.10.0/UrDatabase-0.10.0-osx-arm64.dmg"",
                      ""size"": 83000000 }
                ]
            }
        ]";

        [Fact]
        public async Task Finds_the_build_for_this_machine_on_the_newest_release()
        {
            var handler = FakeHttpMessageHandler.Json(TwoReleases);
            using var service = new UpdateService("0.10.0", "win-x64", handler);

            var update = await service.CheckAsync();

            Assert.NotNull(update);
            Assert.Equal("0.11.0", update!.Version);
            Assert.Equal("UrDatabase-0.11.0-win-x64.zip", update.Asset!.Value.Name);
            Assert.Equal(71303168, update.Asset.Value.Bytes);
            Assert.StartsWith("https://github.com/", update.Asset.Value.Url);
        }

        [Fact]
        public async Task Asks_the_releases_list_rather_than_the_one_github_calls_latest()
        {
            // GitHub defines "latest" by the date a release was created, not by its version, so a
            // fix tagged after a larger release prepared earlier would come back as the latest.
            var handler = FakeHttpMessageHandler.Json(TwoReleases);
            using var service = new UpdateService("0.10.0", "osx-arm64", handler);

            await service.CheckAsync();

            Assert.Equal(UpdateFeed.ReleasesApiUrl, handler.Requests.Single());
            Assert.DoesNotContain("/releases/latest", handler.Requests.Single());
        }

        [Fact]
        public async Task Says_nothing_when_this_build_is_already_the_newest()
        {
            using var service = new UpdateService("0.11.0", "osx-arm64", FakeHttpMessageHandler.Json(TwoReleases));

            Assert.Null(await service.CheckAsync());
        }

        [Fact]
        public async Task A_rate_limited_or_failing_api_is_no_update_rather_than_an_error()
        {
            // 403 here is almost always the anonymous rate limit, which is per IP address and
            // shared with everything else on the network. There is nothing a user could do.
            using var forbidden = new UpdateService(
                "0.10.0", "win-x64", FakeHttpMessageHandler.Json("{}", HttpStatusCode.Forbidden));
            using var missing = new UpdateService(
                "0.10.0", "win-x64", FakeHttpMessageHandler.Json("[]", HttpStatusCode.NotFound));

            Assert.Null(await forbidden.CheckAsync());
            Assert.Null(await missing.CheckAsync());
        }

        [Fact]
        public async Task A_payload_that_is_not_what_github_sends_is_no_update_rather_than_a_crash()
        {
            // A captive portal answering with HTML, a proxy truncating the body, an object where a
            // list was expected. An update check must never be the reason the app falls over.
            using var html = new UpdateService("0.10.0", "win-x64", FakeHttpMessageHandler.Json("<html>hello</html>"));
            using var wrongShape = new UpdateService("0.10.0", "win-x64", FakeHttpMessageHandler.Json(@"{ ""message"": ""nope"" }"));
            using var truncated = new UpdateService("0.10.0", "win-x64", FakeHttpMessageHandler.Json(@"[ { ""tag_name"":"));

            Assert.Null(await html.CheckAsync());
            Assert.Null(await wrongShape.CheckAsync());
            Assert.Null(await truncated.CheckAsync());
        }

        [Fact]
        public async Task Names_itself_to_github_because_an_anonymous_request_is_refused()
        {
            // Not decoration: GitHub answers a request with no User-Agent with a 403. It carries
            // the app and its version and nothing about the machine.
            var handler = new FakeHttpMessageHandler(request =>
            {
                Assert.Equal("UrDatabase", request.Headers.UserAgent.Single().Product!.Name);
                Assert.Equal("0.10.0", request.Headers.UserAgent.Single().Product!.Version);
                Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());

                return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(TwoReleases)
                };
            });

            using var service = new UpdateService("0.10.0", "win-x64", handler);

            Assert.NotNull(await service.CheckAsync());
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task A_machine_no_build_is_published_for_is_told_about_the_release_and_given_no_file()
        {
            using var service = new UpdateService("0.10.0", "linux-x64", FakeHttpMessageHandler.Json(TwoReleases));

            var update = await service.CheckAsync();

            // "linux-x64" is not one of the three published identifiers, so nothing matches it and
            // the banner falls back to the website rather than inventing a download.
            Assert.NotNull(update);
            Assert.Null(update!.Asset);
        }

        [Fact]
        public void Reports_the_version_it_is_comparing_against_so_the_banner_can_say_so()
        {
            using var explicitly = new UpdateService("0.4.2", "win-x64", FakeHttpMessageHandler.Json("[]"));
            using var byDefault = new UpdateService(handler: FakeHttpMessageHandler.Json("[]"));

            Assert.Equal("0.4.2", explicitly.RunningVersion);
            Assert.Equal(AppVersion.Current, byDefault.RunningVersion);
        }
    }
}
