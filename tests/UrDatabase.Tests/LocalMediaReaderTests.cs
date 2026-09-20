using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class LocalMediaReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.CurrentDirectory, ".local-media-tests", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _log;

    internal const string Tracks = """
        {
          "streams": [
            {"codec_type":"video","codec_name":"mjpeg","width":600,"height":600,"disposition":{"attached_pic":1}},
            {"codec_type":"video","codec_name":"hevc","width":3840,"height":1600,"color_transfer":"smpte2084"},
            {"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"eng"}},
            {"codec_type":"audio","codec_name":"eac3","channels":6,"disposition":{"default":1},"tags":{"language":"fra"}},
            {"codec_type":"audio","tags":{"language":"fre"}},
            {"codec_type":"subtitle","tags":{"language":"spa"}},
            {"codec_type":"subtitle"}
          ],
          "format":{"format_name":"matroska,webm","size":"999999"}
        }
        """;

    public LocalMediaReaderTests()
    {
        Directory.CreateDirectory(_root);
        _log = AppLog.Redirect(_root);
    }

    private string Movie(string name = "Film.2020.720p.x264.GERMAN.mkv")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[2048]);
        return path;
    }

    private static FfprobeOutput Output(string json = Tracks) => new(0, json, "");

    [Fact]
    public async Task Actual_streams_replace_filename_claims_and_choose_default_audio_not_cover_art()
    {
        var reader = new LocalMediaReader((_, _) => Task.FromResult(Output()));
        var result = await reader.ReadAsync(Movie());
        var info = Assert.IsType<MediaInfo>(result.Info);

        Assert.True(result.Probed);
        Assert.Null(result.Notice);
        Assert.Equal(3840, info.Width);
        Assert.Equal(1600, info.Height);
        Assert.Null(info.ClaimedQuality);
        Assert.Equal("hevc", info.VideoCodec);
        Assert.Equal("HDR", info.VideoRange);
        Assert.Equal("eac3", info.AudioCodec);
        Assert.Equal(6, info.AudioChannels);
        Assert.Equal(new[] { "eng", "fra", "fre" }, info.AudioLanguages);
        Assert.Equal(new[] { "spa", "und" }, info.SubtitleLanguages);
        Assert.Equal(2048, info.SizeBytes);
        Assert.False(info.IsFilenameEstimate);
        Assert.Contains(MediaFlags.For(info), flag => flag.Text == "EN");
        Assert.Contains(MediaFlags.For(info), flag => flag.Text == "FR");
        Assert.DoesNotContain(MediaFlags.For(info), flag => flag.Text == "DE");
    }

    [Fact]
    public async Task Successful_untagged_tracks_do_not_inherit_filename_languages_codecs_hdr_or_atmos()
    {
        var reader = new LocalMediaReader((_, _) => Task.FromResult(Output(
            """{"streams":[{"codec_type":"video"},{"codec_type":"audio","channels":2},{"codec_type":"subtitle"}]}""")));
        var result = await reader.ReadAsync(Movie("Film.2020.2160p.HDR10.Atmos.HEVC.ENGLISH.mkv"));

        Assert.True(result.Probed);
        Assert.Null(result.Info!.Width);
        Assert.Null(result.Info.ClaimedQuality);
        Assert.Null(result.Info.VideoCodec);
        Assert.Null(result.Info.VideoRange);
        Assert.False(result.Info.HasAtmos);
        Assert.Equal(new[] { "und" }, result.Info.AudioLanguages);
        Assert.Equal(new[] { "und" }, result.Info.SubtitleLanguages);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"streams":[]}""")]
    [InlineData("""{"streams":{}}""")]
    public async Task Invalid_probe_output_is_logged_visible_filename_fallback_and_not_cached(string json)
    {
        var calls = 0;
        var reader = new LocalMediaReader((_, _) => { calls++; return Task.FromResult(Output(json)); });
        var path = Movie();
        var result = await reader.ReadAsync(path);
        await reader.ReadAsync(path);

        Assert.False(result.Probed);
        Assert.Contains("filename", result.Notice);
        Assert.True(result.Info!.IsFilenameEstimate);
        Assert.Equal("720p", result.Info.ClaimedQuality);
        Assert.Equal(2, calls);
        Assert.Contains("ffprobe", File.ReadAllText(Path.Combine(_root, "media.log")));
    }

    [Fact]
    public async Task Nonzero_exit_does_not_trust_even_valid_json()
    {
        var reader = new LocalMediaReader((_, _) => Task.FromResult(new FfprobeOutput(1, Tracks, "invalid media")));
        var result = await reader.ReadAsync(Movie());

        Assert.False(result.Probed);
        Assert.Null(result.Info!.Width);
        Assert.Contains("invalid media", File.ReadAllText(Path.Combine(_root, "media.log")));
    }

    [Fact]
    public async Task Successful_probe_diagnostics_are_visible_and_logged_not_silently_cached()
    {
        var calls = 0;
        var reader = new LocalMediaReader((_, _) =>
        {
            calls++;
            return Task.FromResult(new FfprobeOutput(0, Tracks, "error reading optional stream data"));
        });
        var path = Movie();
        var result = await reader.ReadAsync(path);
        await reader.ReadAsync(path);

        Assert.True(result.Probed);
        Assert.NotEmpty(result.Notice!);
        Assert.Equal(3840, result.Info!.Width);
        Assert.Equal(2, calls);
        Assert.Contains("optional stream data", File.ReadAllText(Path.Combine(_root, "media.log")));
    }

    [Fact]
    public async Task Missing_developer_binary_falls_back_without_hiding_the_reason()
    {
        var runner = new FfprobeRunner(Path.Combine(_root, "absent-ffprobe"));
        var reader = new LocalMediaReader(runner.RunAsync);
        var result = await reader.ReadAsync(Movie());

        Assert.False(result.Probed);
        Assert.Contains("ffprobe", result.Notice);
        Assert.Contains("filename", result.Notice);
        Assert.Equal("h264", result.Info!.VideoCodec);
    }

    [Fact]
    public async Task Missing_local_file_is_not_probed_and_keeps_filename_hints()
    {
        var reader = new LocalMediaReader((_, _) => throw new InvalidOperationException("Should not start"));
        var result = await reader.ReadAsync(Path.Combine(_root, "Absent.2020.1080p.mkv"));

        Assert.False(result.Probed);
        Assert.Null(result.Info!.SizeBytes);
        Assert.Equal("1080p", result.Info.ClaimedQuality);
        Assert.NotEmpty(result.Notice!);
    }

    [Fact]
    public async Task Cache_is_keyed_by_full_path_size_and_mtime_and_refresh_bypasses_it()
    {
        var calls = 0;
        var reader = new LocalMediaReader((_, _) => { calls++; return Task.FromResult(Output()); });
        var path = Movie();
        var first = await reader.ReadAsync(path);
        first.Info!.AudioLanguages.Clear();
        Assert.Equal(3, (await reader.ReadAsync(path)).Info!.AudioLanguages.Count);
        Assert.Equal(1, calls);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        await reader.ReadAsync(path);
        Assert.Equal(2, calls);
        File.AppendAllText(path, "changed");
        await reader.ReadAsync(path);
        Assert.Equal(3, calls);
        await reader.ReadAsync(path, refresh: true);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Cache_is_bounded_and_does_not_retain_every_film_in_a_library()
    {
        var calls = 0;
        var reader = new LocalMediaReader((_, _) => { calls++; return Task.FromResult(Output()); }, cacheCapacity: 2);
        var first = Movie("One.mkv");
        await reader.ReadAsync(first);
        await reader.ReadAsync(Movie("Two.mkv"));
        await reader.ReadAsync(Movie("Three.mkv"));
        await reader.ReadAsync(first);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task File_changed_during_probe_is_not_cached_as_the_old_copy()
    {
        var calls = 0;
        var reader = new LocalMediaReader((path, _) =>
        {
            calls++;
            File.AppendAllText(path, "replacement");
            return Task.FromResult(Output());
        });
        var path = Movie();
        Assert.False((await reader.ReadAsync(path)).Probed);
        Assert.False((await reader.ReadAsync(path)).Probed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancellation_reaches_the_runner_and_never_publishes_or_caches_a_fallback()
    {
        var started = new TaskCompletionSource(CreationOptions);
        CancellationToken received = default;
        var reader = new LocalMediaReader(async (_, ct) =>
        {
            received = ct;
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Output();
        });
        using var cancellation = new CancellationTokenSource();
        var read = reader.ReadAsync(Movie(), ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(received.IsCancellationRequested);
        Assert.False(File.Exists(Path.Combine(_root, "media.log")));
    }

    [Fact]
    public async Task Timeout_cancels_probe_and_is_reported_as_fallback()
    {
        CancellationToken received = default;
        var reader = new LocalMediaReader(async (_, ct) =>
        {
            received = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Output();
        }, timeout: TimeSpan.FromMilliseconds(100));

        var result = await reader.ReadAsync(Movie());
        Assert.True(received.IsCancellationRequested);
        Assert.False(result.Probed);
        Assert.Contains("timed out", result.Notice);
    }

    [Fact]
    public async Task Blocking_runner_start_does_not_block_the_calling_ui_thread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new LocalMediaReader((_, _) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return Task.FromResult(Output());
        });
        var task = reader.ReadAsync(Movie());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(task.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await task).Probed);
    }

    [Fact]
    public async Task A_late_result_cannot_replace_a_new_file_or_a_different_page()
    {
        var reply = new TaskCompletionSource<FfprobeOutput>(CreationOptions);
        var reader = new LocalMediaReader((_, _) => reply.Task);
        var movie = new MovieDetailsVm { FilePath = Movie() };
        using var load = new MovieMediaLoad(movie, reader);
        var pending = load.LoadAsync();
        movie.FilePath = Movie("New.mkv");
        reply.SetResult(Output());
        await pending;

        Assert.False(load.TryApply(movie));
        Assert.False(load.TryApply(new MovieDetailsVm { FilePath = load.Path }));
        Assert.Null(movie.Media);
    }

    [Fact]
    public async Task Downloaded_local_tracks_replace_server_tracks_even_after_server_refresh()
    {
        var reader = new LocalMediaReader((_, _) => Task.FromResult(Output()));
        var movie = new MovieDetailsVm
        {
            IsRemote = true, DownloadedPath = Movie(), Media = new MediaInfo { Width = 720 }
        };
        using var load = new MovieMediaLoad(movie, reader);
        await load.LoadAsync();
        Assert.True(load.TryApply(movie));
        Assert.Equal(3840, movie.Media!.Width);
        movie.Media = new MediaInfo { Width = 1920, AudioLanguages = new() { "deu" } };
        Assert.True(load.TryApply(movie));
        Assert.Equal(3840, movie.Media.Width);
        Assert.DoesNotContain("deu", movie.Media.AudioLanguages);
    }

    [Fact]
    public async Task Leaving_a_page_cancels_its_probe_and_refuses_the_result()
    {
        var started = new TaskCompletionSource(CreationOptions);
        var reader = new LocalMediaReader(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Output();
        });
        var movie = new MovieDetailsVm { FilePath = Movie() };
        using var load = new MovieMediaLoad(movie, reader);
        var pending = load.LoadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        load.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(load.TryApply(movie));
    }

    private const TaskCreationOptions CreationOptions = TaskCreationOptions.RunContinuationsAsynchronously;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
