using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Downloading a film, driven entirely through a fake handler. Nothing here reaches a server
    /// and no test needs a credential.
    ///
    /// The cases worth having are the interrupted ones. A film is large, a laptop lid closes, and
    /// the difference between "resume" and "append a second copy of the film onto the first
    /// twenty minutes of one" is a status code — so the failures are tested harder than the
    /// success is.
    /// </summary>
    public class JellyfinDownloaderTests : IDisposable
    {
        private const string ServerUrl = "http://media.invalid:8096";
        private const string ItemId = "cccc0000cccc0000cccc0000cccc0000";

        private readonly string _folder;
        private readonly TempLog _log = new();

        public JellyfinDownloaderTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "urdb-dlr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_folder, recursive: true); } catch { }
        }

        private static JellyfinSettings Settings() => new()
        {
            ServerUrl = ServerUrl,
            ApiKey = "not-a-real-key"
        };

        private static HttpResponseMessage FileResponse(
            string body,
            string? fileName = null,
            HttpStatusCode status = HttpStatusCode.OK,
            long? contentLengthOverride = null,
            ContentRangeHeaderValue? contentRange = null)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");

            if (fileName is not null)
                content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName };

            if (contentRange is not null) content.Headers.ContentRange = contentRange;
            if (contentLengthOverride is not null) content.Headers.ContentLength = contentLengthOverride;

            return new HttpResponseMessage(status) { Content = content };
        }

        [Fact]
        public async Task Writes_the_film_under_the_name_the_parser_reads()
        {
            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "original-name.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.False(result.AlreadyExisted);
            Assert.Equal(Path.Combine(_folder, "Arrival (2016).mkv"), result.Path);
            Assert.Equal("a film", File.ReadAllText(result.Path));
        }

        [Fact]
        public async Task Leaves_no_part_file_behind_when_it_succeeds()
        {
            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.Empty(Directory.EnumerateFiles(_folder, "*.part"));
            Assert.Single(Directory.EnumerateFiles(_folder));
            Assert.True(File.Exists(result.Path));
        }

        [Fact]
        public async Task Takes_its_extension_from_the_server_not_from_a_guess()
        {
            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "whatever.mp4"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.Equal(Path.Combine(_folder, "Arrival (2016).mp4"), result.Path);
        }

        [Fact]
        public async Task Asks_for_the_download_endpoint_and_authenticates_in_a_header()
        {
            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            await new JellyfinDownloader(client).DownloadAsync(ItemId, "Arrival", 2016, _folder);

            var url = Assert.Single(handler.Requests);
            Assert.Equal($"{ServerUrl}/Items/{ItemId}/Download", url);

            // Unlike a stream URL, which has to carry its token in the query string for an
            // external player, this request can put it in a header — so the address is not a
            // credential and is safe to log.
            Assert.DoesNotContain("api_key", url, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(handler.RawAuthorizationHeaders.Single());
            Assert.Contains("MediaBrowser", handler.RawAuthorizationHeaders.Single()!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_film_already_downloaded_costs_no_request_at_all()
        {
            File.WriteAllText(Path.Combine(_folder, "Arrival (2016).mp4"), "already here");

            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.True(result.AlreadyExisted);
            Assert.Equal(0, handler.CallCount);
            Assert.Equal("already here", File.ReadAllText(result.Path));
        }

        [Fact]
        public async Task Resumes_an_interrupted_transfer_from_where_it_stopped()
        {
            var partial = Path.Combine(_folder, "Arrival (2016).mkv" + JellyfinDownload.PartialExtension);
            File.WriteAllText(partial, "first half ");

            var handler = new FakeHttpMessageHandler(_ => FileResponse(
                "second half",
                "x.mkv",
                HttpStatusCode.PartialContent,
                contentRange: new ContentRangeHeaderValue(11, 21, 22)));

            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.Equal("first half second half", File.ReadAllText(result.Path));
            Assert.False(File.Exists(partial));
        }

        [Fact]
        public async Task Asks_the_server_to_continue_rather_than_start_again()
        {
            var partial = Path.Combine(_folder, "Arrival (2016).mkv" + JellyfinDownload.PartialExtension);
            File.WriteAllText(partial, "0123456789");

            HttpRequestMessage? seen = null;
            var handler = new FakeHttpMessageHandler(request =>
            {
                seen = request;
                return FileResponse(
                    "rest",
                    "x.mkv",
                    HttpStatusCode.PartialContent,
                    contentRange: new ContentRangeHeaderValue(10, 13, 14));
            });

            using var client = new JellyfinClient(Settings(), handler: handler);
            await new JellyfinDownloader(client).DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.Equal(10, seen!.Headers.Range!.Ranges.Single().From);
        }

        /// <summary>
        /// The case that corrupts a film silently. A reverse proxy that does not implement ranges
        /// answers 200 with the whole file; appending that to what is already on disk produces
        /// something that plays for a few minutes and then falls apart.
        /// </summary>
        [Fact]
        public async Task Starts_again_when_the_server_ignores_the_range()
        {
            var partial = Path.Combine(_folder, "Arrival (2016).mkv" + JellyfinDownload.PartialExtension);
            File.WriteAllText(partial, "stale bytes ");

            var handler = new FakeHttpMessageHandler(_ => FileResponse("the whole film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, _folder);

            Assert.Equal("the whole film", File.ReadAllText(result.Path));
        }

        [Fact]
        public async Task A_truncated_download_never_takes_the_films_name()
        {
            // The server promises more than it sends: a dropped connection, mid-film.
            var handler = new FakeHttpMessageHandler(_ => FileResponse(
                "half a film", "x.mkv", contentLengthOverride: 500));

            using var client = new JellyfinClient(Settings(), handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinDownloader(client).DownloadAsync(ItemId, "Arrival", 2016, _folder));

            Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(_folder, "Arrival (2016).mkv")));

            // Kept, so that starting again resumes rather than re-fetching what already arrived.
            Assert.True(File.Exists(Path.Combine(_folder, "Arrival (2016).mkv" + JellyfinDownload.PartialExtension)));
        }

        [Fact]
        public async Task An_interrupted_transfer_keeps_what_it_got()
        {
            using var cts = new CancellationTokenSource();

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(Encoding.UTF8.GetBytes("what arrived"), cts))
            });

            using var client = new JellyfinClient(Settings(), handler: handler);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new JellyfinDownloader(client).DownloadAsync(
                    ItemId, "Arrival", 2016, _folder, ct: cts.Token));

            var partial = Path.Combine(_folder, "Arrival (2016).mkv" + JellyfinDownload.PartialExtension);

            Assert.True(File.Exists(partial));
            Assert.Equal("what arrived", File.ReadAllText(partial));
            Assert.False(File.Exists(Path.Combine(_folder, "Arrival (2016).mkv")));
        }

        [Fact]
        public async Task A_film_the_server_no_longer_has_says_to_sync()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("")
            });

            using var client = new JellyfinClient(Settings(), handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinDownloader(client).DownloadAsync(ItemId, "Arrival", 2016, _folder));

            Assert.Contains("Sync Jellyfin", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Rejected_credentials_are_reported_as_such()
        {
            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("")
            });

            using var client = new JellyfinClient(Settings(), handler: handler);

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinDownloader(client).DownloadAsync(ItemId, "Arrival", 2016, _folder));

            Assert.Contains("credential", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_film_with_no_id_is_refused_before_anything_is_written()
        {
            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinDownloader(client).DownloadAsync("", "Arrival", 2016, _folder));

            Assert.Empty(Directory.EnumerateFiles(_folder));
        }

        [Fact]
        public async Task Creates_the_download_folder_when_it_is_not_there_yet()
        {
            var nested = Path.Combine(_folder, "films", "downloaded");

            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            var result = await new JellyfinDownloader(client)
                .DownloadAsync(ItemId, "Arrival", 2016, nested);

            Assert.True(File.Exists(result.Path));
            Assert.Equal(nested, Path.GetDirectoryName(result.Path));
        }

        [Fact]
        public async Task Reports_progress_up_to_the_whole_file()
        {
            var reports = new System.Collections.Generic.List<JellyfinDownloadProgress>();

            var handler = new FakeHttpMessageHandler(_ => FileResponse("a film", "x.mkv"));
            using var client = new JellyfinClient(Settings(), handler: handler);

            await new JellyfinDownloader(client).DownloadAsync(
                ItemId, "Arrival", 2016, _folder,
                progress: new Progress2<JellyfinDownloadProgress>(reports.Add));

            Assert.NotEmpty(reports);
            Assert.Equal(6, reports[^1].BytesRead);
            Assert.Equal(6, reports[^1].TotalBytes);
        }

        /// <summary>
        /// Yields its bytes once, then cancels and refuses to go on — a connection dropping, or a
        /// person pressing Cancel, in a form a test can rely on.
        /// </summary>
        private sealed class StallingStream : Stream
        {
            private readonly byte[] _payload;
            private readonly CancellationTokenSource _cts;
            private bool _delivered;

            public StallingStream(byte[] payload, CancellationTokenSource cts)
            {
                _payload = payload;
                _cts = cts;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_delivered)
                {
                    _cts.Cancel();
                    throw new OperationCanceledException(_cts.Token);
                }

                _delivered = true;
                _payload.CopyTo(buffer.Span);
                return ValueTask.FromResult(_payload.Length);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _payload.Length;
            public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// <see cref="Progress{T}"/> posts to whatever synchronization context created it, which in
        /// a test is a thread pool thread — so reports arrive after the assertion. This one runs
        /// the callback where it was raised.
        /// </summary>
        private sealed class Progress2<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public Progress2(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }
}
