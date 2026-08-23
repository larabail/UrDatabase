using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UrActorServiceTests : IDisposable
    {
        // Three tests here take a failure path, and each writes a line about it.
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        private const string F1Response = @"[
            { ""year"": ""2026"", ""category"": ""Best Picture"",
              ""nomination"": { ""primary"": [""F1""], ""secondary"": [""Chad Oman"", ""Brad Pitt""], ""won"": false } },
            { ""year"": ""2026"", ""category"": ""Best Sound"",
              ""nomination"": { ""primary"": [""F1""], ""secondary"": [""Gareth John""], ""won"": true } }
        ]";

        /// <summary>
        /// The one genuinely unusual thing about this API: the key is the last segment of the
        /// path, not a header and not a query parameter.
        /// </summary>
        [Fact]
        public void The_key_travels_in_the_path()
        {
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json("[]"));

            var url = svc.BuildLookupUrl("Sinners");

            Assert.Equal("https://api.uractor.com/person/name=Sinners/apikey=test-key", url);
        }

        [Fact]
        public void A_title_with_a_slash_in_it_does_not_write_a_path_segment_of_its_own()
        {
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json("[]"));

            var url = svc.BuildLookupUrl("Face/Off");

            Assert.Contains("name=Face%2FOff", url);
            Assert.DoesNotContain("name=Face/Off", url);
        }

        [Fact]
        public async Task Reads_the_nominations_a_title_search_returns()
        {
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json(F1Response));

            var found = await svc.LookupAsync("F1");

            Assert.NotNull(found);
            Assert.Equal(2, found!.Count);
            Assert.Equal(2026, found[0].Ceremony);
            Assert.Equal("Best Picture", found[0].Category);
            Assert.Equal("F1", found[0].Nominee);
            Assert.Equal("Chad Oman, Brad Pitt", found[0].Detail);
            Assert.False(found[0].Won);
            Assert.True(found[1].Won);
        }

        /// <summary>
        /// A film the Academy never nominated is the commonest answer there is, and the API says
        /// so with a 404. That is an answer rather than a failure, and the caching above depends
        /// on the difference.
        /// </summary>
        [Fact]
        public async Task A_film_with_no_awards_is_an_answer_not_a_failure()
        {
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));

            var found = await svc.LookupAsync("Some Film Nobody Nominated");

            Assert.NotNull(found);
            Assert.Empty(found!);
        }

        [Fact]
        public async Task A_rate_limit_is_not_an_answer()
        {
            // Null, so nothing records "no awards" against whatever the user happened to be
            // browsing when the minute ran out.
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json("{}", (HttpStatusCode)429));

            Assert.Null(await svc.LookupAsync("Sinners"));
        }

        [Fact]
        public async Task A_rejected_key_is_not_an_answer_either()
        {
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json("{}", HttpStatusCode.Forbidden));

            Assert.Null(await svc.LookupAsync("Sinners"));
        }

        [Fact]
        public async Task No_key_means_no_request_is_ever_made()
        {
            var handler = FakeHttpMessageHandler.Json(F1Response);
            using var svc = new UrActorService("", handler);

            Assert.False(svc.IsAvailable);
            Assert.Null(await svc.LookupAsync("F1"));
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task A_malformed_response_is_not_an_answer()
        {
            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json("this is not json"));

            Assert.Null(await svc.LookupAsync("F1"));
        }

        [Fact]
        public async Task Cancellation_is_passed_through_rather_than_swallowed()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            using var svc = new UrActorService("test-key", FakeHttpMessageHandler.Json(F1Response));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.LookupAsync("F1", cts.Token));
        }

        [Fact]
        public void Rows_with_nothing_printable_are_dropped()
        {
            var payload = new List<UrActorService.UrActorMatch>
            {
                new() { Year = "2026", Category = "Best Picture",
                        Nomination = new UrActorService.UrActorNomination { Primary = new List<string> { "F1" } } },
                new() { Year = "2026", Category = null,
                        Nomination = new UrActorService.UrActorNomination { Primary = new List<string> { "F1" } } },
                new() { Year = "not a year", Category = "Best Picture",
                        Nomination = new UrActorService.UrActorNomination { Primary = new List<string> { "F1" } } },
                new() { Year = "2026", Category = "Best Picture", Nomination = null },
                new() { Year = "2026", Category = "Best Picture",
                        Nomination = new UrActorService.UrActorNomination() }
            };

            Assert.Single(UrActorService.Convert(payload));
        }

        /// <summary>
        /// The key is in the URL, so any exception message carrying a URL carries the whole of it.
        /// Low-value credential or not, it does not belong in a log file.
        /// </summary>
        [Fact]
        public void The_key_never_reaches_the_log()
        {
            using var svc = new UrActorService("secret-key", FakeHttpMessageHandler.Json("[]"));

            var redacted = svc.Redact("failed: https://api.uractor.com/person/name=F1/apikey=secret-key");

            Assert.DoesNotContain("secret-key", redacted);
            Assert.Contains("***", redacted);
        }

        /// <summary>
        /// Belt and braces: <see cref="UrActorService.Redact"/> above is the mechanism, and this
        /// asserts the whole failure path actually goes through it.
        /// </summary>
        /// <remarks>
        /// Written against a redirected log directory, never the real one. AGENTS.md forbids a
        /// test from reading or writing the per-user app data directory at all — it holds
        /// somebody's catalogue and their credentials — and a test about not leaking a key would
        /// be a poor place to start appending to a stranger's log file.
        /// </remarks>
        [Fact]
        public async Task Nothing_here_ever_writes_a_key_into_a_log_file()
        {
            var dir = Path.Combine(Path.GetTempPath(), "urdb-oscars-log-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                using (AppLog.Redirect(dir))
                {
                    Assert.StartsWith(dir, AppLog.Directory, StringComparison.Ordinal);

                    var key = "a-very-distinctive-key-" + Guid.NewGuid().ToString("N");
                    using var svc = new UrActorService(
                        key,
                        new FakeHttpMessageHandler(_ => throw new HttpRequestException(
                            $"boom: https://api.uractor.com/person/name=F1/apikey={key}")));

                    Assert.Null(await svc.LookupAsync("F1"));

                    // The line has to be there, or this passes for the wrong reason.
                    var written = File.ReadAllText(Path.Combine(dir, "oscars.log"));
                    Assert.Contains("lookup failed", written);
                    Assert.Contains("***", written);
                    Assert.DoesNotContain(key, written);
                }

                // And the switch is off again, so nothing after this writes to a temporary
                // directory that is about to be deleted. It unwinds to this class's own log
                // directory rather than to the real one, because the whole suite is redirected
                // now — AppLogTests is where the restore-to-the-real-directory case is asserted.
                Assert.Equal(Path.GetFullPath(_log.Directory), AppLog.Directory);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
