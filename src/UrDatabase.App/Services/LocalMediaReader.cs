using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services;

internal sealed record LocalMediaResult(MediaInfo? Info, bool Probed, string? Notice);

/// <summary>Reads only the selected film, with a bounded in-memory cache of unchanged files.</summary>
internal sealed class LocalMediaReader
{
    private readonly Func<string, CancellationToken, Task<FfprobeOutput>> _probe;
    private readonly TimeSpan _timeout;
    private readonly int _cacheCapacity;
    private readonly Dictionary<FileSnapshot, MediaInfo> _cache = new();
    private readonly object _gate = new();

    public LocalMediaReader(Func<string, CancellationToken, Task<FfprobeOutput>>? probe = null,
        TimeSpan? timeout = null, int cacheCapacity = 128)
    {
        _probe = probe ?? new FfprobeRunner().RunAsync;
        _timeout = timeout ?? TimeSpan.FromSeconds(12);
        _cacheCapacity = cacheCapacity > 0 ? cacheCapacity : throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
    }

    public async Task<LocalMediaResult> ReadAsync(string? path, bool refresh = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return new LocalMediaResult(null, false, null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;
        FileSnapshot? snapshot = null;
        try
        {
            // Even stat and Process.Start can stall on disconnected volumes. Neither belongs on
            // the UI thread, and a late stat/probe result must not enter the cache after timeout.
            snapshot = await Task.Run(() => FileSnapshot.Read(path), token)
                .WaitAsync(token).ConfigureAwait(false);
            var file = snapshot;
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (refresh) _cache.Remove(file);
                    if (!refresh && _cache.TryGetValue(file, out var cached))
                        return new LocalMediaResult(Copy(cached), true, null);
                }

                var output = await _probe(file.Path, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (output.ExitCode != 0)
                    throw new InvalidDataException($"ffprobe exited with code {output.ExitCode}: {Diagnostic(output.StandardError)}");
                var info = FfprobeMediaInfo.Parse(output.StandardOutput);
                info.SizeBytes = file.Size > 0 ? file.Size : null;
                if (FileSnapshot.Read(file.Path) != file)
                    throw new IOException("The local file changed while ffprobe was reading it.");
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(output.StandardError))
                {
                    AppLog.Write("media.log", $"ffprobe diagnostics: {Diagnostic(output.StandardError)}");
                    return new LocalMediaResult(info, true,
                        "Some local track metadata could not be read. Available track tags are shown; use Refresh to retry.");
                }
                lock (_gate)
                {
                    if (_cache.Count >= _cacheCapacity && !_cache.ContainsKey(file))
                        _cache.Remove(_cache.Keys.First());
                    _cache[file] = Copy(info);
                }
                return new LocalMediaResult(info, true, null);
            }, token).WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception
                                   or JsonException or ArgumentException or NotSupportedException
                                   or TimeoutException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            var reason = ex switch
            {
                OperationCanceledException or TimeoutException => "local track reading timed out",
                Win32Exception => "bundled ffprobe is missing or could not start",
                FileNotFoundException or DirectoryNotFoundException => "the local file is unavailable",
                _ => "ffprobe could not read the local tracks"
            };
            return await Task.Run(() =>
            {
                AppLog.Write("media.log", $"ffprobe fallback: {reason} ({ex.GetType().Name}): {Diagnostic(ex.Message)}");
                return new LocalMediaResult(LocalMedia.Describe(path, _ => snapshot?.Size), false,
                    $"Local media: {reason}. Track badges are filename hints, not measured tracks. Use Refresh to retry.");
            }, ct).ConfigureAwait(false);
        }
    }

    private static string Diagnostic(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return singleLine.Length <= 512 ? singleLine : singleLine[..512];
    }

    private static MediaInfo Copy(MediaInfo info) => new()
    {
        Width = info.Width, Height = info.Height, ClaimedQuality = info.ClaimedQuality,
        VideoCodec = info.VideoCodec, VideoRange = info.VideoRange, AudioCodec = info.AudioCodec,
        AudioChannels = info.AudioChannels, HasAtmos = info.HasAtmos, Source = info.Source,
        Container = info.Container, SizeBytes = info.SizeBytes, IsFilenameEstimate = info.IsFilenameEstimate,
        AudioLanguages = new(info.AudioLanguages), SubtitleLanguages = new(info.SubtitleLanguages)
    };

    private sealed record FileSnapshot(string Path, long Size, DateTime Modified)
    {
        public static FileSnapshot Read(string path)
        {
            var file = new FileInfo(System.IO.Path.GetFullPath(path));
            file.Refresh();
            if (!file.Exists) throw new FileNotFoundException("The local media file is unavailable.");
            return new FileSnapshot(file.FullName, file.Length, file.LastWriteTimeUtc);
        }
    }
}
