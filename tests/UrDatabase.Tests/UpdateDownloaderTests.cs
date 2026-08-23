using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UpdateDownloaderTests : IDisposable
    {
        private readonly string _dir;

        public UpdateDownloaderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public async Task Writes_the_build_under_its_own_name()
        {
            var body = Body(1024);
            using var downloader = new UpdateDownloader(Serving(body));

            var path = await downloader.DownloadAsync(Asset(body.Length), _dir);

            Assert.Equal(Path.Combine(_dir, "UrDatabase-0.11.0-win-x64.zip"), path);
            Assert.Equal(body, File.ReadAllBytes(path));
        }

        [Fact]
        public async Task Reports_how_far_it_has_got()
        {
            var body = Body(512 * 1024);
            using var downloader = new UpdateDownloader(Serving(body));

            var reports = new System.Collections.Generic.List<UpdateProgress>();
            var path = await downloader.DownloadAsync(
                Asset(body.Length), _dir, new SynchronousProgress<UpdateProgress>(reports.Add));

            Assert.NotEmpty(reports);
            Assert.Equal(body.Length, reports[^1].BytesRead);
            Assert.Equal(1d, reports[^1].Fraction);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public async Task Leaves_no_partial_file_behind_when_it_is_cancelled()
        {
            // Unlike a film there is no resume here — a release archive is small enough to fetch
            // again and is superseded by the next one — so a partial file would only accumulate.
            using var cts = new CancellationTokenSource();
            using var downloader = new UpdateDownloader(new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new CancelsMidTransferStream(cts))
                }));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => downloader.DownloadAsync(Asset(bytes: 4 * 1024 * 1024), _dir, ct: cts.Token));

            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public async Task A_short_download_is_thrown_away_rather_than_named_as_a_release()
        {
            // A truncated .dmg will not mount and a truncated .zip will not open, and under the
            // release's real name that looks like a bad release rather than a bad download.
            var body = Body(1024);
            using var downloader = new UpdateDownloader(Serving(body));

            var ex = await Assert.ThrowsAsync<UpdateException>(
                () => downloader.DownloadAsync(Asset(bytes: 4096), _dir));

            Assert.Contains("ended early", ex.Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public async Task A_refused_download_says_so_and_points_at_the_website()
        {
            using var downloader = new UpdateDownloader(
                FakeHttpMessageHandler.Json("nope", HttpStatusCode.NotFound));

            var ex = await Assert.ThrowsAsync<UpdateException>(
                () => downloader.DownloadAsync(Asset(1024), _dir));

            Assert.Contains("website", ex.Message);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public async Task A_complete_copy_already_on_the_disk_is_not_fetched_again()
        {
            // Pressing Update now twice is what a person does when they cannot remember whether
            // the first one finished.
            var body = Body(1024);
            var handler = Serving(body);
            using var downloader = new UpdateDownloader(handler);

            var first = await downloader.DownloadAsync(Asset(body.Length), _dir);
            var second = await downloader.DownloadAsync(Asset(body.Length), _dir);

            Assert.Equal(first, second);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task A_file_of_the_right_name_and_the_wrong_size_is_replaced()
        {
            // It is the wreckage of something, and it must not be opened as though it were a
            // release.
            var body = Body(2048);
            var handler = Serving(body);
            using var downloader = new UpdateDownloader(handler);

            var path = Path.Combine(_dir, "UrDatabase-0.11.0-win-x64.zip");
            File.WriteAllText(path, "half a download");

            await downloader.DownloadAsync(Asset(body.Length), _dir);

            Assert.Equal(body, File.ReadAllBytes(path));
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task Creates_the_folder_it_was_pointed_at()
        {
            var body = Body(64);
            var nested = Path.Combine(_dir, "not", "there", "yet");
            using var downloader = new UpdateDownloader(Serving(body));

            var path = await downloader.DownloadAsync(Asset(body.Length), nested);

            Assert.True(File.Exists(path));
        }

        [Theory]
        [InlineData("../../etc/passwd", "passwd")]
        [InlineData("nested/build.zip", "build.zip")]
        public void An_asset_name_carrying_a_directory_cannot_write_outside_the_chosen_folder(
            string assetName, string expected)
        {
            var path = UpdateDownloader.ResolvePath(new UpdateAsset(assetName, "https://example.test/x", 1), "/tmp/here");

            Assert.Equal(Path.Combine("/tmp/here", expected), path);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("..")]
        [InlineData("/")]
        public void An_asset_with_no_usable_filename_is_refused(string assetName)
        {
            Assert.Throws<UpdateException>(
                () => UpdateDownloader.ResolvePath(new UpdateAsset(assetName, "https://example.test/x", 1), "/tmp/here"));
        }

        [Fact]
        public void Falls_back_to_the_platform_update_folder_when_none_is_named()
        {
            var path = UpdateDownloader.ResolvePath(Asset(1), null);

            Assert.Equal(Path.Combine(PlatformPaths.DefaultUpdateFolder, "UrDatabase-0.11.0-win-x64.zip"), path);
        }

        [Fact]
        public void Progress_with_no_total_sweeps_rather_than_claiming_a_percentage()
        {
            Assert.Null(new UpdateProgress(1024, null).Fraction);
            Assert.Equal("1.0 KB", new UpdateProgress(1024, null).Describe());
            Assert.Equal("1.0 KB of 2.0 KB (50%)", new UpdateProgress(1024, 2048).Describe());
        }

        private static UpdateAsset Asset(long bytes) => new(
            "UrDatabase-0.11.0-win-x64.zip",
            "https://github.com/larabail/UrDatabase/releases/download/v0.11.0/UrDatabase-0.11.0-win-x64.zip",
            bytes);

        private static byte[] Body(int length)
        {
            var body = new byte[length];
            for (var i = 0; i < length; i++) body[i] = (byte)(i % 251);
            return body;
        }

        private static FakeHttpMessageHandler Serving(byte[] body) => new(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        /// <summary>
        /// <see cref="Progress{T}"/> posts to a synchronization context, which a test has none of,
        /// so its callbacks arrive on the thread pool after the assertions have already run.
        /// </summary>
        private sealed class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> _report;

            public SynchronousProgress(Action<T> report) => _report = report;

            public void Report(T value) => _report(value);
        }

        /// <summary>
        /// A body that hands over one chunk and then behaves as though the user had pressed Cancel
        /// while the rest was still arriving. A fully buffered fake response cannot stand in for
        /// that: the copy loop finishes before anything has a chance to interrupt it, and the test
        /// would pass without the cleanup it is there to prove ever running.
        /// </summary>
        private sealed class CancelsMidTransferStream : Stream
        {
            private readonly CancellationTokenSource _cts;
            private bool _served;

            public CancelsMidTransferStream(CancellationTokenSource cts) => _cts = cts;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();

                if (_served)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new OperationCanceledException();
                }

                _served = true;
                buffer.Span.Fill(7);
                _cts.Cancel();
                return buffer.Length;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
