using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class DiskRelocationTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urdb-relocate-" + Guid.NewGuid().ToString("N"));
    private string Original => Path.Combine(_root, "Original");
    private string Current => Path.Combine(_root, "Renamed 1");
    private string Db => Path.Combine(_root, "catalogue.db");

    public DiskRelocationTests() => Directory.CreateDirectory(Original);

    [Fact]
    public async Task A_remounted_disk_keeps_its_playable_file_and_manual_assignment()
    {
        using var conn = Database.Open(Db);
        var old = Path.Combine(Original, "Known (2000).mkv");
        File.WriteAllText(old, "synthetic video");
        var volumes = new TestVolumes(Original);
        await volumes.ConfirmAsync(conn, Original);
        await new ScanService(volumes).ScanAsync(conn, new[] { Original });
        var id = conn.QuerySingle<long>("SELECT movie_id FROM files");
        PlayTargetResolver.LinkFile(conn, id, old, volumes: volumes.Snapshot);
        var fileId = conn.QuerySingle<long>("SELECT id FROM files");
        Remember(conn, Original);

        Directory.Move(Original, Current);
        volumes.Snapshot = new(new[] { TestVolumes.Mount(Current, "TEST-A") }, true);
        Remember(conn, Current);

        Assert.Equal(Path.Combine(Current, Path.GetFileName(old)),
            PlayTargetResolver.Resolve(conn, id, "Known", 2000, volumes: volumes.Snapshot).FilePath);
        Assert.Equal(fileId, conn.QuerySingle<long>("SELECT id FROM files"));
        Assert.Equal(1, conn.QuerySingle<int>("SELECT linked_by_hand FROM files"));
    }

    [Fact]
    public async Task A_saved_scan_folder_follows_the_remounted_disk()
    {
        using var conn = Database.Open(Db);
        File.WriteAllText(Path.Combine(Original, "Known (2000).mkv"), "synthetic video");
        var volumes = new TestVolumes(Original);
        await volumes.ConfirmAsync(conn, Original);
        await new ScanService(volumes).ScanAsync(conn, new[] { Original });
        Remember(conn, Original);
        Directory.Move(Original, Current);
        volumes.Snapshot = new(new[] { TestVolumes.Mount(Current, "TEST-A") }, true);
        Remember(conn, Current);

        var scan = await new ScanService(volumes).ScanAsync(conn, new[] { Original });

        Assert.Equal(1, scan.FilesSeen);
        Assert.Empty(scan.SkippedRoots);
        Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public async Task A_different_disk_reusing_the_old_root_is_not_the_linked_file()
    {
        using var conn = Database.Open(Db);
        var old = Path.Combine(Original, "Known (2000).mkv");
        File.WriteAllText(old, "synthetic video");
        var volumes = new TestVolumes(Original);
        await volumes.ConfirmAsync(conn, Original);
        await new ScanService(volumes).ScanAsync(conn, new[] { Original });
        var id = conn.QuerySingle<long>("SELECT movie_id FROM files");
        Remember(conn, Original);
        Directory.Move(Original, Current);
        Remember(conn, Current);
        Directory.CreateDirectory(Original);
        File.WriteAllText(old, "wrong disk file");
        volumes.Snapshot = new(new[] { TestVolumes.Mount(Current, "TEST-A"), TestVolumes.Mount(Original, "TEST-B") }, true);

        Assert.NotEqual(old, PlayTargetResolver.Resolve(conn, id, "Known", 2000, volumes: volumes.Snapshot).FilePath);
    }

    [Fact]
    public async Task A_volume_rename_cannot_reinterpret_an_existing_episodes_series_folder()
    {
        var season = Path.Combine(Original, "Season 01");
        Directory.CreateDirectory(season);
        File.WriteAllText(Path.Combine(season, "01 - Chapter.mkv"), "synthetic episode");
        var volumes = new TestVolumes(Original);
        using var conn = Database.Open(Db);
        await volumes.ConfirmAsync(conn, Original);
        await new ScanService(volumes).ScanAsync(conn, new[] { Original });
        var episode = conn.QuerySingle<long>("SELECT episode_id FROM files");
        var series = conn.QuerySingle<long>("SELECT series_id FROM episodes");
        Directory.Move(Original, Current);
        volumes.Snapshot = new(new[] { TestVolumes.Mount(Current, "TEST-A") }, true);

        await new ScanService(volumes).ScanAsync(conn, new[] { Original });

        Assert.Equal(episode, conn.QuerySingle<long>("SELECT episode_id FROM files"));
        Assert.Equal(series, conn.QuerySingle<long>("SELECT series_id FROM episodes WHERE id=@episode", new { episode }));
        Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM series"));
    }

    private static void Remember(SqliteConnection conn, string root) =>
        KnownDriveStore.Remember(conn, new[] { new KnownDrive("serial:TEST-A", "Films", root, 1000, DateTime.UtcNow) });

    public void Dispose()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
