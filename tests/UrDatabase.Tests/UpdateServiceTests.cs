using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UpdateServiceTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _statePath;
        private readonly TempLog _log = new();

        /// <summary>
        /// What the service is told the time is. Moved by the tests about rationing, which is the
        /// only way to ask "and what happens tomorrow" without waiting until tomorrow.
        /// </summary>
        private DateTimeOffset _now = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

        public UpdateServiceTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-update-service-" + Guid.NewGuid().ToString("N"));
            _statePath = Path.Combine(_dir, UpdateState.FileName);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        /// <summary>
        /// Every service in this class is built here, and that is the point.
        ///
        /// <see cref="UpdateService.CheckAsync"/> now writes down what it found, so a test that
        /// forgot the path would read and overwrite the update state of whoever ran the suite —
        /// and worse, would pass or fail depending on when that person last opened the app, since
        /// a recent timestamp in their file is precisely what suppresses the request. Constructing
        /// through one method means the next test added here cannot omit it.
        /// </summary>
        private UpdateService Service(
            string? runningVersion,
            string? runtimeIdentifier,
            HttpMessageHandler handler) =>
            new(runningVersion, runtimeIdentifier, handler, _statePath, () => _now);

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
            using var service = Service("0.10.0", "win-x64", handler);

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
            using var service = Service("0.10.0", "osx-arm64", handler);

            await service.CheckAsync();

            Assert.Equal(UpdateFeed.ReleasesApiUrl, handler.Requests.Single());
            Assert.DoesNotContain("/releases/latest", handler.Requests.Single());
        }

        [Fact]
        public async Task Says_nothing_when_this_build_is_already_the_newest()
        {
            using var service = Service("0.11.0", "osx-arm64", FakeHttpMessageHandler.Json(TwoReleases));

            Assert.Null(await service.CheckAsync());
        }

        [Fact]
        public async Task A_rate_limited_or_failing_api_is_no_update_rather_than_an_error()
        {
            // 403 here is almost always the anonymous rate limit, which is per IP address and
            // shared with everything else on the network. There is nothing a user could do.
            using var forbidden = Service(
                "0.10.0", "win-x64", FakeHttpMessageHandler.Json("{}", HttpStatusCode.Forbidden));

            Assert.Null(await forbidden.CheckAsync());

            // A separate file, because the first check has now recorded that it happened and a
            // second one sharing it would be answered from that rather than from the network.
            var elsewhere = Path.Combine(_dir, "second", UpdateState.FileName);
            using var missing = new UpdateService(
                "0.10.0", "win-x64", FakeHttpMessageHandler.Json("[]", HttpStatusCode.NotFound), elsewhere, () => _now);

            Assert.Null(await missing.CheckAsync());
        }

        [Fact]
        public async Task A_payload_that_is_not_what_github_sends_is_no_update_rather_than_a_crash()
        {
            // A captive portal answering with HTML, a proxy truncating the body, an object where a
            // list was expected. An update check must never be the reason the app falls over.
            foreach (var body in new[] { "<html>hello</html>", @"{ ""message"": ""nope"" }", @"[ { ""tag_name"":" })
            {
                var path = Path.Combine(_dir, Guid.NewGuid().ToString("N"), UpdateState.FileName);
                using var service = new UpdateService(
                    "0.10.0", "win-x64", FakeHttpMessageHandler.Json(body), path, () => _now);

                Assert.Null(await service.CheckAsync());
            }
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

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoReleases) };
            });

            using var service = Service("0.10.0", "win-x64", handler);

            Assert.NotNull(await service.CheckAsync());
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task A_machine_no_build_is_published_for_is_told_about_the_release_and_given_no_file()
        {
            using var service = Service("0.10.0", "linux-x64", FakeHttpMessageHandler.Json(TwoReleases));

            var update = await service.CheckAsync();

            // "linux-x64" is not one of the three published identifiers, so nothing matches it and
            // the banner falls back to the website rather than inventing a download.
            Assert.NotNull(update);
            Assert.Null(update!.Asset);
        }

        [Fact]
        public void Reports_the_version_it_is_comparing_against_so_the_banner_can_say_so()
        {
            using var explicitly = Service("0.4.2", "win-x64", FakeHttpMessageHandler.Json("[]"));
            using var byDefault = new UpdateService(handler: FakeHttpMessageHandler.Json("[]"), statePath: _statePath);

            Assert.Equal("0.4.2", explicitly.RunningVersion);
            Assert.Equal(AppVersion.Current, byDefault.RunningVersion);
        }

        // ==============================================================
        //  rationing: the bug this file grew for
        // ==============================================================

        [Fact]
        public async Task A_second_launch_the_same_day_asks_nothing_and_still_knows_the_answer()
        {
            // The fault being fixed. One request per launch is affordable only while nothing else
            // shares the allowance, and the downloads site asks the very same URL from the browser
            // on the same address — so an app opened a dozen times in a working day was spending
            // the budget the website needed to list its own downloads.
            var handler = FakeHttpMessageHandler.Json(TwoReleases);

            using (var first = Service("0.10.0", "win-x64", handler))
                Assert.Equal("0.11.0", (await first.CheckAsync())!.Version);

            _now = _now.AddHours(1);

            using var second = Service("0.10.0", "win-x64", handler);
            var update = await second.CheckAsync();

            Assert.Equal(1, handler.CallCount);
            Assert.Equal("0.11.0", update!.Version);
            Assert.Equal("UrDatabase-0.11.0-win-x64.zip", update.Asset!.Value.Name);
        }

        [Fact]
        public async Task Asks_again_once_the_day_is_up()
        {
            var handler = FakeHttpMessageHandler.Json(TwoReleases);

            using (var first = Service("0.10.0", "win-x64", handler))
                await first.CheckAsync();

            _now = _now.Add(UpdateService.CheckInterval).AddMinutes(1);

            using var later = Service("0.10.0", "win-x64", handler);
            await later.CheckAsync();

            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task Replays_the_validator_so_an_unchanged_list_costs_no_rate_limit()
        {
            // The other half of the fix, and the reason the ETag is kept at all: GitHub does not
            // charge a conditional request it answers 304. An install that is up to date therefore
            // costs nothing on almost every check it does make.
            var sent = new List<string?>();
            var handler = new FakeHttpMessageHandler(request =>
            {
                sent.Add(request.Headers.TryGetValues("If-None-Match", out var values)
                    ? string.Join(", ", values)
                    : null);

                if (sent.Count == 1)
                {
                    var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoReleases) };
                    ok.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
                    return ok;
                }

                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });

            using (var first = Service("0.10.0", "win-x64", handler))
                await first.CheckAsync();

            _now = _now.Add(UpdateService.CheckInterval).AddMinutes(1);

            using var second = Service("0.10.0", "win-x64", handler);
            var update = await second.CheckAsync();

            Assert.Null(sent[0]);
            Assert.Equal("\"abc123\"", sent[1]);

            // A 304 carries no body, so the answer can only have come from the file.
            Assert.Equal("0.11.0", update!.Version);
            Assert.Equal("UrDatabase-0.11.0-win-x64.zip", update.Asset!.Value.Name);
        }

        [Fact]
        public async Task A_rate_limited_check_waits_its_turn_instead_of_asking_again_next_launch()
        {
            // Requests that keep arriving after the allowance is gone are what escalates GitHub
            // from a plain 403 to an edge block, and an edge block carries no CORS headers — which
            // is how this surfaced in the first place, as a browser security error on the
            // downloads page rather than as the rate limit it actually was.
            var handler = FakeHttpMessageHandler.Json("{}", HttpStatusCode.Forbidden);

            using (var first = Service("0.10.0", "win-x64", handler))
                Assert.Null(await first.CheckAsync());

            _now = _now.AddHours(2);

            using var second = Service("0.10.0", "win-x64", handler);
            Assert.Null(await second.CheckAsync());

            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task Keeps_offering_the_last_answer_when_the_network_goes_away()
        {
            var good = FakeHttpMessageHandler.Json(TwoReleases);
            using (var first = Service("0.10.0", "win-x64", good))
                await first.CheckAsync();

            _now = _now.Add(UpdateService.CheckInterval).AddMinutes(1);

            var broken = new FakeHttpMessageHandler(_ => throw new HttpRequestException("the network is gone"));
            using var offline = Service("0.10.0", "win-x64", broken);

            // The last thing known to be true beats saying nothing: the release really is out
            // there, and a laptop opened on a train has not stopped being out of date.
            var update = await offline.CheckAsync();

            Assert.Equal(1, broken.CallCount);
            Assert.Equal("0.11.0", update!.Version);
        }

        [Fact]
        public async Task An_update_that_has_since_been_installed_is_not_announced_from_the_cache()
        {
            var handler = FakeHttpMessageHandler.Json(TwoReleases);

            using (var before = Service("0.10.0", "win-x64", handler))
                Assert.NotNull(await before.CheckAsync());

            _now = _now.AddMinutes(5);

            // The same file, read by the build the user has just moved to. Answering from the
            // cache here would congratulate somebody on 0.11.0 by offering them 0.11.0.
            using var after = Service("0.11.0", "win-x64", handler);

            Assert.Null(await after.CheckAsync());
        }

        [Fact]
        public async Task Does_not_replay_a_validator_belonging_to_a_different_machine()
        {
            // A validator says the release list has not changed. It does not say the conclusion
            // drawn from it still applies, and the conclusion names one file chosen for one
            // runtime identifier — so a Mac that moved off Rosetta must not be handed the Intel
            // build again on the strength of a 304.
            var sent = new List<string?>();
            var handler = new FakeHttpMessageHandler(request =>
            {
                sent.Add(request.Headers.TryGetValues("If-None-Match", out var values)
                    ? string.Join(", ", values)
                    : null);

                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoReleases) };
                ok.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
                return ok;
            });

            using (var intel = Service("0.10.0", "osx-x64", handler))
                await intel.CheckAsync();

            _now = _now.Add(UpdateService.CheckInterval).AddMinutes(1);

            using var native = Service("0.10.0", "osx-arm64", handler);
            var update = await native.CheckAsync();

            Assert.Null(sent[1]);
            Assert.Equal("UrDatabase-0.11.0-osx-arm64.dmg", update!.Asset!.Value.Name);
        }

        [Fact]
        public async Task A_clock_that_moved_backwards_does_not_lock_the_check_out_for_ever()
        {
            var handler = FakeHttpMessageHandler.Json(TwoReleases);

            using (var first = Service("0.10.0", "win-x64", handler))
                await first.CheckAsync();

            // A machine whose clock was wrong and has been corrected, or one restored from a
            // backup. Read as "checked in the future" the wait never elapses and the install
            // silently stops looking for updates forever after.
            _now = _now.AddDays(-30);

            using var corrected = Service("0.10.0", "win-x64", handler);
            await corrected.CheckAsync();

            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task Pressing_later_does_not_throw_away_the_validator_it_shares_a_file_with()
        {
            // SaveSkipped used to write a whole new state over the old one. Left that way, the one
            // deliberate act of dismissing a banner would silently cost a full request on the next
            // launch — and on an install that never updates, one on every launch after that.
            var handler = FakeHttpMessageHandler.Json(TwoReleases);
            using (var first = Service("0.10.0", "win-x64", handler))
                await first.CheckAsync();

            UpdateState.SaveSkipped("0.11.0", _statePath);

            var state = UpdateState.Load(_statePath);
            Assert.Equal("0.11.0", state.SkippedVersion);
            Assert.Equal(_now, state.LastCheckedUtc);
            Assert.Equal("0.11.0", state.Cached!.Version);
        }
    }
}
