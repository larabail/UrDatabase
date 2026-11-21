using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;

namespace UrDatabase.Tests;

// Existing catalogue tests construct their own files. Give those fixtures explicit synthetic
// identities rather than letting any service enumerate the host's disks.
internal static class FixtureMedia
{
    public static TestVolumes Volumes(string dbPath)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(dbPath);
        if (!VolumePaths.TryRelative(temp, full, out var relative) || !relative.Contains('/'))
            throw new InvalidOperationException("A media fixture must use its own directory below the temporary root.");
        return new TestVolumes(Path.Combine(temp, relative.Split('/')[0]));
    }

    public static TestVolumes BindFixtureFiles(SqliteConnection conn)
    {
        var volumes = Volumes(new SqliteConnectionStringBuilder(conn.ConnectionString).DataSource);
        using var tx = conn.BeginTransaction();
        foreach (var file in FileLocations.Files(conn, tx).Where(f => f.Mode == "legacy"))
        {
            if (volumes.Snapshot.Containing(file.FilePath) is null) continue;
            FileLocations.WriteFile(conn, tx, file.Id, file.Revision,
                FileLocations.Capture(file.FilePath, volumes.Snapshot), found: false);
        }
        tx.Commit();
        return volumes;
    }

    public static PlayTarget Resolve(SqliteConnection conn, long id, string? title, int? year,
        Func<string, bool>? fileExists = null) =>
        PlayTargetResolver.Resolve(conn, id, title, year, fileExists, BindFixtureFiles(conn).Snapshot);

    public static void LinkFile(SqliteConnection conn, long id, string path, Func<string, bool>? fileExists = null) =>
        PlayTargetResolver.LinkFile(conn, id, path, fileExists,
            Volumes(new SqliteConnectionStringBuilder(conn.ConnectionString).DataSource).Snapshot);

    public static Task<long> RecordSingleFileAsync(SqliteConnection conn, string path) =>
        ScanService.RecordSingleFileAsync(conn, path, BindFixtureFiles(conn));

    public static Task<ScanFileIndex> IndexAsync(SqliteConnection conn) =>
        ScanFileIndex.LoadAsync(conn, BindFixtureFiles(conn).Snapshot);

    public static LocalSeriesLoader Loader(string dbPath, Func<TmdbService?>? tmdb = null) =>
        new(dbPath, tmdb, Volumes(dbPath));

    public static async Task<ScanResult> ScanLibraryAsync(string dbPath, IEnumerable<string> folders,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var conn = Database.Open(dbPath);
        return await new FixtureScanner().ScanAsync(conn, folders, progress, ct);
    }
}

internal sealed class FixtureScanner
{
    public async Task<ScanResult> ScanAsync(SqliteConnection conn, IEnumerable<string> folders,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(folders);
        var selected = folders.ToArray();
        var volumes = FixtureMedia.BindFixtureFiles(conn);
        using (var tx = conn.BeginTransaction())
        {
            foreach (var folder in selected.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                if (volumes.Snapshot.Containing(folder) is null || !Directory.Exists(folder)) continue;
                if (FileLocations.Root(conn, "watch", folder) is null)
                    FileLocations.WriteRoot(conn, tx, "watch", folder, FileLocations.Capture(folder, volumes.Snapshot), null);
            }
            tx.Commit();
        }
        return await new ScanService(volumes).ScanAsync(conn, selected, progress, ct);
    }
}
