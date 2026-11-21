using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public sealed class DiskRecoveryTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urdb-recovery-" + Guid.NewGuid().ToString("N"));
    private string Old => Path.Combine(_root, "Old disk");
    private string New => Path.Combine(_root, "New disk");
    private string Db => Path.Combine(_root, "movies.db");
    private readonly TestVolumes _volumes;
    private IReadOnlyList<ConfiguredRoot> _roots;
    private DiskRecovery Recovery => new(Db, () => _roots, _volumes);

    public DiskRecoveryTests()
    {
        Directory.CreateDirectory(New);
        _volumes = new TestVolumes(New);
        _roots = new[] { new ConfiguredRoot("watch", Old, Old) };
    }

    private long Legacy(string relative, bool present = true, long? episode = null, bool missing = false)
    {
        using var conn = Database.Open(Db);
        var movie = episode is null ? conn.ExecuteScalar<long?>(
            "INSERT INTO movies (title, year) VALUES (@title, 2000) RETURNING id", new { title = relative }) : null;
        var saved = VolumePaths.Join(Old, relative);
        var current = VolumePaths.Join(New, relative);
        if (present)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            File.WriteAllText(current, "synthetic video");
        }
        return conn.ExecuteScalar<long>(@"INSERT INTO files
(movie_id, episode_id, file_path, linked_by_hand, missing_since, size_bytes)
VALUES (@movie,@episode,@saved,1,@missing,15) RETURNING id",
            new { movie, episode, saved, missing = missing ? "2025-01-01" : null });
    }

    [Fact]
    public async Task One_known_file_recovers_hundreds_of_legacy_rows_and_persists_scan_roots()
    {
        for (var i = 0; i < 201; i++) Legacy($"Library/{i}/Disc.mkv", missing: true);
        using (var conn = Database.Open(Db))
        {
            conn.Execute("INSERT INTO series (id,title) VALUES (1,'Manual show'); INSERT INTO episodes (id,series_id,season_number,episode_number) VALUES (1,1,2,7)");
            conn.Execute("INSERT INTO watchlist (item_key,added_at) VALUES ('local:1','yesterday')");
            conn.Execute("INSERT INTO film_ratings (film_key,title,rating,updated_at) VALUES ('local:1','Chosen title',4,'yesterday')");
            conn.Execute("INSERT INTO film_viewings (film_key,title,note,created_at) VALUES ('local:1','Chosen title','Keep me','yesterday')");
        }
        var episodeId = Legacy("TV/Show/episode.mkv", episode: 1, missing: true);
        var absentId = Legacy("Library/absent.mkv", present: false, missing: true);
        _roots = new[]
        {
            new ConfiguredRoot("watch", Old, Old),
            new ConfiguredRoot("watch", Path.Combine(Old, "Library"), Path.Combine(Old, "Library"))
        };
        LocatedFile anchor;
        using (var conn = Database.Open(Db)) anchor = FileLocations.Files(conn)[0];
        var request = DiskRecovery.FromFile(anchor, VolumePaths.Join(New, "Library/0/Disc.mkv"), _volumes.Snapshot, Old);
        var preview = await Recovery.PreviewAsync(request);
        Assert.True(preview.CanApply);
        Assert.Equal(203, preview.Files.Count);
        Assert.Equal(202, preview.Present);
        Assert.Equal(2, preview.Roots.Count);
        await Recovery.CommitAsync(preview);

        using var reopened = Database.Open(Db);
        Assert.Equal(203, reopened.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE location_mode='volume' AND linked_by_hand=1"));
        Assert.Equal(1, reopened.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE missing_since IS NOT NULL"));
        Assert.NotNull(FileLocations.File(reopened, absentId)!.MissingSince);
        Assert.Equal(1, FileLocations.File(reopened, episodeId)!.EpisodeId);
        Assert.Equal("Keep me", reopened.QuerySingle<string>("SELECT note FROM film_viewings"));
        Assert.Equal(4, reopened.QuerySingle<int>("SELECT rating FROM film_ratings"));
        Assert.Equal("local:1", reopened.QuerySingle<string>("SELECT item_key FROM watchlist"));
        var played = PlayTargetResolver.Resolve(reopened, anchor.MovieId!.Value, anchor.FilePath, 2000, volumes: _volumes.Snapshot);
        Assert.Equal(VolumePaths.Join(New, "Library/0/Disc.mkv"), played.FilePath);
        var episodes = await new LocalSeriesLoader(Db, volumes: _volumes).LoadAsync(1);
        Assert.Equal(VolumePaths.Join(New, "TV/Show/episode.mkv"), Assert.Single(episodes).FilePath);
        var scan = await new ScanService(_volumes).ScanAsync(reopened, _roots.Select(r => r.Token));
        Assert.Equal(0, scan.Inserted);
        Assert.Equal(202, scan.FilesSeen);
        Assert.Equal(203, reopened.ExecuteScalar<int>("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public async Task Cancelled_preview_changes_neither_bindings_nor_raw_configuration()
    {
        var id = Legacy("Movies/One.mkv");
        var configPath = Path.Combine(_root, "appsettings.json");
        var json = "{\"WatchFolders\":[\"old\"],\"TmdbApiKey\":\"fake-key\",\"Jellyfin\":{\"Password\":\"fake-password\"},\"Unknown\":{\"keep\":true}}";
        File.WriteAllText(configPath, json);
        await Recovery.PreviewAsync(new(Old, New));
        using var conn = Database.Open(Db);
        Assert.Equal("legacy", FileLocations.File(conn, id)!.Mode);
        Assert.Null(FileLocations.Root(conn, "watch", Old));
        Assert.Equal(json, File.ReadAllText(configPath));
    }

    [Fact]
    public async Task Same_path_confirmation_is_supported_without_hiding_the_card()
    {
        Legacy("Movies/One.mkv");
        Directory.Move(New, Old);
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(Old, "TEST-A") }, true);
        using (var conn = Database.Open(Db))
        {
            Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM files WHERE missing_since IS NULL"));
            Assert.Equal(LocationState.NeedsConfirmation, PlayTargetResolver.Resolve(conn, 1, "One", 2000,
                volumes: _volumes.Snapshot).Location!.State);
        }
        var preview = await Recovery.PreviewAsync(new(Old, Old));
        await Recovery.CommitAsync(preview);
        using var verify = Database.Open(Db);
        Assert.Equal(PlayTargetKind.Linked, PlayTargetResolver.Resolve(verify, 1, "One", 2000, volumes: _volumes.Snapshot).Kind);
    }

    [Fact]
    public void A_root_level_known_file_can_propose_an_unambiguous_disk_mapping()
    {
        var id = Legacy("One.mkv");
        using var conn = Database.Open(Db);
        var request = DiskRecovery.FromFile(FileLocations.File(conn, id)!, Path.Combine(New, "One.mkv"), _volumes.Snapshot, Old);
        Assert.Equal(Old, request.OldRoot);
        Assert.Equal(New, request.NewRoot);
    }

    [Fact]
    public void A_matching_basename_does_not_infer_a_changed_folder_layout()
    {
        var id = Legacy("Original layout/One.mkv");
        var different = Path.Combine(New, "Other layout", "One.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(different)!);
        File.WriteAllText(different, "synthetic video");
        using var conn = Database.Open(Db);
        Assert.Throws<ArgumentException>(() => DiskRecovery.FromFile(FileLocations.File(conn, id)!, different, _volumes.Snapshot, Old));
        Assert.Throws<ArgumentException>(() => DiskRecovery.FromFile(FileLocations.File(conn, id)!, different, _volumes.Snapshot));
    }

    [Theory]
    [InlineData("swap")]
    [InlineData("remove")]
    [InlineData("change-file")]
    [InlineData("change-link")]
    [InlineData("change-config")]
    public async Task A_stale_preview_never_applies_part_of_the_mapping(string change)
    {
        var id = Legacy("Movies/One.mkv");
        Legacy("Movies/Two.mkv");
        var preview = await Recovery.PreviewAsync(new(Old, New));
        switch (change)
        {
            case "swap": _volumes.Snapshot = new(new[] { TestVolumes.Mount(New, "TEST-B") }, true); break;
            case "remove": _volumes.Snapshot = new(Array.Empty<VolumeMount>(), true); break;
            case "change-file": File.WriteAllText(Path.Combine(New, "Movies", "Two.mkv"), "different video length"); break;
            case "change-link":
                using (var conn = Database.Open(Db)) conn.Execute("UPDATE files SET linked_by_hand=0 WHERE id=@id", new { id });
                break;
            case "change-config": _roots = Array.Empty<ConfiguredRoot>(); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => Recovery.CommitAsync(preview));
        using var verify = Database.Open(Db);
        Assert.Equal(2, verify.QuerySingle<int>("SELECT COUNT(*) FROM files WHERE location_mode='legacy'"));
        Assert.Equal(0, verify.QuerySingle<int>("SELECT COUNT(*) FROM library_roots"));
    }

    [Fact]
    public async Task A_failure_mid_commit_rolls_back_every_file_and_root()
    {
        Legacy("Movies/One.mkv");
        var second = Legacy("Movies/Two.mkv");
        var preview = await Recovery.PreviewAsync(new(Old, New));
        using var conn = Database.Open(Db);
        conn.Execute($"CREATE TRIGGER reject_binding BEFORE UPDATE OF location_mode ON files WHEN NEW.id={second} BEGIN SELECT RAISE(ABORT,'injected failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => Recovery.CommitAsync(preview));
        Assert.Equal(2, conn.QuerySingle<int>("SELECT COUNT(*) FROM files WHERE location_mode='legacy'"));
        Assert.Equal(0, conn.QuerySingle<int>("SELECT COUNT(*) FROM library_roots"));
    }

    [Fact]
    public async Task A_destination_claimed_by_another_file_is_a_visible_conflict()
    {
        Legacy("Movies/One.mkv");
        using (var conn = Database.Open(Db))
        {
            var other = conn.ExecuteScalar<long>("INSERT INTO movies (title) VALUES ('Other assignment') RETURNING id");
            PlayTargetResolver.LinkFile(conn, other, Path.Combine(New, "Movies", "One.mkv"), volumes: _volumes.Snapshot);
        }
        var preview = await Recovery.PreviewAsync(new(Old, New));
        Assert.False(preview.CanApply);
        Assert.Single(preview.Conflicts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recovery.CommitAsync(preview));
    }

    [Fact]
    public async Task Other_identified_disks_and_prefix_lookalikes_are_excluded()
    {
        var id = Legacy("Movies/One.mkv");
        Legacy("Movies Extra/Two.mkv");
        var other = Legacy("Movies/Other.mkv");
        using (var conn = Database.Open(Db))
        {
            using var tx = conn.BeginTransaction();
            FileLocations.WriteFile(conn, tx, other, 0,
                new("volume", Path.Combine(Old, "Movies", "Other.mkv"), "serial:OTHER", "Movies/Other.mkv", "Movies/Other.mkv", "posix"));
            tx.Commit();
        }
        var preview = await Recovery.PreviewAsync(new(Path.Combine(Old, "Movies"), Path.Combine(New, "Movies")));
        Assert.Equal(id, Assert.Single(preview.Files).Id);
        Assert.Equal(1, preview.Excluded);
        await Recovery.CommitAsync(preview);
        using var verify = Database.Open(Db);
        Assert.Equal("serial:OTHER", FileLocations.File(verify, other)!.VolumeId);
    }

    [Fact]
    public async Task An_old_root_reused_by_another_disk_does_not_change_the_recovery_choice()
    {
        Legacy("Movies/One.mkv");
        Directory.CreateDirectory(Path.Combine(Old, "Movies"));
        File.WriteAllText(Path.Combine(Old, "Movies", "One.mkv"), "wrong disk copy");
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(Old, "WRONG"), TestVolumes.Mount(New, "TEST-A") }, true);
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        using var verify = Database.Open(Db);
        Assert.Equal(Path.Combine(New, "Movies", "One.mkv"),
            PlayTargetResolver.Resolve(verify, 1, "One", 2000, volumes: _volumes.Snapshot).FilePath);
    }

    [Fact]
    public async Task Repeated_migration_preserves_bindings_and_allows_distinct_disks_at_one_historical_path()
    {
        Legacy("One.mkv");
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        for (var i = 0; i < 3; i++)
        {
            using var conn = Database.Open(Db);
            Assert.Equal("serial:TEST-A", conn.QuerySingle<string>("SELECT volume_id FROM files"));
            Assert.Equal(1, conn.QuerySingle<long>("SELECT id FROM files"));
        }
        using var verify = Database.Open(Db);
        verify.Execute(@"INSERT INTO files (file_path,location_mode,volume_id,relative_path,relative_key,path_style)
SELECT file_path,'volume','serial:OTHER',relative_path,relative_key,path_style FROM files");
        Assert.Equal(2, verify.QuerySingle<int>("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public void An_old_schema_gains_unverified_bindings_without_rebuilding_rows()
    {
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db }.ToString()))
        {
            conn.Open();
            conn.Execute(@"CREATE TABLE movies (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, year INTEGER, genres TEXT, poster_path TEXT);
CREATE TABLE files (id INTEGER PRIMARY KEY AUTOINCREMENT, movie_id INTEGER, file_path TEXT NOT NULL,
size_bytes INTEGER, created_at TEXT, updated_at TEXT, linked_by_hand INTEGER NOT NULL DEFAULT 0);
CREATE UNIQUE INDEX ux_files_path ON files(file_path);
INSERT INTO movies (id,title) VALUES (17,'Manual title');
INSERT INTO files (id,movie_id,file_path,linked_by_hand) VALUES (42,17,@path,1);",
                new { path = Path.Combine(Old, "One.mkv") });
        }
        for (var i = 0; i < 3; i++)
        {
            using var conn = Database.Open(Db);
            var file = Assert.Single(FileLocations.Files(conn));
            Assert.Equal(42, file.Id);
            Assert.Equal(17, file.MovieId);
            Assert.True(file.LinkedByHand);
            Assert.Equal("legacy", file.Mode);
            Assert.Null(file.VolumeId);
            Assert.Null(file.MissingSince);
        }
    }

    [Fact]
    public async Task Removing_and_readding_a_config_token_does_not_adopt_a_new_occupant()
    {
        Legacy("Movies/One.mkv");
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        using var conn = Database.Open(Db);
        var without = await new ScanService(_volumes).ScanAsync(conn, Array.Empty<string>());
        Assert.Equal(0, without.FilesSeen);
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(New, "NEW-OCCUPANT") }, true);
        var readded = await new ScanService(_volumes).ScanAsync(conn, new[] { Old });
        Assert.Equal(0, readded.FilesSeen);
        Assert.Equal(new[] { Old }, readded.SkippedRoots);
        Assert.Equal("serial:TEST-A", FileLocations.Root(conn, "watch", Old)!.VolumeId);
        Assert.Null(conn.QuerySingle<string?>("SELECT missing_since FROM files"));
    }

    [Fact]
    public async Task Drive_counts_follow_the_bound_identity_not_the_old_root()
    {
        Legacy("One.mkv");
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        using var conn = Database.Open(Db);
        var moved = Path.Combine(_root, "Mounted 1");
        Directory.Move(New, moved);
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(moved, "TEST-A"), TestVolumes.Mount(New, "OTHER") }, true);
        var reports = await new DriveMonitor(volumes: _volumes).CheckDrivesAsync(new[] { Old }, Db);
        var disk = Assert.Single(reports, r => r.Id == "serial:TEST-A");
        Assert.Equal(moved, disk.Root);
        Assert.Equal(1, disk.FilmCount);
        Assert.Equal(new[] { moved }, disk.WatchFolders);
        Assert.Equal(0, Assert.Single(reports, r => r.Id == "serial:OTHER").FilmCount);
    }

    [Fact]
    public async Task Replacing_a_bound_disk_includes_files_with_different_historical_mount_roots()
    {
        Legacy("Movies/One.mkv");
        var second = Legacy("Movies/Two.mkv");
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        var earlier = Path.Combine(_root, "Earlier mount");
        using (var conn = Database.Open(Db))
            conn.Execute("UPDATE files SET file_path=@path WHERE id=@second",
                new { path = Path.Combine(earlier, "Movies", "Two.mkv"), second });
        var replacement = Path.Combine(_root, "Replacement");
        Directory.Move(New, replacement);
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(replacement, "REPLACEMENT") }, true);
        var preview = await Recovery.PreviewAsync(new(New, replacement, "serial:TEST-A"));
        Assert.Equal(2, preview.Files.Count);
        await Recovery.CommitAsync(preview);
        using var verify = Database.Open(Db);
        Assert.Equal(2, verify.QuerySingle<int>("SELECT COUNT(*) FROM files WHERE volume_id='serial:REPLACEMENT'"));
    }

    private async Task<(string Parent, string Child, string Destination, long ChildId)> PrepareSubtreeAsync()
    {
        var parent = Path.Combine(Old, "Library");
        var child = Path.Combine(parent, "Child");
        var destination = Path.Combine(New, "Moved child");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(parent, "Sibling (2000).mkv"), "sibling");
        File.WriteAllText(Path.Combine(child, "Child (2001).mkv"), "child");
        _roots = new[] { new ConfiguredRoot("watch", parent, parent) };
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(Old, "A"), TestVolumes.Mount(New, "B") }, true);
        using var conn = Database.Open(Db);
        await FileLocations.ConfirmNewRootAsync(conn, "watch", parent, parent, _volumes.Snapshot);
        await new ScanService(_volumes).ScanAsync(conn, new[] { parent });
        var id = conn.ExecuteScalar<long>("SELECT id FROM files WHERE file_path=@path",
            new { path = Path.Combine(child, "Child (2001).mkv") });
        conn.Execute("UPDATE files SET linked_by_hand=1 WHERE id=@id", new { id });
        Directory.Move(child, destination);
        return (parent, child, destination, id);
    }

    [Fact]
    public async Task A_child_scope_preserves_siblings_survives_restart_and_follows_the_parent_config_token()
    {
        var (parent, child, destination, id) = await PrepareSubtreeAsync();
        var request = new RelocationRequest(child, destination, "serial:A");
        var preview = await Recovery.PreviewAsync(request);
        Assert.Equal(child, Assert.Single(preview.Roots).ScopePath);
        await Recovery.CommitAsync(preview);
        await Recovery.CommitAsync(await Recovery.PreviewAsync(request));

        using (var restarted = Database.Open(Db))
        {
            Assert.Equal(2, FileLocations.Roots(restarted).Count);
            Assert.Equal("serial:A", FileLocations.Root(restarted, "watch", parent)!.VolumeId);
            Assert.Equal("serial:B", FileLocations.File(restarted, id)!.VolumeId);
            var scan = await new ScanService(_volumes).ScanAsync(restarted, new[] { parent });
            Assert.Equal(2, scan.FilesSeen);
            Assert.Equal(0, scan.Inserted);
            Assert.Equal(0, scan.Missing);
            Assert.True(FileLocations.File(restarted, id)!.LinkedByHand);
        }

        var again = Path.Combine(New, "Child again");
        Directory.Move(destination, again);
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(destination, again, "serial:B")));
        using var conn = Database.Open(Db);
        Assert.Equal(2, FileLocations.Roots(conn).Count);
        Assert.Equal(again, FileLocations.Root(conn, "watch", parent, child)!.FilePath);
        await FileLocations.ConfirmNewRootAsync(conn, "watch", again, again, _volumes.Snapshot);
        var overlapping = await new ScanService(_volumes).ScanAsync(conn, new[] { parent, again });
        Assert.Equal(2, overlapping.FilesSeen);
        var removed = await new ScanService(_volumes).ScanAsync(conn, Array.Empty<string>());
        Assert.Equal(0, removed.FilesSeen);
        Assert.Equal(0, removed.Missing);
        var onlyIndependent = await new ScanService(_volumes).ScanAsync(conn, new[] { again });
        Assert.Equal(1, onlyIndependent.FilesSeen);
    }

    [Fact]
    public async Task Relocating_the_parent_does_not_rebase_a_child_already_on_another_disk()
    {
        var (parent, child, destination, id) = await PrepareSubtreeAsync();
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(child, destination, "serial:A")));
        var replacement = Path.Combine(_root, "Replacement disk");
        var parentDestination = Path.Combine(replacement, "Library");
        Directory.CreateDirectory(replacement);
        Directory.Move(parent, parentDestination);
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(New, "B"), TestVolumes.Mount(replacement, "C") }, true);
        var preview = await Recovery.PreviewAsync(new(parent, parentDestination, "serial:A"));
        Assert.Single(preview.Files);
        await Recovery.CommitAsync(preview);
        using var conn = Database.Open(Db);
        Assert.Equal("serial:B", FileLocations.File(conn, id)!.VolumeId);
        Assert.Equal(destination, FileLocations.Root(conn, "watch", parent, child)!.FilePath);
        Assert.Equal("serial:C", FileLocations.Root(conn, "watch", parent)!.VolumeId);
        var scan = await new ScanService(_volumes).ScanAsync(conn, new[] { parent });
        Assert.Equal(2, scan.FilesSeen);
        Assert.Equal(0, scan.Inserted);
    }

    [Fact]
    public async Task A_scope_write_failure_rolls_back_the_relocated_files()
    {
        var (parent, child, destination, id) = await PrepareSubtreeAsync();
        var preview = await Recovery.PreviewAsync(new(child, destination, "serial:A"));
        using var conn = Database.Open(Db);
        conn.Execute(@"CREATE TRIGGER reject_scope BEFORE INSERT ON library_roots
WHEN NEW.scope_path<>'' BEGIN SELECT RAISE(ABORT,'scope failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => Recovery.CommitAsync(preview));
        Assert.Equal("serial:A", FileLocations.File(conn, id)!.VolumeId);
        Assert.Null(FileLocations.Root(conn, "watch", parent, child));
    }

    [Fact]
    public async Task A_scope_created_after_preview_requires_another_confirmation()
    {
        var (parent, child, destination, id) = await PrepareSubtreeAsync();
        var preview = await Recovery.PreviewAsync(new(child, destination, "serial:A"));
        using var conn = Database.Open(Db);
        using (var tx = conn.BeginTransaction())
        {
            FileLocations.WriteRoot(conn, tx, "watch", parent, FileLocations.Capture(destination, _volumes.Snapshot), null, child);
            tx.Commit();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recovery.CommitAsync(preview));
        Assert.Equal("serial:A", FileLocations.File(conn, id)!.VolumeId);
        Assert.Equal(1, FileLocations.Root(conn, "watch", parent, child)!.Revision);
    }

    [Fact]
    public async Task Recovered_files_support_filtered_bulk_grouping_without_losing_problem_reports()
    {
        var problemFile = Legacy("Films/Problem.mkv");
        var keepFile = Legacy("Films/Keep.mkv");
        var first = Legacy("Group/Source.S01E01.mkv");
        var second = Legacy("Group/Source.S01E02.mkv");
        long problemMovie;
        using (var conn = Database.Open(Db))
        {
            problemMovie = FileLocations.File(conn, problemFile)!.MovieId!.Value;
            conn.Execute("UPDATE movies SET title='Problem film', genres='Drama' WHERE id=@problemMovie", new { problemMovie });
            FilmProblemStore.Save(conn, new($"local:{problemMovie}", new[] { FilmProblemKind.Audio }, "Preserve this report"));
        }
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        using var reopened = Database.Open(Db);
        var files = LocalSeriesGrouping.Files(reopened, _volumes.Snapshot);
        Assert.All(files, f => Assert.StartsWith(New, f.FilePath));
        using var selection = new SeriesGroupingSelection(files);
        files.Single(f => f.Id == keepFile).IsSelected = true;
        selection.Filter("Group");
        Assert.Equal(2, selection.Results.Count);
        selection.SelectAllResults(true);
        Assert.Equal(3, selection.SelectedCount);
        selection.Results.Single(f => f.Id == first).EpisodeText = "42";
        selection.SelectAllResults(false);
        Assert.True(files.Single(f => f.Id == keepFile).IsSelected);
        Assert.Equal("42", files.Single(f => f.Id == first).EpisodeText);
        files.Single(f => f.Id == keepFile).IsSelected = false;
        selection.SelectAllResults(true);
        var series = LocalSeriesGrouping.Assign(reopened, null, "Grouped after recovery",
            files.Where(f => f.IsSelected).Select(f => f.Assignment()).ToArray());
        var episodes = await new LocalSeriesLoader(Db, volumes: _volumes).LoadAsync(series);
        Assert.Equal(new long?[] { first, second }, episodes.OrderBy(e => e.FileId).Select(e => e.FileId));
        Assert.All(episodes, e => Assert.True(e.IsAvailable));
        Assert.All(FileLocations.Files(reopened), f => Assert.Equal("serial:TEST-A", f.VolumeId));

        var loader = new LibraryLoader(new MovieRepository(Db));
        var filter = new LibraryRefinement { ProblemsOnly = true };
        var shown = filter.Apply(loader.Load("problem").All);
        Assert.Equal($"local:{problemMovie}", Assert.Single(shown).Key);
        Assert.Equal("Preserve this report", shown[0].ProblemReport!.Note);
        Assert.Equal(0, LibraryGrouping.BuildGenreChips(shown, "Comedy").Single(c => c.Name == "Comedy").Count);
        Assert.Equal(4, (await new ScanService(_volumes).ScanAsync(reopened, new[] { Old })).FilesSeen);
        Assert.Equal($"local:{problemMovie}", Assert.Single(filter.Apply(loader.Load(null).All)).Key);
    }

    [Fact]
    public async Task Bulk_selection_filters_current_paths_after_another_remount()
    {
        var id = Legacy("Source.S01E01.mkv");
        await Recovery.CommitAsync(await Recovery.PreviewAsync(new(Old, New)));
        var renamed = Path.Combine(_root, "Remounted again");
        Directory.Move(New, renamed);
        _volumes.Snapshot = new(new[] { TestVolumes.Mount(renamed, "TEST-A") }, true);
        using var conn = Database.Open(Db);
        using var selection = new SeriesGroupingSelection(LocalSeriesGrouping.Files(conn, _volumes.Snapshot));
        selection.Filter("Remounted again");
        Assert.Equal(id, Assert.Single(selection.Results).Id);
        selection.SelectAllResults(true);
        Assert.True(selection.AllSelected);
        Assert.Equal(New, FileLocations.SavedRoot(FileLocations.File(conn, id)!.Binding));
        var series = LocalSeriesGrouping.Assign(conn, null, "Still the same file",
            selection.Results.Select(f => f.Assignment()).ToArray());
        var episode = Assert.Single(await new LocalSeriesLoader(Db, volumes: _volumes).LoadAsync(series));
        Assert.Equal(id, episode.FileId);
        Assert.Equal(Path.Combine(renamed, "Source.S01E01.mkv"), episode.FilePath);
    }

    public void Dispose()
    {
        _log.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
