using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UrDatabase.Services;

internal sealed record FfprobeOutput(int ExitCode, string StandardOutput, string StandardError);

internal interface IFfprobeProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct);
    void KillTree();
}

internal sealed class FfprobeRunner
{
    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo, IFfprobeProcess> _start;
    private readonly int _outputLimit;

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe",
        OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

    public FfprobeRunner(string? executable = null, TimeSpan? timeout = null,
        Func<ProcessStartInfo, IFfprobeProcess>? start = null, int outputLimit = 1024 * 1024)
    {
        _executable = executable ?? BundledPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _start = start ?? StartProcess;
        _outputLimit = outputLimit > 0 ? outputLimit : throw new ArgumentOutOfRangeException(nameof(outputLimit));
    }

    public async Task<FfprobeOutput> RunAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-hide_banner", "-print_format", "json",
            "-probesize", "10000000", "-analyzeduration", "5000000",
            "-protocol_whitelist", "file",
            "-show_entries",
            "stream=codec_type,codec_name,width,height,channels,color_transfer,profile:" +
            "stream_tags=language:stream_disposition=default,attached_pic:" +
            "stream_side_data=side_data_type,dv_profile:format=format_name",
            "-i", path
        })
            info.ArgumentList.Add(argument);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        using var process = _start(info);
        using var registration = deadline.Token.Register(() => Kill(process));
        var stdout = ReadBoundedAsync(process.StandardOutput, _outputLimit, process, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError, Math.Min(_outputLimit, 32768), process, deadline.Token);
        var exited = process.WaitForExitAsync(deadline.Token);
        var all = Task.WhenAll(stdout, stderr, exited);
        try
        {
            await all.WaitAsync(deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new FfprobeOutput(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("ffprobe timed out while reading local tracks.");
        }
        finally
        {
            if (!all.IsCompletedSuccessfully)
            {
                Kill(process);
                deadline.Cancel();
                // Observe any pipe failure which arrives after cancellation stopped the wait.
                _ = all.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or Win32Exception)
                {
                    AppLog.Write("media.log", $"ffprobe cleanup failed: {ex.GetType().Name}");
                }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, int limit, IFfprobeProcess process, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit)
                    throw new InvalidDataException("ffprobe output exceeded its safety limit.");
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(IFfprobeProcess process)
    {
        try { process.KillTree(); }
        catch (InvalidOperationException) { /* The process has already exited. */ }
        catch (Win32Exception ex) { AppLog.Write("media.log", $"could not stop ffprobe: {ex.NativeErrorCode}"); }
        catch (AggregateException) { AppLog.Write("media.log", "could not stop every process in the ffprobe tree"); }
    }

    private static IFfprobeProcess StartProcess(ProcessStartInfo info)
    {
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("ffprobe did not start.");
            return new OwnedProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private sealed class OwnedProcess(Process process) : IFfprobeProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
        public void KillTree()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        public void Dispose() => process.Dispose();
    }
}
