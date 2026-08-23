using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet.Common;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Uploading a film, driven entirely through a fake filesystem and a fake handler. Nothing
    /// here opens a socket and no test needs a key or a credential.
    ///
    /// As with the download, the interrupted cases are tested harder than the successful one. A
    /// film is large, a laptop lid closes, and the difference between "nothing happened" and "the
    /// server now holds a forty-minute film under the name of a two-hour one" is whether the
    /// partial file was cleaned up before anything renamed it.
    /// </summary>
    public class JellyfinUploaderTests : IDisposable
    {
        private const string ServerUrl = "http://media.invalid:8096";
        private const string Movies = "movies";
        private const string RemoteFile = "movies/Arrival (2016)/Arrival (2016).mkv";
        private const string RemotePartial = "movies/Arrival (2016)/Arrival (2016).mkv.uploading";

        private readonly string _folder;

        public JellyfinUploaderTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "urdb-up-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { }
        }

        private string LocalFilm(string contents = "a film", string name = "arrival.2016.1080p.mkv")
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllText(path, contents);
            return path;
        }

        private static JellyfinSettings Settings() => new()
        {
            ServerUrl = ServerUrl,
            ApiKey = "not-a-real-key"
        };

        /// <summary>
        /// A server that signs the client in and accepts a rescan. Two hops, because resolving the
        /// user id is what <see cref="JellyfinClient.ConnectAsync"/> does with an API key.
        /// </summary>
        private static FakeHttpMessageHandler Server(HttpStatusCode refresh = HttpStatusCode.NoContent) =>
            new(request =>
            {
                var url = request.RequestUri?.ToString() ?? "";

                if (url.Contains("/Library/Refresh", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(refresh) { Content = new StringContent("") };

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """[{"Id":"user-1","Name":"someone"}]""",
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });

        [Fact]
        public async Task Puts_the_film_where_Jellyfin_will_recognise_it()
        {
            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.False(result.AlreadyExisted);
            Assert.Equal(RemoteFile, result.RemotePath);
            Assert.Equal("a film", transport.TextAt(RemoteFile));
        }

        [Fact]
        public async Task Makes_the_films_directory_before_writing_into_it()
        {
            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            await new JellyfinUploader(transport, client).UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.Contains("movies", transport.Directories);
            Assert.Contains("movies/Arrival (2016)", transport.Directories);

            var madeFolder = transport.Calls.FindIndex(c => c == "mkdir movies/Arrival (2016)");
            var wrote = transport.Calls.FindIndex(c => c.StartsWith("upload ", StringComparison.Ordinal));

            Assert.True(madeFolder >= 0 && madeFolder < wrote);
        }

        /// <summary>
        /// The property that stops a scan running mid-transfer from cataloguing a film that is
        /// four minutes long and getting longer.
        /// </summary>
        [Fact]
        public async Task The_bytes_arrive_under_a_name_no_scan_reads_as_a_film()
        {
            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            await new JellyfinUploader(transport, client).UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.Contains("upload " + RemotePartial, transport.Calls);
            Assert.Contains($"rename {RemotePartial} -> {RemoteFile}", transport.Calls);
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
        }

        [Fact]
        public async Task A_film_already_on_the_server_costs_no_transfer()
        {
            var transport = new FakeSftpTransport();
            transport.Put("movies/Arrival (2016)/Arrival (2016).mp4", "already up there");

            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.True(result.AlreadyExisted);
            Assert.Equal("movies/Arrival (2016)/Arrival (2016).mp4", result.RemotePath);
            Assert.DoesNotContain(transport.Calls, call => call.StartsWith("upload", StringComparison.Ordinal));
            Assert.DoesNotContain(transport.Calls, call => call.StartsWith("rename", StringComparison.Ordinal));
        }

        /// <summary>
        /// Scanning is administrative and the transfer is not, so the rescan must not be the thing
        /// that decides whether an upload happened — but it must also not be asked for when
        /// nothing was sent.
        /// </summary>
        [Fact]
        public async Task Jellyfin_is_asked_to_rescan_only_after_a_film_was_actually_sent()
        {
            var handler = Server();
            using var client = new JellyfinClient(Settings(), handler: handler);

            var transport = new FakeSftpTransport();
            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.True(result.LibraryRefreshed);
            Assert.Contains(handler.Requests, url => url.EndsWith("/Library/Refresh", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Nothing_is_rescanned_when_the_server_already_had_the_film()
        {
            var handler = Server();
            using var client = new JellyfinClient(Settings(), handler: handler);

            var transport = new FakeSftpTransport();
            transport.Put(RemoteFile, "already up there");

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.True(result.AlreadyExisted);
            Assert.False(result.LibraryRefreshed);
            Assert.DoesNotContain(handler.Requests, url => url.Contains("/Library/Refresh", StringComparison.Ordinal));
        }

        /// <summary>
        /// A perfectly ordinary Jellyfin account cannot start a scan. The film is on the disk
        /// either way, so a 403 must not be reported as a failed upload — only as a film that
        /// will appear later.
        /// </summary>
        [Fact]
        public async Task A_server_that_refuses_the_rescan_does_not_fail_the_upload()
        {
            using var client = new JellyfinClient(Settings(), handler: Server(HttpStatusCode.Forbidden));

            var transport = new FakeSftpTransport();
            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.False(result.AlreadyExisted);
            Assert.False(result.LibraryRefreshed);
            Assert.Equal("a film", transport.TextAt(RemoteFile));
            Assert.Contains("next scan", UploadPrompts.Describe(result), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task An_upload_that_fails_leaves_nothing_under_the_films_own_name()
        {
            var transport = new FakeSftpTransport
            {
                UploadFailure = new SshConnectionException("connection lost"),
                StopAfterBytes = 8
            };

            using var client = new JellyfinClient(Settings(), handler: Server());

            await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport, client)
                    .UploadAsync(LocalFilm(new string('x', 64)), "Arrival", 2016, Movies));

            Assert.DoesNotContain(RemoteFile, transport.Files.Keys);
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
            Assert.Contains("delete " + RemotePartial, transport.Calls);
        }

        /// <summary>
        /// The other half of the same property: a transfer that arrives short must not be renamed
        /// into place. A truncated film plays, stops early, and looks like a bad rip rather than a
        /// failed upload — and it does so on everybody's television, not just the uploader's.
        /// </summary>
        [Fact]
        public async Task A_film_that_arrives_short_is_removed_rather_than_renamed()
        {
            var transport = new FakeSftpTransport { TruncateWrittenTo = 3 };
            using var client = new JellyfinClient(Settings(), handler: Server());

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport, client)
                    .UploadAsync(LocalFilm("a whole film"), "Arrival", 2016, Movies));

            Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RemoteFile, transport.Files.Keys);
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
        }

        [Fact]
        public async Task Cancelling_stops_the_transfer_and_takes_the_partial_file_with_it()
        {
            using var cts = new CancellationTokenSource();

            var transport = new FakeSftpTransport();
            transport.DuringUpload = () => cts.Cancel();

            using var client = new JellyfinClient(Settings(), handler: Server());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new JellyfinUploader(transport, client).UploadAsync(
                    LocalFilm(new string('x', 4096)), "Arrival", 2016, Movies, ct: cts.Token));

            Assert.DoesNotContain(RemoteFile, transport.Files.Keys);
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
        }

        /// <summary>
        /// A previous attempt that was killed outright — the process quit, the machine slept —
        /// leaves one of these behind. It is not resumed, so starting again has to clear it out
        /// rather than append to it.
        /// </summary>
        [Fact]
        public async Task A_leftover_partial_from_a_previous_attempt_is_replaced_not_appended_to()
        {
            var transport = new FakeSftpTransport();
            transport.Put(RemotePartial, "bytes from a dead attempt");

            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.False(result.AlreadyExisted);
            Assert.Equal("a film", transport.TextAt(RemoteFile));
        }

        [Fact]
        public async Task A_file_that_is_not_a_video_never_reaches_the_server()
        {
            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            var notes = Path.Combine(_folder, "notes.txt");
            File.WriteAllText(notes, "not a film");

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport, client).UploadAsync(notes, "Arrival", 2016, Movies));

            Assert.Contains("not a video file", error.Message, StringComparison.OrdinalIgnoreCase);

            // Not even a connection: the answer never depended on the server.
            Assert.Equal(0, transport.Connections);
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task A_film_whose_file_has_gone_is_refused_before_anything_connects()
        {
            var transport = new FakeSftpTransport();

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport).UploadAsync(
                    Path.Combine(_folder, "missing.mkv"), "Arrival", 2016, Movies));

            Assert.Contains("missing.mkv", error.Message, StringComparison.Ordinal);
            Assert.Empty(transport.Calls);
        }

        /// <summary>
        /// The message a person meets when the server will not have them. It has to name what to
        /// go and look at; an SSH stack trace names nothing.
        /// </summary>
        [Fact]
        public async Task A_refused_connection_is_reported_as_something_to_act_on()
        {
            var transport = new FakeSftpTransport
            {
                ConnectFailure = new JellyfinException(
                    SftpFailure.Describe(
                        new SshAuthenticationException("Permission denied (publickey)."),
                        "media.invalid",
                        2223,
                        "/keys/id_ed25519"))
            };

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport).UploadAsync(LocalFilm(), "Arrival", 2016, Movies));

            Assert.Contains("media.invalid:2223", error.Message, StringComparison.Ordinal);
            Assert.Contains("/keys/id_ed25519", error.Message, StringComparison.Ordinal);
            Assert.Contains("authorized_keys", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SshAuthenticationException", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The window between the last byte arriving and the rename. Everything has succeeded by
        /// then, so it is easy to leave uncovered — and a failure there strands a file that is the
        /// size of the whole film rather than part of one.
        /// </summary>
        [Fact]
        public async Task A_rename_that_fails_at_the_last_moment_leaves_nothing_behind()
        {
            var transport = new FakeSftpTransport
            {
                RenameFailure = new SftpPermissionDeniedException("denied")
            };

            using var client = new JellyfinClient(Settings(), handler: Server());

            await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport, client).UploadAsync(LocalFilm(), "Arrival", 2016, Movies));

            Assert.DoesNotContain(RemoteFile, transport.Files.Keys);
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
        }

        [Fact]
        public async Task Cancelling_after_the_last_byte_still_takes_the_partial_file_with_it()
        {
            using var cts = new CancellationTokenSource();

            var transport = new FakeSftpTransport();
            transport.DuringLength = () => cts.Cancel();

            using var client = new JellyfinClient(Settings(), handler: Server());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new JellyfinUploader(transport, client)
                    .UploadAsync(LocalFilm(), "Arrival", 2016, Movies, ct: cts.Token));

            Assert.DoesNotContain(RemoteFile, transport.Files.Keys);
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
        }

        /// <summary>
        /// The cleanup needs the connection that has just dropped, so it cannot be promised. What
        /// can be promised is that the film's own name was never put on anything — the rename is
        /// the last step — and that a retry clears whatever is left. A cleanup that fails must
        /// also not become the error the user sees: the transfer's own failure is the one worth
        /// reporting.
        /// </summary>
        [Fact]
        public async Task A_cleanup_that_itself_fails_still_leaves_the_films_name_unused()
        {
            var transport = new FailingCleanupTransport
            {
                // What the real transport hands up: SSH.NET's exception already turned into a
                // sentence by SftpFailure, which is the layer this class sits above.
                UploadFailure = new JellyfinException(
                    SftpFailure.Describe(new SshConnectionException("lost"), "media.invalid", 2223, "/keys/id_ed25519")),
                StopAfterBytes = 8
            };

            using var client = new JellyfinClient(Settings(), handler: Server());

            var error = await Assert.ThrowsAsync<JellyfinException>(() =>
                new JellyfinUploader(transport, client)
                    .UploadAsync(LocalFilm(new string('x', 64)), "Arrival", 2016, Movies));

            // Nothing a scan would read as a film, and nothing under the film's real name.
            Assert.DoesNotContain(RemoteFile, transport.Files.Keys);
            Assert.Contains(RemotePartial, transport.Files.Keys);

            // And the message must not claim the server was left spotless when it was not.
            Assert.DoesNotContain("Nothing was left", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was removed from the server", error.Message, StringComparison.OrdinalIgnoreCase);

            // The failure reported is the transfer's, not the tidying up that failed after it.
            Assert.Contains("dropped", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Clearing a leftover partial before starting is housekeeping, not a precondition: the
        /// transfer overwrites that path regardless. Left unguarded it was the one call that could
        /// put a raw transport exception in front of somebody.
        /// </summary>
        [Fact]
        public async Task A_leftover_that_cannot_be_deleted_does_not_stop_the_upload()
        {
            var transport = new FailingCleanupTransport();
            transport.Put(RemotePartial, "the remains of a dropped connection");

            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.False(result.AlreadyExisted);
            Assert.Equal("a film", transport.TextAt(RemoteFile));
        }

        [Fact]
        public async Task A_retry_clears_what_a_failed_cleanup_left()
        {
            var transport = new FakeSftpTransport();
            transport.Put(RemotePartial, "the remains of a dropped connection");

            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, Movies);

            Assert.Equal("a film", transport.TextAt(RemoteFile));
            Assert.DoesNotContain(RemotePartial, transport.Files.Keys);
            Assert.False(result.AlreadyExisted);
        }

        [Fact]
        public async Task Progress_is_reported_up_to_the_whole_film()
        {
            var reports = new List<JellyfinUploadProgress>();

            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            await new JellyfinUploader(transport, client).UploadAsync(
                LocalFilm("a film that is a bit longer"),
                "Arrival",
                2016,
                Movies,
                new ImmediateProgress<JellyfinUploadProgress>(reports.Add));

            Assert.NotEmpty(reports);
            Assert.Equal(27, reports[^1].BytesSent);
            Assert.Equal(27, reports[^1].TotalBytes);
            Assert.Equal(1d, reports[^1].Fraction);
        }

        [Fact]
        public async Task An_absolute_movies_path_is_honoured_rather_than_made_relative()
        {
            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client)
                .UploadAsync(LocalFilm(), "Arrival", 2016, "/tank/movies");

            Assert.Equal("/tank/movies/Arrival (2016)/Arrival (2016).mkv", result.RemotePath);
        }

        [Fact]
        public async Task The_film_is_named_from_the_catalogue_not_from_the_local_filename()
        {
            var transport = new FakeSftpTransport();
            using var client = new JellyfinClient(Settings(), handler: Server());

            var result = await new JellyfinUploader(transport, client).UploadAsync(
                LocalFilm(name: "Arrival.2016.2160p.UHD.BluRay.x265-GROUP.mkv"),
                "Arrival",
                2016,
                Movies);

            Assert.Equal(RemoteFile, result.RemotePath);
        }

        /// <summary>
        /// A server that cannot be tidied up on, because the connection that would do the tidying
        /// is the thing that failed. The commonest way a multi-hour transfer ends.
        /// </summary>
        private sealed class FailingCleanupTransport : FakeSftpTransport
        {
            public override Task DeleteAsync(string remotePath, CancellationToken ct = default)
                => throw new SshConnectionException("connection lost");
        }

        /// <summary>
        /// <see cref="Progress{T}"/> posts to whatever synchronization context created it, which
        /// in a test is a thread pool thread — so reports arrive after the assertion. This one
        /// runs the callback where it was raised.
        /// </summary>
        private sealed class ImmediateProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public ImmediateProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }
}
