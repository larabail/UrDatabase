using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services;

public sealed record ConfiguredRoot(string Kind, string Token, string Path, string ScopePath = "");
public sealed record RelocationRequest(string OldRoot, string NewRoot, string? SourceVolumeId = null, bool PathOnly = false);
public sealed record RecoveryFile(long Id, string OldPath, LocationBinding Target, LocationState State,
    string? Fingerprint, long Revision, long? MovieId, long? EpisodeId, bool LinkedByHand, string? MissingSince);
public sealed record RecoveryRoot(string Kind, string Token, string OldPath, LocationBinding Target, long? Revision, LocationState State, string ScopePath = "");
public sealed record RecoveryPreview(RelocationRequest Request, IReadOnlyList<RecoveryFile> Files,
    IReadOnlyList<RecoveryRoot> Roots, IReadOnlyList<string> Conflicts, int Excluded)
{
    public bool CanApply => Conflicts.Count == 0 && Files.Count + Roots.Count > 0;
    public int Present => Files.Count(f => f.State == LocationState.Available);
    public int Films => Files.Select(f => f.MovieId).OfType<long>().Distinct().Count();
    public int Episodes => Files.Select(f => f.EpisodeId).OfType<long>().Distinct().Count();
    public string Summary => $"{Count(Files.Count, "file")} ({Count(Films, "film")}, {Count(Episodes, "episode")}), {Count(Roots.Count, "configured folder")}. " +
        $"{Present} present, {Files.Count - Present} absent or unreadable, {Excluded} other-identity entries excluded. " +
        $"{Conflicts.Count} conflicts. Media files will not be moved or renamed.";
    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}

public sealed class DiskRecovery
{
    private readonly string _db;
    private readonly IVolumeSnapshotSource _volumes;
    private readonly Func<IReadOnlyList<ConfiguredRoot>> _configured;

    public DiskRecovery(string databasePath, Func<IReadOnlyList<ConfiguredRoot>> configured,
        IVolumeSnapshotSource? volumes = null)
    {
        _db = databasePath;
        _configured = configured;
        _volumes = volumes ?? VolumeSnapshotSource.Shared;
    }

    public static RelocationRequest FromFile(LocatedFile file, string selected, VolumeSnapshot snapshot,
        string? knownOldRoot = null)
    {
        var target = FileLocations.Capture(selected, snapshot, allowPathOnly: true);
        if (!System.IO.File.Exists(selected) || !ScanService.IsVideoFile(selected))
            throw new ArgumentException("Choose the existing video file you recognise.");
        var mount = snapshot.Containing(selected) ?? throw new IOException("The destination volume is unknown.");
        var oldRoot = knownOldRoot;
        if (oldRoot is null && file.Mode == "volume" && file.RelativePath is not null)
            oldRoot = FileLocations.SavedRoot(file.Binding);
        oldRoot ??= DriveVolumes.VolumeOf(file.FilePath);
        if (oldRoot is null || !VolumePaths.TryRelative(oldRoot, file.FilePath, out var before)
            || !VolumePaths.TryRelative(mount.Root, selected, out var after)
            || !string.Equals(before, after, VolumePaths.Comparison(mount.Root, mount.PathStyle)))
            throw new ArgumentException("The relative folder layout changed or its old disk boundary is unknown. Choose the old and new folder roots explicitly.");
        return new(oldRoot, mount.Root, file.Mode == "volume" ? file.VolumeId : null, target.Mode == "path");
    }

