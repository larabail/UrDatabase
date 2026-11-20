using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests;

public class LocalSeriesGroupingTests : IDisposable
{
    private readonly TempLog _log = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urdb-grouping-" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly string _db;
    private readonly SqliteConnection _conn;

    public LocalSeriesGroupingTests()
    {
        _media = Path.Combine(_root, "media");
        _db = Path.Combine(_root, "movies.db");
        Assert.StartsWith(Path.GetTempPath(), _db);
        Directory.CreateDirectory(_media);
        _conn = Database.Open(_db);
    }

    [Fact]
    public async Task Split_series_and_a_loose_film_can_be_grouped_and_stay_grouped_after_a_scan()
    {
        Write("Los.Simpsons.S01E01.mkv");
        Write("The.Simpsons.S01E02.mkv");
        Write("Loose pilot.mkv");
        await Scan();
        _conn.Execute("UPDATE series SET year = 1989");
        var target = LocalSeriesGrouping.Targets(_conn).Single(s => s.Title == "The Simpsons");
        Assert.Equal("The Simpsons (1989)", target.Label);
        var files = LocalSeriesGrouping.Files(_conn);
        Assert.Equal(3, files.Count);
        var loose = files.Single(f => f.MovieId is not null);
        Assert.Throws<ArgumentException>(() => loose.Assignment());
        loose.SeasonText = "1";
        loose.EpisodeText = "3";

        var id = LocalSeriesGrouping.Assign(_conn, target.Id, null, files.Select(f => f.Assignment()).ToArray());
        Assert.Equal(target.Id, id);
        Assert.Equal(3, new LocalSeriesLoader(_db).Load(id).Count);
        Assert.Equal(0, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
        Assert.Equal(3, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NULL AND linked_by_hand = 1"));
        Assert.All(files, f => Assert.Equal("video " + Path.GetFileName(f.FilePath), File.ReadAllText(f.FilePath)));

        var result = await Scan();
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.MoviesAdded);
        var cards = new MovieRepository(_db).Query(null);
        Assert.Equal(id, Assert.Single(cards).Id);
        Assert.True(cards[0].IsSeries);
        Assert.Equal(3, new LocalSeriesLoader(_db).Load(id).Count);
    }

    [Fact]
    public async Task Moving_an_episode_does_not_copy_metadata_from_the_wrong_series()
    {
        Write("Wrong.Show.S01E01.mkv");
        await Scan();
        _conn.Execute("UPDATE episodes SET tmdb_title = 'Wrong title', overview = 'Wrong plot', tmdb_fetched_at = 'now'");
        var file = Assert.Single(LocalSeriesGrouping.Files(_conn));

        var target = LocalSeriesGrouping.Assign(_conn, null, "Right show", new[] { file.Assignment() });

        var episode = Assert.Single(new LocalSeriesLoader(_db).Load(target));
        Assert.Null(episode.TmdbTitle);
        Assert.Null(episode.Overview);
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes"));
    }

    [Fact]
    public async Task Matching_numbers_combine_copies_without_replacing_the_destination_metadata()
    {
        Write("First.Show.S01E01.mkv");
        Write("Second.Show.S01E01.mkv");
        await Scan();
        var files = LocalSeriesGrouping.Files(_conn);
        var target = LocalSeriesGrouping.Targets(_conn).Single(s => s.Title == "First Show");
        _conn.Execute("UPDATE episodes SET tmdb_title = 'Correct title' WHERE series_id = @id", new { id = target.Id });

        LocalSeriesGrouping.Assign(_conn, target.Id, null, files.Select(f => f.Assignment()).ToArray());

        Assert.Equal(2, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files"));
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes"));
        Assert.Equal("Correct title", Assert.Single(new LocalSeriesLoader(_db).Load(target.Id)).TmdbTitle);
    }

    [Fact]
    public async Task A_stale_selection_rolls_back_every_assignment_and_new_series()
    {
        Write("First.Show.S01E01.mkv");
        Write("Second.Show.S01E02.mkv");
        await Scan();
        var files = LocalSeriesGrouping.Files(_conn);
        var assignments = files.Select(f => f.Assignment()).ToArray();
        assignments[1] = assignments[1] with { EpisodeId = 9999 };
        var oldIds = _conn.Query<long>("SELECT episode_id FROM files ORDER BY id").ToArray();

        Assert.Throws<InvalidOperationException>(() => LocalSeriesGrouping.Assign(_conn, null, "New show", assignments));

        Assert.Equal(oldIds, _conn.Query<long>("SELECT episode_id FROM files ORDER BY id"));
        Assert.Equal(0, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM series WHERE title = 'New show'"));
    }

    [Fact]
    public async Task An_empty_film_with_a_personal_journal_is_preserved_instead_of_losing_the_notes()
    {
        Write("Loose pilot.mkv");
        await Scan();
        var identity = new FilmIdentity(null, "Loose pilot", null);
        FilmJournalStore.SaveRating(_conn, identity, 8, "Keep my notes", DateTime.UtcNow);
        FilmJournalStore.AddViewing(_conn, identity, null, null, null, DateTime.UtcNow);
        var file = Assert.Single(LocalSeriesGrouping.Files(_conn));
        file.EpisodeText = "1";

        var target = LocalSeriesGrouping.Assign(_conn, null, "New series", new[] { file.Assignment() });

        Assert.Single(new LocalSeriesLoader(_db).Load(target));
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
        Assert.Equal("Keep my notes", FilmJournalStore.GetRating(_conn, identity)!.Review);
        Assert.Null(Assert.Single(FilmJournalStore.LoadViewings(_conn, identity)).WatchedOn);
    }

    [Fact]
    public async Task An_assignment_survives_moving_the_file_to_a_different_folder()
    {
        var original = Write("Wrong.Show.S01E01.mkv");
        await Scan();
        var assignment = Assert.Single(LocalSeriesGrouping.Files(_conn)).Assignment() with { Season = null, Episode = 42 };
        var target = LocalSeriesGrouping.Assign(_conn, null, "Right show", new[] { assignment });
        var destination = Path.Combine(_media, "moved");
        Directory.CreateDirectory(destination);
        File.Move(original, Path.Combine(destination, Path.GetFileName(original)));

        Assert.Equal(0, (await Scan()).Failed);
        var episode = Assert.Single(new LocalSeriesLoader(_db).Load(target));
        Assert.Null(episode.SeasonNumber);
        Assert.Equal(42, episode.EpisodeNumber);
        Assert.Equal(Path.Combine(destination, Path.GetFileName(original)), episode.FilePath);
    }

    [Fact]
    public async Task Invalid_or_duplicate_selections_and_duplicate_new_titles_are_refused()
    {
        Write("Known.Show.S01E01.mkv");
        await Scan();
        var assignment = Assert.Single(LocalSeriesGrouping.Files(_conn)).Assignment();
        Assert.Throws<ArgumentException>(() => LocalSeriesGrouping.Assign(_conn, null, "known show", new[] { assignment }));
        Assert.Throws<ArgumentException>(() => LocalSeriesGrouping.Assign(_conn, null, "New", new[] { assignment, assignment }));
        Assert.Throws<ArgumentException>(() => LocalSeriesGrouping.Assign(_conn, null, "New", new[] { assignment with { Episode = -1 } }));
        Assert.Throws<ArgumentException>(() => LocalSeriesGrouping.Assign(_conn, null, "New", Array.Empty<EpisodeAssignment>()));
        Assert.Throws<InvalidOperationException>(() => LocalSeriesGrouping.Assign(_conn, 9999, null, new[] { assignment }));
        Assert.Equal(1, _conn.ExecuteScalar<long>("SELECT COUNT(*) FROM series"));
    }

    private string Write(string name)
    {
        var path = Path.Combine(_media, name);
        File.WriteAllText(path, "video " + name);
        return path;
    }

    private Task<ScanResult> Scan() => new ScanService().ScanAsync(_conn, new[] { _media });

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
        _log.Dispose();
    }
}
