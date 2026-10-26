using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The strip of stills under a film's plot: which backdrops make it, and what reaches the
    /// cache. Every response is served by <see cref="FakeHttpMessageHandler"/>; nothing here
    /// reaches TMDB and no real key is involved. The cache is a temporary folder of this class's
    /// own, deleted afterwards.
    /// </summary>
    public sealed class FilmStillsTests : IDisposable
    {
        private readonly TempLog _log = new();
        private readonly string _cacheDir;

        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03 };

        private const string Images = """
            {"id":603,"backdrops":[
              {"file_path":"/titled.jpg","width":1920,"height":1080,"iso_639_1":"en","vote_average":9.9,"vote_count":50},
              {"file_path":"/low.jpg","width":1920,"height":1080,"iso_639_1":null,"vote_average":5.0,"vote_count":3},
              {"file_path":"/best.jpg","width":3840,"height":2160,"iso_639_1":null,"vote_average":5.6,"vote_count":12},
              {"file_path":"/hero.jpg","width":1920,"height":1080,"iso_639_1":null,"vote_average":6.0,"vote_count":20}
            ],"posters":[{"file_path":"/poster.jpg","width":1000,"height":1500}]}
            """;

        public FilmStillsTests()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), "urdb-stills-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cacheDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
            _log.Dispose();
        }

        private static TmdbService.TmdbImage Image(string path, string? language = null, double votes = 5, int count = 1,
            int width = 1920, int height = 1080) =>
            new() { FilePath = path, Language = language, VoteAverage = votes, VoteCount = count, Width = width, Height = height };

        private TmdbService Create(FakeHttpMessageHandler handler, string apiKey = "test-key") =>
            new(apiKey, posterCacheDir: _cacheDir, imageSize: "w342", downloadPosters: true, handler: handler);

        private string StillsRoot => Path.Combine(_cacheDir, FilmStills.FolderName);

        private string FilmFolder(int id) => Path.Combine(StillsRoot, id.ToString());

        /// <summary>The listing for <c>/images</c>, and a JPEG for every image URL except those given.</summary>
        private static FakeHttpMessageHandler Serving(string listing, params string[] brokenImages) => new(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/images?", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(listing, System.Text.Encoding.UTF8, "application/json")
                };

            if (brokenImages.Any(b => url.EndsWith(b, StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>captive portal</html>", System.Text.Encoding.UTF8, "text/html")
                };

            var body = new ByteArrayContent(Jpeg);
            body.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        });

        private static FakeHttpMessageHandler Unreachable() =>
            new(_ => throw new HttpRequestException("no network"));

        // ---------- which frames ----------

        [Fact]
        public void Text_free_backdrops_are_chosen_best_voted_first_and_the_hero_is_left_off()
        {
            var picked = FilmStills.Pick(new[]
            {
                Image("/titled.jpg", "en", votes: 9.9),
                Image("/low.jpg", votes: 5.0),
                Image("/best.jpg", votes: 5.6, count: 12),
                Image("/hero.jpg", votes: 6.0),
            }, heroBackdrop: "https://image.tmdb.org/t/p/w780/hero.jpg");

            Assert.Equal(new[] { "best.jpg", "low.jpg" }, picked);
        }

        [Fact]
        public void Backdrops_with_words_on_them_are_used_only_when_there_is_nothing_else()
        {
            var picked = FilmStills.Pick(new[] { Image("/a.jpg", "en"), Image("/b.jpg", "fr", votes: 7) });

            Assert.Equal(new[] { "b.jpg", "a.jpg" }, picked);
        }

        [Fact]
        public void Duplicates_portraits_and_names_that_are_not_plain_files_are_refused()
        {
            var picked = FilmStills.Pick(new[]
            {
                Image("/same.jpg"),
                Image("/SAME.jpg"),
                Image("/tall.jpg", width: 1000, height: 1500),
                Image("/bad name.jpg"),
                Image("/page.html"),
                Image(""),
                Image("/../../escape.jpg"),
            });

            // The last one is reduced to its own name, which is a plain file; nothing is ever
            // written anywhere but the film's own folder.
            Assert.Equal(new[] { "same.jpg", "escape.jpg" }, picked);
        }

        [Fact]
        public void The_strip_is_capped()
        {
            var many = Enumerable.Range(0, 30).Select(i => Image($"/f{i}.jpg"));

            Assert.Equal(FilmStills.MaxStills, FilmStills.Pick(many).Count);
            Assert.Empty(FilmStills.Pick(null));
        }

        // ---------- when nothing is asked ----------

        [Fact]
        public async Task A_film_nothing_has_identified_asks_nothing()
        {
            var handler = Serving(Images);
            using var tmdb = Create(handler);

            var stills = await new FilmStills(tmdb, StillsRoot).LoadAsync(null, null);

            Assert.Empty(stills);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task An_install_with_no_key_asks_nothing_and_shows_nothing()
        {
            var handler = Serving(Images);
            using var tmdb = Create(handler, apiKey: "");

            var stills = await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null);

            Assert.Empty(stills);
            Assert.Equal(0, handler.CallCount);
            Assert.False(Directory.Exists(StillsRoot));
        }

        // ---------- without a cache ----------

        [Fact]
        public async Task Without_a_cache_the_strip_is_tmdb_urls_and_nothing_is_written()
        {
            var handler = Serving(Images);
            using var tmdb = Create(handler);

            var stills = await new FilmStills(tmdb, cacheRoot: null).LoadAsync(603, "https://image.tmdb.org/t/p/w780/hero.jpg");

            Assert.Equal(new[]
            {
                new FilmStill("https://image.tmdb.org/t/p/w300/best.jpg", "https://image.tmdb.org/t/p/w1280/best.jpg"),
                new FilmStill("https://image.tmdb.org/t/p/w300/low.jpg", "https://image.tmdb.org/t/p/w1280/low.jpg"),
            }, stills);
            Assert.Equal("https://api.themoviedb.org/3/movie/603/images?api_key=test-key", Assert.Single(handler.Requests));
            Assert.False(Directory.Exists(StillsRoot));
        }

        [Fact]
        public void The_cache_follows_the_poster_setting()
        {
            using var tmdb = Create(Serving(Images));

            Assert.Null(FilmStills.CacheRootFor(tmdb, downloadPosters: false));
            Assert.Equal(StillsRoot, FilmStills.CacheRootFor(tmdb, downloadPosters: true));
        }

        // ---------- with a cache ----------

        [Fact]
        public async Task A_strip_that_arrives_whole_is_cached_and_read_back_without_the_network()
        {
            using (var tmdb = Create(Serving(Images)))
            {
                var first = await new FilmStills(tmdb, StillsRoot).LoadAsync(603, "https://image.tmdb.org/t/p/w780/hero.jpg");

                Assert.Equal(new[] { Path.Combine(FilmFolder(603), "00-best.jpg"), Path.Combine(FilmFolder(603), "01-low.jpg") },
                    first.Select(s => s.Thumbnail));
                Assert.All(first, s => Assert.Equal(Jpeg, File.ReadAllBytes(s.Thumbnail)));
                Assert.Empty(Directory.GetFiles(FilmFolder(603), "*.part"));
            }

            var offline = Unreachable();
            using var again = Create(offline);
            var cached = await new FilmStills(again, StillsRoot).LoadAsync(603, null);

            Assert.Equal(0, offline.CallCount);
            Assert.Equal(new[]
            {
                new FilmStill(Path.Combine(FilmFolder(603), "00-best.jpg"), "https://image.tmdb.org/t/p/w1280/best.jpg"),
                new FilmStill(Path.Combine(FilmFolder(603), "01-low.jpg"), "https://image.tmdb.org/t/p/w1280/low.jpg"),
            }, cached);
        }

        [Fact]
        public async Task A_cached_strip_is_still_shown_once_the_key_is_gone()
        {
            using (var tmdb = Create(Serving(Images)))
                await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null);

            using var keyless = Create(Unreachable(), apiKey: "");
            var stills = await new FilmStills(keyless, StillsRoot).LoadAsync(603, null, refresh: true);

            Assert.Equal(3, stills.Count);
        }

        [Fact]
        public async Task A_film_with_no_stills_is_remembered_as_having_none()
        {
            using (var tmdb = Create(Serving("""{"backdrops":[]}""")))
                Assert.Empty(await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null));

            var offline = Unreachable();
            using var again = Create(offline);
            Assert.Empty(await new FilmStills(again, StillsRoot).LoadAsync(603, null));
            Assert.Equal(0, offline.CallCount);
        }

        [Fact]
        public async Task A_strip_missing_a_frame_is_shown_but_not_trusted_from_disk_next_time()
        {
            using (var tmdb = Create(Serving(Images, brokenImages: "/w300/low.jpg")))
            {
                var stills = await new FilmStills(tmdb, StillsRoot).LoadAsync(603, "hero.jpg");

                Assert.Equal(Path.Combine(FilmFolder(603), "00-best.jpg"), stills[0].Thumbnail);
                Assert.Equal("https://image.tmdb.org/t/p/w300/low.jpg", stills[1].Thumbnail);
                Assert.False(File.Exists(Path.Combine(FilmFolder(603), FilmStills.ManifestName)));
            }

            var handler = Serving(Images);
            using var again = Create(handler);
            var retried = await new FilmStills(again, StillsRoot).LoadAsync(603, "hero.jpg");

            Assert.Contains(handler.Requests, url => url.Contains("/images?"));
            Assert.All(retried, s => Assert.True(File.Exists(s.Thumbnail)));
            Assert.True(File.Exists(Path.Combine(FilmFolder(603), FilmStills.ManifestName)));
        }

        [Fact]
        public async Task A_failed_listing_is_never_cached_as_no_stills()
        {
            var failing = FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound);
            using (var tmdb = Create(failing))
                Assert.Empty(await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null));

            Assert.False(File.Exists(Path.Combine(FilmFolder(603), FilmStills.ManifestName)));

            var handler = Serving(Images);
            using var again = Create(handler);
            Assert.NotEmpty(await new FilmStills(again, StillsRoot).LoadAsync(603, null));
            Assert.Contains(handler.Requests, url => url.Contains("/images?"));
        }

        [Fact]
        public async Task Refresh_asks_again_replaces_the_strip_and_keeps_the_old_one_when_it_fails()
        {
            using (var tmdb = Create(Serving(Images)))
                await new FilmStills(tmdb, StillsRoot).LoadAsync(603, "hero.jpg");

            using (var failing = Create(Unreachable()))
            {
                var kept = await new FilmStills(failing, StillsRoot).LoadAsync(603, "hero.jpg", refresh: true);
                Assert.Equal(2, kept.Count);
            }

            const string Changed = """{"backdrops":[{"file_path":"/new.jpg","width":1920,"height":1080}]}""";
            var handler = Serving(Changed);
            using var refreshed = Create(handler);
            var stills = await new FilmStills(refreshed, StillsRoot).LoadAsync(603, "hero.jpg", refresh: true);

            Assert.Contains(handler.Requests, url => url.Contains("/images?"));
            Assert.Equal(Path.Combine(FilmFolder(603), "00-new.jpg"), Assert.Single(stills).Thumbnail);
            Assert.Equal(new[] { "00-new.jpg", FilmStills.ManifestName },
                Directory.GetFiles(FilmFolder(603)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        }

        // ---------- strips cached under the old cap ----------

        private static string Listing(int count) =>
            "{\"backdrops\":[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
                $"{{\"file_path\":\"/f{i:00}.jpg\",\"width\":1920,\"height\":1080}}")) + "]}";

        /// <summary>A manifest as builds before the cap was recorded wrote it.</summary>
        private void CacheLegacyStrip(int id, int count)
        {
            Directory.CreateDirectory(FilmFolder(id));
            var files = Enumerable.Range(0, count).Select(i => $"{i:00}-f{i:00}.jpg").ToList();
            foreach (var file in files) File.WriteAllBytes(Path.Combine(FilmFolder(id), file), Jpeg);
            File.WriteAllLines(Path.Combine(FilmFolder(id), FilmStills.ManifestName), files);
        }

        [Fact]
        public async Task A_full_strip_cached_under_the_old_cap_is_asked_for_again_and_grows()
        {
            CacheLegacyStrip(603, FilmStills.LegacyMaxStills);

            var handler = Serving(Listing(FilmStills.MaxStills + 4));
            using (var tmdb = Create(handler))
            {
                var stills = await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null);

                Assert.Contains(handler.Requests, url => url.Contains("/images?"));
                Assert.Equal(FilmStills.MaxStills, stills.Count);
            }

            // Recorded under the new cap, so the next visit trusts it.
            var offline = Unreachable();
            using var again = Create(offline);
            Assert.Equal(FilmStills.MaxStills, (await new FilmStills(again, StillsRoot).LoadAsync(603, null)).Count);
            Assert.Equal(0, offline.CallCount);
        }

        [Fact]
        public async Task A_short_strip_cached_under_the_old_cap_was_already_everything()
        {
            CacheLegacyStrip(603, FilmStills.LegacyMaxStills - 3);

            var offline = Unreachable();
            using var tmdb = Create(offline);
            var stills = await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null);

            Assert.Equal(FilmStills.LegacyMaxStills - 3, stills.Count);
            Assert.Equal(0, offline.CallCount);
        }

        [Fact]
        public async Task A_full_old_strip_is_still_shown_when_it_cannot_be_asked_for_again()
        {
            CacheLegacyStrip(603, FilmStills.LegacyMaxStills);

            using var tmdb = Create(Unreachable());
            var stills = await new FilmStills(tmdb, StillsRoot).LoadAsync(603, null);

            Assert.Equal(FilmStills.LegacyMaxStills, stills.Count);
            Assert.All(stills, s => Assert.True(File.Exists(s.Thumbnail)));
        }

        /// <summary>
        /// Eight frames stopped about half way across a maximised window and left the rest of the
        /// row as empty backdrop. The strip scrolls with its arrows, so a narrow window loses
        /// nothing by holding more.
        /// </summary>
        [Fact]
        public void The_strip_holds_enough_to_run_the_width_of_a_wide_window()
        {
            Assert.True(FilmStills.MaxStills * DetailsLayoutTests.StillPitch >= DetailsLayoutTests.WideTextColumn);
        }

        [Fact]
        public async Task Leaving_the_page_cancels_the_request_rather_than_answering_empty()
        {
            using var tmdb = Create(Serving(Images));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new FilmStills(tmdb, StillsRoot).LoadAsync(603, null, ct: cts.Token));
        }

        // ---------- the TMDB call itself ----------

        [Fact]
        public async Task Backdrops_are_read_from_the_images_endpoint()
        {
            var handler = Serving(Images);
            using var tmdb = Create(handler);

            var backdrops = await tmdb.GetBackdropsAsync(603, CancellationToken.None);

            Assert.Equal(4, backdrops.Count);
            Assert.Equal("/titled.jpg", backdrops[0].FilePath);
            Assert.Equal("en", backdrops[0].Language);
            Assert.Null(backdrops[1].Language);
            Assert.Equal(3840, backdrops[2].Width);
            Assert.Equal(12, backdrops[2].VoteCount);
        }

        [Fact]
        public async Task A_refused_images_request_throws_instead_of_answering_empty()
        {
            using var tmdb = Create(FakeHttpMessageHandler.Json("{}", HttpStatusCode.NotFound));

            await Assert.ThrowsAsync<HttpRequestException>(() => tmdb.GetBackdropsAsync(603, CancellationToken.None));
        }
    }
}