    public async Task<RecoveryPreview> PreviewAsync(RelocationRequest request, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var worker = Task.Run(async () =>
        {
            var snapshot = await _volumes.CaptureAsync(new[] { request.NewRoot }, token);
            using var conn = Database.Open(_db);
            return Build(conn, request, snapshot, _configured(), token);
        }, token);
        _ = worker.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Checking the mapping took too long. No links were changed; reconnect the disk and preview again.");
        }
    }

    private static RecoveryPreview Build(SqliteConnection conn, RelocationRequest request, VolumeSnapshot snapshot,
        IReadOnlyList<ConfiguredRoot> configured, CancellationToken ct, SqliteTransaction? tx = null, bool inspectFiles = true)
    {
        var source = VolumePaths.Absolute(request.OldRoot);
        var destination = VolumePaths.Absolute(request.NewRoot);
        LocationBinding Bind(string path) => inspectFiles ? FileLocations.Capture(path, snapshot, request.PathOnly)
            : FileLocations.CapturePath(path, snapshot, request.PathOnly);
        Bind(destination);
        if (inspectFiles && !Directory.Exists(destination)) throw new IOException("The destination folder is no longer available.");
        var all = FileLocations.Files(conn, tx);
        var storedRoots = FileLocations.Roots(conn);
        string? sourceScope = null;
        if (request.SourceVolumeId is not null)
        {
            var scopes = all.Select(f => f.Binding).Concat(storedRoots.Select(r => r.Binding))
                .Where(b => b.Mode == "volume" && b.VolumeId == request.SourceVolumeId)
                .Select(b => VolumePaths.TryRelative(FileLocations.SavedRoot(b), source, out var scope, b.PathStyle) ? scope : null)
                .OfType<string>().Distinct().ToList();
            if (scopes.Count != 1)
                throw new InvalidOperationException("The old folder's scope within this disk is ambiguous. Select a saved file or configured folder as the starting point.");
            sourceScope = scopes[0];
        }
        bool InScope(LocationBinding binding, out string relative) =>
            sourceScope is not null && binding.Mode == "volume" && binding.VolumeId == request.SourceVolumeId
                ? VolumePaths.TryRelative(VolumePaths.Join("/", sourceScope),
                    VolumePaths.Join("/", binding.RelativePath!), out relative, binding.PathStyle)
                : VolumePaths.TryRelative(source, binding.Path, out relative, binding.PathStyle);
        var files = new List<RecoveryFile>();
        var conflicts = new List<string>();
        var excluded = 0;
        bool Includes(string mode, string? volume) =>
            request.SourceVolumeId is null ? mode != "volume" : mode == "volume" && volume == request.SourceVolumeId;
        foreach (var file in all)
        {
            ct.ThrowIfCancellationRequested();
            if (!InScope(file.Binding, out var relative)) continue;
            if (!Includes(file.Mode, file.VolumeId)) { excluded++; continue; }
            if (sourceScope is null && source == DriveVolumes.VolumeOf(source) && DriveVolumes.VolumeOf(file.FilePath) != source)
            {
                excluded++;
                continue;
            }
            var target = Bind(VolumePaths.Join(destination, relative));
            var state = inspectFiles ? FileLocations.Resolve(target, snapshot).State : LocationState.Missing;
            if (state is not (LocationState.Available or LocationState.Missing))
                conflicts.Add($"{target.Path} could not be safely validated ({state}).");
            string? stamp = null;
            if (state == LocationState.Available)
            {
                var info = new FileInfo(target.Path);
                stamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }
            files.Add(new(file.Id, file.FilePath, target, state, stamp, file.Revision,
                file.MovieId, file.EpisodeId, file.LinkedByHand, file.MissingSince));
        }
        var selected = files.Select(f => f.Id).ToHashSet();
        foreach (var file in files)
        {
            if (all.Any(other => !selected.Contains(other.Id) &&
                (other.Mode == "volume" && file.Target.Mode == "volume"
                    ? other.VolumeId == file.Target.VolumeId && other.RelativeKey == file.Target.RelativeKey
                    : other.Mode != "volume" && string.Equals(VolumePaths.Absolute(other.FilePath),
                        file.Target.Path, VolumePaths.Comparison(file.Target.Path)))))
                conflicts.Add($"{file.Target.Path} already belongs to another catalogue file.");
        }
        if (files.GroupBy(f => f.Target.Mode == "volume"
                ? f.Target.VolumeId + "\0" + f.Target.RelativeKey : f.Target.Path).Any(g => g.Count() > 1))
            conflicts.Add("More than one selected file maps to the same destination.");
        var roots = new List<RecoveryRoot>();
        var activeScopes = storedRoots.Where(r => r.ScopePath.Length > 0
            && configured.Any(c => c.Kind == r.Kind && c.Token == r.Token));
        var configuredScopes = configured.Concat(activeScopes.Select(r => new ConfiguredRoot(r.Kind, r.Token, r.FilePath, r.ScopePath)));
        foreach (var item in configuredScopes.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var stored = FileLocations.Root(conn, item.Kind, item.Token, item.ScopePath);
            var oldPath = stored?.FilePath ?? item.Path;
            if (!InScope(stored?.Binding ?? LocationBinding.Legacy(oldPath), out var relative))
            {
                if (item.Kind != "watch" || !VolumePaths.TryRelative(oldPath, source, out var within, stored?.PathStyle)
                    || within.Length == 0) continue;
                if (request.SourceVolumeId is not null && stored?.Mode == "volume" && stored.VolumeId != request.SourceVolumeId)
                {
                    excluded++;
                    continue;
                }
                if (stored?.Mode == "volume")
                {
                    var parent = FileLocations.Project(stored.Binding, snapshot);
                    if (parent.Available && snapshot.Containing(destination)?.Id == stored.VolumeId
                        && VolumePaths.TryRelative(parent.Path!, destination, out _, stored.PathStyle))
                        continue;
                }
                var existing = activeScopes.FirstOrDefault(r => r.Kind == item.Kind && r.Token == item.Token
                    && string.Equals(r.FilePath, source, VolumePaths.Comparison(source, r.PathStyle)));
                var scope = existing?.ScopePath ?? source;
                var collision = FileLocations.Root(conn, item.Kind, item.Token, scope);
                var child = Bind(destination);
                if (collision is not null && existing is null)
                {
                    if (collision.Binding == child) existing = collision;
                    else
                    {
                        conflicts.Add($"The scan scope for {source} already has a different location. Select that saved scope explicitly.");
                        continue;
                    }
                }
                if (roots.Any(r => r.Kind == item.Kind && r.Token == item.Token && r.ScopePath == scope)) continue;
                var childState = inspectFiles ? FileLocations.Resolve(child, snapshot, Directory.Exists).State : LocationState.Missing;
                roots.Add(new(item.Kind, item.Token, source, child, existing?.Revision, childState, scope));
                continue;
            }
            if (stored is not null && !Includes(stored.Mode, stored.VolumeId)) { excluded++; continue; }
            var target = Bind(VolumePaths.Join(destination, relative));
            var state = inspectFiles ? FileLocations.Resolve(target, snapshot, Directory.Exists).State : LocationState.Missing;
            if (state is not (LocationState.Available or LocationState.Missing))
                conflicts.Add($"{target.Path} could not be safely validated ({state}).");
            if (!roots.Any(r => r.Kind == item.Kind && r.Token == item.Token && r.ScopePath == item.ScopePath))
                roots.Add(new(item.Kind, item.Token, oldPath, target, stored?.Revision, state, item.ScopePath));
        }
        return new(request, files, roots, conflicts, excluded);
    }

    public Task CommitAsync(RecoveryPreview preview, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            if (!preview.CanApply) throw new InvalidOperationException("Resolve the preview conflicts before applying.");
            using var conn = Database.Open(_db);
            await DatabaseWriteLane.RunAsync(conn, async token =>
            {
                var fresh = await PreviewAsync(preview.Request, token);
                if (!fresh.CanApply || !fresh.Files.SequenceEqual(preview.Files) || !fresh.Roots.SequenceEqual(preview.Roots)
                    || fresh.Excluded != preview.Excluded)
                    throw new InvalidOperationException("The disk, files or configured folders changed after preview. Preview again before applying.");
                var snapshot = await _volumes.CaptureAsync(new[] { preview.Request.NewRoot }, token);
                foreach (var binding in fresh.Files.Select(f => f.Target).Concat(fresh.Roots.Select(r => r.Target)))
                {
                    var current = FileLocations.Project(binding, snapshot);
                    if (!current.Available || current.Path != binding.Path)
                        throw new IOException("The destination disk changed during validation. Preview again.");
                }
                var configured = _configured();
                using var tx = conn.BeginTransaction();
                var stored = Build(conn, preview.Request, snapshot, configured, token, tx, inspectFiles: false);
                if (!stored.CanApply || stored.Excluded != fresh.Excluded
                    || !stored.Files.SequenceEqual(fresh.Files.Select(f => f with { State = LocationState.Missing, Fingerprint = null }))
                    || !stored.Roots.SequenceEqual(fresh.Roots.Select(r => r with { State = LocationState.Missing })))
                    throw new InvalidOperationException("The catalogue or configured roots changed during validation. Preview again.");
                foreach (var file in fresh.Files)
                {
                    token.ThrowIfCancellationRequested();
                    FileLocations.WriteFile(conn, tx, file.Id, file.Revision, file.Target, file.State == LocationState.Available);
                }
                foreach (var root in fresh.Roots)
                    FileLocations.WriteRoot(conn, tx, root.Kind, root.Token, root.Target, root.Revision, root.ScopePath);
                token.ThrowIfCancellationRequested();
                tx.Commit();
            }, ct);
        }, ct);
}
