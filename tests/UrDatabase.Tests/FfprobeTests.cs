using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class FfprobeTests
{
    [Fact]
    public void Optional_wrongly_typed_fields_and_unknown_streams_do_not_break_valid_tracks()
    {
        var info = FfprobeMediaInfo.Parse("""
            {"streams":[null, {"codec_type":"attachment"}, {"codec_type":"video","width":"1920","height":-1},
            {"codec_type":"audio","codec_name":"unknown-codec","channels":"6","tags":{"language":42}}]}
            """);
        Assert.Null(info.Width);
        Assert.Null(info.Height);
        Assert.Null(info.AudioChannels);
        Assert.Equal("unknown-codec", info.AudioCodec);
        Assert.Equal(new[] { "und" }, info.AudioLanguages);
    }

    [Theory]
    [InlineData("smpte2084", "HDR")]
    [InlineData("arib-std-b67", "HLG")]
    [InlineData("bt709", null)]
    [InlineData("unknown", null)]
    public void Dynamic_range_is_only_inferred_from_explicit_transfer_metadata(string transfer, string? expected)
    {
        var info = FfprobeMediaInfo.Parse($$"""
            {"streams":[{"codec_type":"video","color_transfer":"{{transfer}}"}]}
            """);
        Assert.Equal(expected, info.VideoRange);
    }

    [Fact]
    public void Dolby_vision_configuration_is_recognised_but_truehd_alone_is_not_atmos()
    {
        var info = FfprobeMediaInfo.Parse("""
            {"streams":[{"codec_type":"video","side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":8}]},
            {"codec_type":"audio","codec_name":"truehd","channels":8}]}
            """);
        Assert.Equal("DOVI", info.VideoRange);
        Assert.False(info.HasAtmos);
    }

    [Fact]
    public void First_audio_track_is_used_when_none_is_marked_default()
    {
        var info = FfprobeMediaInfo.Parse("""
            {"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"tags":{"language":"jpn"}},
            {"codec_type":"audio","codec_name":"ac3","channels":6,"tags":{"language":"eng"}}]}
            """);
        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(2, info.AudioChannels);
        Assert.Equal(new[] { "jpn", "eng" }, info.AudioLanguages);
    }

    [Fact]
    public async Task Arguments_keep_spaces_quotes_unicode_and_option_like_names_as_one_literal_input()
    {
        var path = Path.GetFullPath("Films/-Film \"é\" $(touch no) & title.mkv");
        ProcessStartInfo? actual = null;
        var process = new FakeProcess(LocalMediaReaderTests.Tracks);
        var runner = new FfprobeRunner("bundled-probe", start: info => { actual = info; return process; });
        var result = await runner.RunAsync(path, default);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(actual);
        Assert.False(actual.UseShellExecute);
        Assert.True(actual.RedirectStandardOutput);
        Assert.True(actual.RedirectStandardError);
        Assert.True(actual.CreateNoWindow);
        Assert.Equal("", actual.Arguments);
        Assert.Equal("-i", actual.ArgumentList[^2]);
        Assert.Equal(path, actual.ArgumentList[^1]);
        Assert.Contains("file", actual.ArgumentList);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void Default_executable_is_bundled_not_resolved_from_path()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe",
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"), FfprobeRunner.BundledPath);
    }

    [Fact]
    public async Task Cancellation_kills_only_the_owned_process_tree_and_disposes_streams()
    {
        var process = new FakeProcess("{}", running: true);
        var runner = new FfprobeRunner("bundled-probe", start: _ => process);
        using var cts = new CancellationTokenSource();
        var pending = runner.RunAsync("film.mkv", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task Timeout_kills_owned_process_and_is_distinct_from_caller_cancellation()
    {
        var process = new FakeProcess("{}", running: true);
        var runner = new FfprobeRunner("bundled-probe", TimeSpan.FromMilliseconds(30), start: _ => process);
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("film.mkv", default));
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_output_pipe_is_bounded_and_oversized_output_kills_the_process(bool stdout)
    {
        var process = new FakeProcess(stdout ? new string('x', 4096) : "{}", running: true,
            error: stdout ? "" : new string('x', 4096));
        var runner = new FfprobeRunner("bundled-probe", start: _ => process, outputLimit: 1024);
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync("film.mkv", default));
        Assert.True(process.Killed);
        Assert.True(process.Disposed);
    }

    private sealed class FakeProcess : IFfprobeProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public int ExitCode => 0;
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }

        public FakeProcess(string output, bool running = false, string error = "")
        {
            StandardOutput = new StringReader(output);
            StandardError = new StringReader(error);
            if (!running) _exited.SetResult();
        }

        public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);
        public void KillTree() { Killed = true; _exited.TrySetResult(); }
        public void Dispose()
        {
            Disposed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
