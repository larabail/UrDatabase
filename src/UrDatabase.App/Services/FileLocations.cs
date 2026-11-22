using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services;

public enum LocationState
{
    Available, Missing, Disconnected, WrongDisk, Ambiguous, IdentityUnavailable,
    TimedOut, NeedsConfirmation, Invalid, Conflict
}

public sealed class LocationChangedException(string message) : IOException(message);

public sealed record LocationResult(LocationState State, string? Path, string Message, string? Identity = null)
{
    public bool Available => State == LocationState.Available;
}

public sealed record LocationBinding(string Mode, string Path, string? VolumeId = null,
    string? RelativePath = null, string? RelativeKey = null, string? PathStyle = null)
{
    public static LocationBinding Legacy(string path) => new("legacy", path);
}

public sealed class LocatedFile
{
    public long Id { get; set; }
    public long? MovieId { get; set; }
    public long? EpisodeId { get; set; }
    public bool LinkedByHand { get; set; }
    public string FilePath { get; set; } = "";
    public string Mode { get; set; } = "legacy";
    public string? VolumeId { get; set; }
    public string? RelativePath { get; set; }
    public string? RelativeKey { get; set; }
    public string? PathStyle { get; set; }
    public long Revision { get; set; }
    public long? SizeBytes { get; set; }
    public string? MissingSince { get; set; }
    public string? UpdatedAt { get; set; }
    public LocationBinding Binding => new(Mode, FilePath, VolumeId, RelativePath, RelativeKey, PathStyle);
}

public sealed class LibraryRoot
{
    public long Id { get; set; }
    public string Kind { get; set; } = "watch";
    public string Token { get; set; } = "";
    public string ScopePath { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Mode { get; set; } = "legacy";
    public string? VolumeId { get; set; }
    public string? RelativePath { get; set; }
    public string? RelativeKey { get; set; }
    public string? PathStyle { get; set; }
    public long Revision { get; set; }
    public LocationBinding Binding => new(Mode, FilePath, VolumeId, RelativePath, RelativeKey, PathStyle);
}

public static class FileLocations
{
    internal const string FileColumns = @"id AS Id, movie_id AS MovieId, episode_id AS EpisodeId,
linked_by_hand AS LinkedByHand, file_path AS FilePath, location_mode AS Mode, volume_id AS VolumeId,
relative_path AS RelativePath, relative_key AS RelativeKey, path_style AS PathStyle,
location_revision AS Revision, size_bytes AS SizeBytes, missing_since AS MissingSince, updated_at AS UpdatedAt";
    internal const string RootColumns = @"id AS Id, kind AS Kind, config_token AS Token, scope_path AS ScopePath,
file_path AS FilePath, location_mode AS Mode, volume_id AS VolumeId, relative_path AS RelativePath,
relative_key AS RelativeKey, path_style AS PathStyle, location_revision AS Revision";

    public static IReadOnlyList<LocatedFile> Files(SqliteConnection conn, SqliteTransaction? tx = null) =>
        conn.Query<LocatedFile>($"SELECT {FileColumns} FROM files ORDER BY id", transaction: tx).ToList();

    public static LocatedFile? File(SqliteConnection conn, long id) =>
        conn.QuerySingleOrDefault<LocatedFile>($"SELECT {FileColumns} FROM files WHERE id=@id", new { id });

    public static LibraryRoot? Root(SqliteConnection conn, string kind, string token, string scopePath = "") =>
        conn.QuerySingleOrDefault<LibraryRoot>($"SELECT {RootColumns} FROM library_roots WHERE kind=@kind AND config_token=@token AND scope_path=@scopePath",
            new { kind, token, scopePath });

    public static IReadOnlyList<LibraryRoot> Roots(SqliteConnection conn) =>
        conn.Query<LibraryRoot>($"SELECT {RootColumns} FROM library_roots ORDER BY id").ToList();

    public static string SavedRoot(LocationBinding binding)
    {
        var path = VolumePaths.Absolute(binding.Path);
        if (binding.Mode != "volume" || binding.RelativePath is null)
            return DriveVolumes.VolumeOf(path) ?? path;
        var root = path;
        var separator = VolumePaths.IsWindows(root) ? '\\' : '/';
        foreach (var segment in binding.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = root.LastIndexOf(separator);
            if (cut < 0) throw new ArgumentException("The saved volume path is inconsistent.");
            root = cut == 0 ? "/" : root[..(cut == 2 && root[1] == ':' ? cut + 1 : cut)];
        }
        if (!string.Equals(VolumePaths.Join(root, binding.RelativePath), path, VolumePaths.Comparison(root)))
            throw new ArgumentException("The saved volume path is inconsistent.");
        return root;
    }

    public static LocatedFile? Match(SqliteConnection conn, LocationBinding binding, SqliteTransaction? tx = null) =>
        conn.QuerySingleOrDefault<LocatedFile>($"SELECT {FileColumns} FROM files WHERE " +
            (binding.Mode == "volume" ? "location_mode='volume' AND volume_id=@volume AND relative_key=@key"
                : "location_mode <> 'volume' AND file_path=@path"), Parameters(binding), tx);

