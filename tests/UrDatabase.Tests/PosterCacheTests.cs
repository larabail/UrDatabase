using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What reaches the poster cache, and — the point of these — what does not.
    ///
    /// A cached poster is never re-checked: the file being on disk is the whole of the lookup on
    /// every later launch. So anything the cache accepts, it accepts permanently, and the tests
    /// below are all variations on one question — after something went wrong, is the destination
    /// still absent so the next attempt actually happens?
    ///
    /// Nothing here reaches TMDB. Every response is served by <see cref="FakeHttpMessageHandler"/>
    /// or by the stubs at the bottom of the file, and no API key is involved.
    /// </summary>
    public class PosterCacheTests : IDisposable
    {
        private readonly string _cacheDir;

        /// <summary>A JPEG as far as anything that reads signatures is concerned.</summary>
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x02, 0x03 };

        private const string PosterUrl = "https://image.tmdb.org/t/p/w342/poster.jpg";

        public PosterCacheTests()
        {
            _cacheDir = Path.Combine(Path.GetTempPath(), "urdb-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_cacheDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_cacheDir, recursive: true); } catch { }
        }

        private TmdbService Create(FakeHttpMessageHandler handler)
            => new("test-key", posterCacheDir: _cacheDir, imageSize: "w342", downloadPosters: true, handler: handler);

        private string Destination(string fileName) => Path.Combine(_cacheDir, fileName);

        private static FakeHttpMessageHandler Responds(Func<HttpContent> content, HttpStatusCode status = HttpStatusCode.OK)
            => new(_ => new HttpResponseMessage(status) { Content = content() });

        private static HttpContent Body(byte[] bytes, string? mediaType)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = mediaType is null ? null : new MediaTypeHeaderValue(mediaType);
            return content;
        }

        // ---------- the good case, so the guards below are not just refusing everything ----------

        [Fact]
        public async Task A_poster_that_arrives_intact_is_cached_and_returned()
        {
            using var svc = Create(Responds(() => Body(Jpeg, "image/jpeg")));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Equal(Destination("7.jpg"), path);
            Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Destination("7.jpg")));
        }

        [Fact]
        public async Task A_finished_download_leaves_no_staging_file_behind()
        {
            using var svc = Create(Responds(() => Body(Jpeg, "image/jpeg")));

            await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Empty(Directory.GetFiles(_cacheDir, "*.part"));
            Assert.Single(Directory.GetFiles(_cacheDir));
        }

        // ---------- wreckage this process never saw ----------

        /// <summary>
        /// A force quit or a lost power supply leaves a staging file no catch block ever ran
        /// for. Nothing reads one, so it is invisible rather than harmful — but a cache that
        /// only grows is one somebody eventually finds and wonders about.
        /// </summary>
        [Fact]
        public async Task A_staging_file_left_by_a_process_that_died_is_cleared_out()
        {
            var abandoned = Path.Combine(_cacheDir, "3.jpg.abcdef.part");
            await File.WriteAllBytesAsync(abandoned, Jpeg[..4]);
            File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromDays(2));

            using var svc = Create(Responds(() => Body(Jpeg, "image/jpeg")));
            await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.False(File.Exists(abandoned));
            Assert.True(File.Exists(Destination("7.jpg")));
        }

        /// <summary>
        /// The other half, and the reason the sweep goes by age rather than by name: a second
        /// copy of the app may be part way through writing into the same cache directory, and
        /// deleting its staging file would break a download that was going perfectly well.
        /// </summary>
        [Fact]
        public async Task A_staging_file_somebody_is_still_writing_is_left_alone()
        {
            var live = Path.Combine(_cacheDir, "9.jpg.fedcba.part");
            await File.WriteAllBytesAsync(live, Jpeg[..4]);

            using var svc = Create(Responds(() => Body(Jpeg, "image/jpeg")));
            await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.True(File.Exists(live));
        }

        [Fact]
        public void The_sweep_reports_what_it_removed_and_spares_finished_posters()
        {
            var stale = Path.Combine(_cacheDir, "1.jpg.aaa.part");
            var fresh = Path.Combine(_cacheDir, "2.jpg.bbb.part");
            var poster = Path.Combine(_cacheDir, "3.jpg");

            foreach (var path in new[] { stale, fresh, poster }) File.WriteAllBytes(path, Jpeg);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(3));

            var removed = TmdbService.SweepStaleStaging(_cacheDir, TimeSpan.FromHours(1));

            Assert.Equal(1, removed);
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(poster));
        }

        [Fact]
        public void The_sweep_says_nothing_about_a_cache_directory_that_does_not_exist()
            => Assert.Equal(0, TmdbService.SweepStaleStaging(Path.Combine(_cacheDir, "not-here"), TimeSpan.Zero));

        [Fact]
        public async Task A_poster_already_in_the_cache_is_returned_without_asking_tmdb()
        {
            await File.WriteAllBytesAsync(Destination("7.jpg"), Jpeg);
            var handler = Responds(() => Body(Jpeg, "image/jpeg"));
            using var svc = Create(handler);

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Equal(Destination("7.jpg"), path);
            Assert.Equal(0, handler.CallCount);
        }

        // ---------- interrupted part way through ----------

        /// <summary>
        /// The bug. The destination file was created before the body was copied into it, so a
        /// connection that dropped mid-poster left a fragment that every later call accepted.
        /// </summary>
        [Fact]
        public async Task A_download_that_fails_part_way_leaves_nothing_at_the_destination()
        {
            using var svc = Create(Responds(() => new StreamContent(new FailingStream(Jpeg[..4]))));

            await Assert.ThrowsAnyAsync<IOException>(
                () => svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None));

            Assert.False(File.Exists(Destination("7.jpg")));
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        /// <summary>
        /// The same thing as the window closing, which is how anybody would actually meet it:
        /// four posters in flight, the app told to stop, and whatever each had written so far
        /// sitting at the name the next launch will trust.
        /// </summary>
        [Fact]
        public async Task A_cancelled_download_leaves_nothing_at_the_destination()
        {
            using var cts = new CancellationTokenSource();
            using var svc = Create(Responds(() => new StreamContent(new FailingStream(Jpeg[..4], onFirstRead: cts.Cancel))));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => svc.DownloadForPublic(PosterUrl, "7.jpg", cts.Token));

            Assert.False(File.Exists(Destination("7.jpg")));
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        /// <summary>
        /// And the consequence that made this worth fixing rather than tolerating: a poster that
        /// failed once has to be fetched again, not served from the wreckage of the first try.
        /// </summary>
        [Fact]
        public async Task A_poster_that_failed_once_is_fetched_again_rather_than_served_broken()
        {
            using (var interrupted = Create(Responds(() => new StreamContent(new FailingStream(Jpeg[..4])))))
            {
                await Assert.ThrowsAnyAsync<IOException>(
                    () => interrupted.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None));
            }

            var handler = Responds(() => Body(Jpeg, "image/jpeg"));
            using var retried = Create(handler);

            var path = await retried.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Equal(1, handler.CallCount);
            Assert.Equal(Jpeg, await File.ReadAllBytesAsync(path!));
        }

        // ---------- answered, but not with a poster ----------

        /// <summary>
        /// A captive portal, a proxy or an outage page: HTTP says 200, and what arrives is HTML.
        /// Saved under a .jpg name it is indistinguishable from a real poster until something
        /// tries to decode it, which is at the point the card renders blank.
        /// </summary>
        [Fact]
        public async Task An_html_page_answered_with_200_is_not_cached_as_a_poster()
        {
            var html = System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Sign in to continue</body></html>");
            using var svc = Create(Responds(() => Body(html, "text/html")));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        /// <summary>The same page, from a server confident enough to call it an image.</summary>
        [Fact]
        public async Task A_body_that_is_not_an_image_is_rejected_even_when_the_server_calls_it_one()
        {
            var html = System.Text.Encoding.UTF8.GetBytes("<html><body>404 Not Found</body></html>");
            using var svc = Create(Responds(() => Body(html, "image/jpeg")));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        [Fact]
        public async Task An_empty_body_is_not_cached_as_a_poster()
        {
            using var svc = Create(Responds(() => Body(Array.Empty<byte>(), "image/jpeg")));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        [Fact]
        public async Task A_truncated_image_is_not_cached_as_a_poster()
        {
            using var svc = Create(Responds(() => Body(new byte[] { 0xFF }, "image/jpeg")));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        [Fact]
        public async Task An_http_error_writes_nothing_and_reports_nothing_cached()
        {
            using var svc = Create(Responds(() => Body(Array.Empty<byte>(), "text/plain"), HttpStatusCode.NotFound));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Null(path);
            Assert.Empty(Directory.GetFiles(_cacheDir));
        }

        /// <summary>
        /// Artwork routinely arrives with no useful type, and refusing it would trade a rare
        /// corrupt poster for a common missing one. The signature is what decides.
        /// </summary>
        [Fact]
        public async Task A_poster_served_without_a_content_type_is_still_cached()
        {
            using var svc = Create(Responds(() => Body(Jpeg, mediaType: null)));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Equal(Destination("7.jpg"), path);
        }

        [Fact]
        public async Task A_poster_served_as_a_generic_binary_is_still_cached()
        {
            using var svc = Create(Responds(() => Body(Jpeg, "application/octet-stream")));

            var path = await svc.DownloadForPublic(PosterUrl, "7.jpg", CancellationToken.None);

            Assert.Equal(Destination("7.jpg"), path);
        }

        /// <summary>
        /// A stream that hands back a prefix and then behaves like a connection that went away.
        /// Read one byte at a time is not simulated here; what matters is that the failure lands
        /// after some bytes have already been written.
        /// </summary>
        private sealed class FailingStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly Action? _onFirstRead;
            private int _position;

            public FailingStream(byte[] prefix, Action? onFirstRead = null)
            {
                _prefix = prefix;
                _onFirstRead = onFirstRead;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position == 0) _onFirstRead?.Invoke();

                if (_position < _prefix.Length)
                {
                    var take = Math.Min(count, _prefix.Length - _position);
                    Array.Copy(_prefix, _position, buffer, offset, take);
                    _position += take;
                    return take;
                }

                throw new IOException("the connection was reset");
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
