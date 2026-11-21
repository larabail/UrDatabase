using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public class CatalogueRemovalTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urdb-removal-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _conn;
    private readonly string _path;

    public CatalogueRemovalTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "movies.db");
        Assert.StartsWith(Path.GetTempPath(), _path);
        _conn = Database.Open(_path);
    }

    [Fact]
    public async Task Removing_a_local_film_preserves_the_file_and_diary_and_a_scan_can_add_it_again()
    {
        var media = Path.Combine(_root, "media");
        Directory.CreateDirectory(media);
        var file = Path.Combine(media, "The Matrix (1999).mkv");
        File.WriteAllText(file, "unchanged video");
        await new FixtureScanner().ScanAsync(_conn, new[] { media });
        var id = _conn.ExecuteScalar<long>("SELECT id FROM movies");
        var identity = new FilmIdentity(null, "The Matrix", 1999);
        FilmJournalStore.SaveRating(_conn, identity, 9, "Keep this review", DateTime.UtcNow);
        FilmJournalStore.AddViewing(_conn, identity, null, null, null, DateTime.UtcNow);
        _conn.Execute("INSERT INTO watchlist VALUES (@key, 'now')", new { key = Watchlist.LocalKey(id) });

        Assert.True(CatalogueRemoval.Remove(_conn, id, null));
        Assert.False(CatalogueRemoval.Remove(_conn, id, null));
        Assert.Equal("unchanged video", File.ReadAllText(file));
        Assert.Equal(0, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files"));
        Assert.Equal(0, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies_fts WHERE movies_fts MATCH 'matrix'"));
        Assert.Equal(0, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM watchlist"));
        Assert.Equal("Keep this review", FilmJournalStore.GetRating(_conn, identity)!.Review);
        Assert.Single(FilmJournalStore.LoadViewings(_conn, identity));

        await new FixtureScanner().ScanAsync(_conn, new[] { media });
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
    }

    [Fact]
    public void Removing_a_combined_card_removes_only_its_local_and_cached_server_entries()
    {
        _conn.Execute(@"
INSERT INTO movies (id, title) VALUES (1, 'Chosen'), (2, 'Kept');
INSERT INTO files (movie_id, file_path) VALUES (1, @first), (2, @second);
INSERT INTO jellyfin_movies (item_id, title, synced_at) VALUES ('chosen', 'Chosen', 'now'), ('kept', 'Kept', 'now');
INSERT INTO jellyfin_resume (item_id, position_ticks, sort_order, synced_at) VALUES ('chosen', 10, 0, 'now');
INSERT INTO watchlist VALUES ('local:1', 'now'), ('jellyfin:chosen', 'now'), ('local:2', 'now');",
            new { first = Path.Combine(_root, "chosen.mkv"), second = Path.Combine(_root, "kept.mkv") });

        Assert.True(CatalogueRemoval.Remove(_conn, 1, "chosen"));
        Assert.Equal("Kept", _conn.ExecuteScalar<string>("SELECT title FROM movies"));
        Assert.Equal("kept", _conn.ExecuteScalar<string>("SELECT item_id FROM jellyfin_movies"));
        Assert.Equal(0, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM jellyfin_resume"));
        Assert.Equal("local:2", _conn.ExecuteScalar<string>("SELECT item_key FROM watchlist"));
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files"));
    }

    [Fact]
    public void A_server_only_film_can_be_removed_without_touching_a_series()
    {
        _conn.Execute(@"
INSERT INTO jellyfin_movies (item_id, title, synced_at) VALUES ('film', 'Film', 'now');
INSERT INTO jellyfin_series (item_id, title, synced_at) VALUES ('series', 'Series', 'now');");
        Assert.True(CatalogueRemoval.Remove(_conn, 0, "film"));
        Assert.False(CatalogueRemoval.Remove(_conn, 0, "series"));
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM jellyfin_series"));
    }

    [Fact]
    public void A_failed_removal_rolls_back_the_file_links()
    {
        _conn.Execute(@"
INSERT INTO movies (id, title) VALUES (1, 'Kept');
INSERT INTO files (movie_id, file_path) VALUES (1, @path);
CREATE TRIGGER refuse_removal BEFORE DELETE ON movies BEGIN SELECT RAISE(ABORT, 'refused'); END;",
            new { path = Path.Combine(_root, "kept.mkv") });
        Assert.Throws<SqliteException>(() => CatalogueRemoval.Remove(_conn, 1, null));
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT movie_id FROM files"));
    }

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        _log.Dispose();
    }
}