    public static async Task<LocationResult> ResolveFileAsync(string dbPath, long fileId,
        IVolumeSnapshotSource? volumes = null, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            using var conn = Database.Open(dbPath);
            var file = File(conn, fileId);
            if (file is null) return new LocationResult(LocationState.Missing, null, "The catalogue file no longer exists.");
            var snapshot = await (volumes ?? VolumeSnapshotSource.Shared).CaptureAsync(new[] { file.FilePath }, ct);
            if (file.MissingSince is not null)
                return new LocationResult(LocationState.Missing, null, "The last scan marked this file missing. Locate it or scan its verified folder.");
            return Resolve(file.Binding, snapshot) with { Identity = $"{file.Id}:{file.Revision}:{file.VolumeId}:{file.RelativeKey}" };
        }, ct).WaitAsync(DriveMonitor.DefaultTimeout + TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
    }

    public static Task<LocationResult> ResolveRootAsync(string dbPath, ConfiguredRoot root,
        IVolumeSnapshotSource? volumes = null, bool forWrite = false, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            using var conn = Database.Open(dbPath);
            var stored = Root(conn, root.Kind, root.Token);
            var snapshot = await (volumes ?? VolumeSnapshotSource.Shared).CaptureAsync(new[] { root.Path }, ct);
            var binding = stored?.Binding ?? LocationBinding.Legacy(root.Path);
            return Resolve(binding, snapshot, forWrite ? _ => true : Directory.Exists)
                with { Identity = $"{stored?.Id}:{stored?.Revision}:{stored?.VolumeId}:{stored?.RelativeKey}" };
        }, ct).WaitAsync(DriveMonitor.DefaultTimeout + TimeSpan.FromSeconds(2), ct);

    public static LocationResult Resolve(LocationBinding binding, VolumeSnapshot snapshot,
        Func<string, bool>? exists = null, Action<string, string>? validateLinks = null)
    {
        var projected = Project(binding, snapshot);
        if (!projected.Available) return projected;
        try
        {
            var result = Present(projected.Path!, exists,
                binding.Mode == "volume" ? snapshot.WithIdentity(binding.VolumeId!)[0].Root : null, validateLinks);
            return binding.Mode == "path" && result.Available
                ? result with { Message = "Path-only location; automatic disk tracking is unavailable." } : result;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(LocationState.Invalid, null, ex.Message);
        }
    }

    // A display/index projection, not permission to open a file. Resolve also validates
    // filesystem containment and existence before an operation consumes this location.
    public static LocationResult Project(LocationBinding binding, VolumeSnapshot snapshot)
    {
        try
        {
            if (binding.Mode == "legacy")
                return new(LocationState.NeedsConfirmation, null, "Confirm this disk or locate a known file to recover its links.");
            if (binding.Mode == "path")
                return new(LocationState.Available, VolumePaths.Absolute(binding.Path), "Path-only location");
            if (binding.Mode != "volume" || binding.VolumeId is null || binding.RelativePath is null
                || binding.PathStyle is not ("windows" or "posix" or "posix-insensitive")
                || binding.RelativeKey != VolumePaths.RelativeKey(binding.RelativePath, binding.PathStyle))
                return new(LocationState.Invalid, null, "The saved location is incomplete. Relocate this disk/folder.");
            if (!snapshot.Complete)
                return new(LocationState.TimedOut, null, "The volume list could not be verified. Check again.");
            var candidates = snapshot.WithIdentity(binding.VolumeId);
            if (candidates.Count > 1)
                return new(LocationState.Ambiguous, null, "More than one mounted volume has this identity. Disconnect the duplicate and check again.");
            if (candidates.Count == 0)
            {
                var previous = snapshot.Mounts.FirstOrDefault(m => string.Equals(m.Root,
                    SavedRoot(binding), VolumePaths.Comparison(binding.Path)));
                if (previous?.Probe.State == DriveState.NotResponding)
                    return new(LocationState.TimedOut, null, "The disk did not respond. Check again.");
                if (previous is { SupportsIdentity: true, Id: null } && previous.Probe.State == DriveState.Connected)
                    return new(LocationState.IdentityUnavailable, null, "The disk answered without its identity. Check again; the old path is not trusted.");
                return previous?.Id is not null
                    ? new(LocationState.WrongDisk, null, "A different disk occupies the old location. Connect the original disk or relocate its links.")
                    : new(LocationState.Disconnected, null, "The linked disk is not connected. Connect it or relocate its links.");
            }
            var mount = candidates[0];
            var path = VolumePaths.Join(mount.Root, binding.RelativePath);
            if (snapshot.Containing(path)?.Id != mount.Id)
                return new(LocationState.Invalid, null, "The path crosses into a different mounted volume.");
            return new(LocationState.Available, path, "");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new(LocationState.Invalid, null, ex.Message);
        }
    }

    private static LocationResult Present(string path, Func<string, bool>? exists, string? root = null,
        Action<string, string>? validateLinks = null)
    {
        if (root is not null) (validateLinks ?? VolumePaths.CheckLinks)(root, path);
        return (exists ?? System.IO.File.Exists)(path)
            ? new(LocationState.Available, path, "")
            : new(LocationState.Missing, null, "The file or folder is absent at its verified location.");
    }

    public static LocationBinding Capture(string path, VolumeSnapshot snapshot, bool allowPathOnly = false)
    {
        var binding = CapturePath(path, snapshot, allowPathOnly);
        if (binding.Mode == "volume")
            VolumePaths.CheckLinks(snapshot.WithIdentity(binding.VolumeId!)[0].Root, binding.Path);
        return binding;
    }

    internal static LocationBinding CapturePath(string path, VolumeSnapshot snapshot, bool allowPathOnly = false)
    {
        path = VolumePaths.Absolute(path);
        if (!snapshot.Complete) throw new IOException("The volume list could not be verified. Check again.");
        var mount = snapshot.Containing(path) ?? throw new IOException("The selected location has no verified mounted volume.");
        if (mount.Probe.State != DriveState.Connected) throw new IOException("The selected disk is not responding.");
        if (!mount.SupportsIdentity || mount.Id is null)
        {
            if (!allowPathOnly) throw new IOException("This location supports path-only access. Confirm that limitation before continuing.");
            return new("path", path);
        }
        if (snapshot.WithIdentity(mount.Id).Count != 1)
            throw new IOException("More than one mounted volume has this identity. Disconnect the duplicate and check again.");
        if (!VolumePaths.TryRelative(mount.Root, path, out var relative, mount.PathStyle)) throw new IOException("The selected path is outside the disk.");
        path = VolumePaths.Join(mount.Root, relative);
        var style = mount.PathStyle;
        return new("volume", path, mount.Id, relative, VolumePaths.RelativeKey(relative, style), style);
    }

    internal static object Parameters(LocationBinding binding) => new
    {
        mode = binding.Mode, path = binding.Path, volume = binding.VolumeId,
        relative = binding.RelativePath, key = binding.RelativeKey, style = binding.PathStyle
    };

    internal static void WriteFile(SqliteConnection conn, SqliteTransaction tx, long id, long revision,
        LocationBinding binding, bool found = false)
    {
        var args = new DynamicParameters(Parameters(binding));
        args.Add("id", id);
        args.Add("revision", revision);
        args.Add("found", found);
        if (conn.Execute(@"UPDATE files SET file_path=@path, location_mode=@mode, volume_id=@volume,
relative_path=@relative, relative_key=@key, path_style=@style, location_revision=location_revision+1,
missing_since=CASE WHEN @found THEN NULL ELSE missing_since END
WHERE id=@id AND location_revision=@revision", args, tx) != 1)
            throw new InvalidOperationException("The file location changed. Reopen the preview.");
    }

    internal static void WriteRoot(SqliteConnection conn, SqliteTransaction tx, string kind, string token,
        LocationBinding binding, long? revision, string scopePath = "")
    {
        var args = new DynamicParameters(Parameters(binding));
        args.Add("kind", kind);
        args.Add("token", token);
        args.Add("scope", scopePath);
        args.Add("revision", revision);
        if (revision is null)
        {
            conn.Execute(@"INSERT INTO library_roots (kind, config_token, scope_path, file_path, location_mode,
volume_id, relative_path, relative_key, path_style, location_revision)
VALUES (@kind,@token,@scope,@path,@mode,@volume,@relative,@key,@style,1)", args, tx);
        }
        else if (conn.Execute(@"UPDATE library_roots SET file_path=@path, location_mode=@mode,
volume_id=@volume, relative_path=@relative, relative_key=@key, path_style=@style,
location_revision=location_revision+1 WHERE kind=@kind AND config_token=@token AND scope_path=@scope AND location_revision=@revision",
                     args, tx) != 1)
            throw new InvalidOperationException("The configured folder changed. Reopen the preview.");
    }

    public static async Task ConfirmNewRootAsync(SqliteConnection conn, string kind, string token, string path,
        VolumeSnapshot snapshot, bool allowPathOnly = false, CancellationToken ct = default)
    {
        var binding = Capture(path, snapshot, allowPathOnly);
        if (kind != "download" && !Directory.Exists(path)) throw new IOException("The selected folder is no longer there.");
        await DatabaseWriteLane.RunAsync(conn, cancellation =>
        {
            using var tx = conn.BeginTransaction();
            if (Files(conn, tx).Any(f => f.Mode == "legacy" && VolumePaths.TryRelative(path, f.FilePath, out _)))
                throw new InvalidOperationException("This folder contains legacy links. Preview and confirm their recovery first.");
            cancellation.ThrowIfCancellationRequested();
            WriteRoot(conn, tx, kind, token, binding, Root(conn, kind, token)?.Revision);
            cancellation.ThrowIfCancellationRequested();
            tx.Commit();
            return Task.CompletedTask;
        }, ct);
    }
}
