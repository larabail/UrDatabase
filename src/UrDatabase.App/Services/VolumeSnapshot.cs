using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;

namespace UrDatabase.Services;

public sealed record VolumeMount(string Root, DriveKind Kind, DriveProbeOutcome Probe)
{
    public string? Id => SupportsIdentity && Probe.State == DriveState.Connected && !string.IsNullOrWhiteSpace(Probe.Result.Serial)
        ? "serial:" + Probe.Result.Serial.Trim().ToUpperInvariant() : null;
    public bool SupportsIdentity => (Kind is DriveKind.Fixed or DriveKind.Removable)
        && !DriveEnumeration.LooksVirtual(Probe.Result.Label, Probe.Result.Format);
    public string PathStyle => VolumePaths.IsWindows(Root) ? "windows"
        : Probe.Result.CaseSensitive == false ? "posix-insensitive" : "posix";
}

public interface IVolumeSnapshotSource
{
    Task<VolumeSnapshot> CaptureAsync(IEnumerable<string> paths, CancellationToken ct = default);
}

public sealed record VolumeSnapshot(IReadOnlyList<VolumeMount> Mounts, bool Complete)
{
    public static VolumeSnapshot Empty { get; } = new(Array.Empty<VolumeMount>(), false);

    public VolumeMount? Containing(string path) => Mounts
        .Where(m => VolumePaths.TryRelative(m.Root, path, out _, m.PathStyle))
        .OrderByDescending(m => m.Root.Length).FirstOrDefault();

    public IReadOnlyList<VolumeMount> WithIdentity(string id) =>
        Mounts.Where(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>One bounded snapshot per operation, with in-flight OS calls shared across callers.</summary>
public sealed class VolumeSnapshotSource : IVolumeSnapshotSource
{
    public static VolumeSnapshotSource Shared { get; } = new();
    private readonly IDriveEnumerator _enumerator;
    private readonly IDriveProbe _probe;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _slots = new(8);
    private readonly ConcurrentDictionary<string, Lazy<Pending<DriveProbeResult>>> _probes = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Pending<IReadOnlyList<MountedVolume>>? _listing;

    private sealed class Pending<T>(Task<T> work)
    {
        private int _expired;
        public Task<T> Work { get; } = work;
        public bool Expired => Volatile.Read(ref _expired) != 0;
        public void Expire() => Interlocked.Exchange(ref _expired, 1);
    }

    public VolumeSnapshotSource(IDriveEnumerator? enumerator = null, IDriveProbe? probe = null, TimeSpan? timeout = null)
    {
        _enumerator = enumerator ?? SystemDriveEnumerator.Instance;
        _probe = probe ?? FileSystemDriveProbe.Instance;
        _timeout = timeout ?? DriveMonitor.DefaultTimeout;
    }

    public async Task<VolumeSnapshot> CaptureAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Pending<IReadOnlyList<MountedVolume>> listing;
        lock (_gate)
        {
            if (_listing is null || _listing.Work.IsCompleted)
                _listing = new(Task.Factory.StartNew(_enumerator.Enumerate, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default));
            listing = _listing;
        }
        IReadOnlyList<MountedVolume> mounted;
        var complete = true;
        try
        {
            if (listing.Expired) throw new TimeoutException("The previous volume listing has not finished.");
            mounted = await listing.Work.WaitAsync(_timeout, ct).ConfigureAwait(false);
            if (listing.Expired) throw new TimeoutException("A late volume list was discarded.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            listing.Expire();
            AppLog.Write("drives.log", $"Could not enumerate volumes: {ex.Message}");
            _ = listing.Work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            mounted = Array.Empty<MountedVolume>();
            complete = false;
        }

        // UNC shares may not appear in the OS list; they are explicitly path-only.
        var shares = paths.Select(DriveVolumes.VolumeOf).OfType<string>()
            .Where(p => p.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(p => new MountedVolume(p, DriveKind.Network));
        var roots = mounted.Concat(shares).GroupBy(m => VolumePaths.Absolute(m.Root), StringComparer.Ordinal)
            .Select(g => g.First()).ToArray();
        var results = await Task.WhenAll(roots.Select(async mount =>
            new VolumeMount(VolumePaths.Absolute(mount.Root), mount.Kind, await ProbeAsync(mount.Root, ct))))
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new VolumeSnapshot(results, complete);
    }

    private async Task<DriveProbeOutcome> ProbeAsync(string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lazy = _probes.GetOrAdd(root, _ => new Lazy<Pending<DriveProbeResult>>(() => new(RunProbeAsync(root))));
        var pending = lazy.Value;
        var task = pending.Work;
        if (pending.Expired && task.IsCompleted)
        {
            _probes.TryRemove(new KeyValuePair<string, Lazy<Pending<DriveProbeResult>>>(root, lazy));
            return await ProbeAsync(root, ct).ConfigureAwait(false);
        }
        try
        {
            if (pending.Expired) return new(DriveState.NotResponding, DriveProbeResult.Unavailable);
            var result = await task.WaitAsync(_timeout, ct).ConfigureAwait(false);
            if (pending.Expired) return new(DriveState.NotResponding, DriveProbeResult.Unavailable);
            return new(result.IsAvailable ? DriveState.Connected : DriveState.Disconnected, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            pending.Expire();
            return new(DriveState.NotResponding, DriveProbeResult.Unavailable);
        }
        catch (Exception ex)
        {
            AppLog.Write("drives.log", $"Could not probe {root}: {ex.Message}");
            return new(DriveState.NotResponding, DriveProbeResult.Unavailable);
        }
        finally
        {
            // A timed-out native call keeps its slot until it really exits; repeated clicks
            // cannot accumulate an unbounded number of threads stuck on the same dead share.
            if (task.IsCompleted) _probes.TryRemove(new KeyValuePair<string, Lazy<Pending<DriveProbeResult>>>(root, lazy));
            else _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private async Task<DriveProbeResult> RunProbeAsync(string root)
    {
        await _slots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(() => _probe.Probe(root), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally { _slots.Release(); }
    }
}
