using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A scan that meets television on this disk: episodes filed under a programme rather than
    /// each catalogued as an oddly titled film, and a library scanned before that was possible
    /// brought back into shape by the next scan.
    ///
    /// Against real files in a temporary folder and a real SQLite catalogue beside them, because
    /// what is being tested is the scan's walk, its parse and its writes together.
    /// </summary>
    public class LocalTelevisionScanTests : IDisposable
    {
        private readonly string _root;
        private readonly string _media;
        private readonly string _dbPath;
        private readonly TempLog _log = new();

        public LocalTelevisionScanTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-tv-" + Guid.NewGuid().ToString("N"));
            _media = Path.Combine(_root, "media");
            Directory.CreateDirectory(_media);
            _dbPath = Path.Combine(_root, "movies.db");
        }

        public void Dispose()
        {
            _log.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public async Task Episodes_in_season_folders_become_one_series_and_no_films()
        {
            Write("The Sopranos", "Season 01", "01 - The Sopranos.mkv");
            Write("The Sopranos", "Season 01", "02 - 46 Long.mkv");
            Write("The Sopranos", "Season 02", "01 - Guy Walks Into a Psychiatrist's Office.mkv");

            var result = await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal("The Sopranos", conn.ExecuteScalar<string>("SELECT title FROM series"));
            Assert.Equal(3, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes"));
            Assert.Equal(3, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE episode_id IS NOT NULL AND movie_id IS NULL"));
            Assert.Equal("46 Long", conn.ExecuteScalar<string>(
                "SELECT title FROM episodes WHERE season_number = 1 AND episode_number = 2"));

            Assert.Equal(3, result.Inserted);
            Assert.Equal(0, result.MoviesAdded);
            Assert.Equal(1, result.SeriesAdded);
        }

        [Fact]
        public async Task A_film_beside_a_programme_stays_a_film()
        {
            Write("Films", "Heat (1995).mkv");
            Write("TV", "Fargo.S01E01.The.Crocodiles.Dilemma.mkv");

            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal("Heat", conn.ExecuteScalar<string>("SELECT title FROM movies"));
            Assert.Equal("Fargo", conn.ExecuteScalar<string>("SELECT title FROM series"));
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NOT NULL"));
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE episode_id IS NOT NULL"));
        }

        [Fact]
        public async Task Rescanning_an_unchanged_programme_adds_nothing()
        {
            Write("Show", "Season 1", "Show.S01E01.mkv");
            Write("Show", "Season 1", "Show.S01E02.mkv");

            await ScanAsync();
            var second = await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM series"));
            Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes"));
            Assert.Equal(2, second.Unchanged);
            Assert.Equal(0, second.Inserted);
            Assert.Equal(0, second.Updated);
            Assert.Equal(0, second.SeriesAdded);
        }

        [Fact]
        public async Task Two_spellings_of_a_programme_are_one_programme_and_a_later_year_fills_in()
        {
            Write("A", "The.Office.S01E01.mkv");
            Write("B", "the office (2005)", "Season 1", "02 - Diversity Day.mkv");

            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM series"));
            Assert.Equal(2005, conn.ExecuteScalar<long>("SELECT year FROM series"));
            Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes"));
        }

        [Fact]
        public async Task Two_copies_of_one_episode_are_one_episode_with_two_files()
        {
            Write("Show", "Show.S01E01.720p.mkv");
            Write("Show", "Show.S01E01.1080p.mkv");

            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM episodes"));
            Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT file_path) FROM files WHERE episode_id IS NOT NULL"));
        }

        [Fact]
        public async Task A_specials_folder_is_season_zero()
        {
            Write("Doctor Who", "Specials", "01 - The Christmas Invasion.mkv");

            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(0, conn.ExecuteScalar<long>("SELECT season_number FROM episodes"));
        }

        [Fact]
        public async Task An_episode_catalogued_as_a_film_is_refiled_and_its_empty_film_row_goes()
        {
            // How a library scanned before television existed looks: every episode a film row of
            // its own, and a real film beside them that must come through untouched.
            var episode = Write("Show", "Show.S01E01.mkv");
            var film = Write("Films", "Heat (1995).mkv");
            SeedOldCatalogue(("Show S01E01", episode), ("Heat", film));

            var result = await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(new[] { "Heat" }, conn.Query<string>("SELECT title FROM movies").ToArray());
            Assert.Equal(1, conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM files WHERE file_path = @p AND movie_id IS NULL AND episode_id IS NOT NULL",
                new { p = episode }));
            Assert.Equal(1, conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM files WHERE file_path = @p AND movie_id IS NOT NULL", new { p = film }));
            Assert.Equal(1, result.Refiled);
            Assert.Contains("1 re-filed as episodes", result.Summary);
        }

        [Fact]
        public async Task A_film_row_that_still_has_a_film_file_survives_losing_an_episode()
        {
            var episode = Write("Show", "Show.S01E01.mkv");
            var film = Write("Films", "Show (2010).mkv");
            SeedOldCatalogue(("Show", episode));
            using (var conn = Database.Open(_dbPath))
                conn.Execute("INSERT INTO files (movie_id, file_path) VALUES (1, @p)", new { p = film });

            await ScanAsync();

            using var read = Database.Open(_dbPath);
            Assert.Equal(1, read.ExecuteScalar<long>("SELECT COUNT(*) FROM movies WHERE id = 1"));
            Assert.Equal(1, read.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE movie_id = 1"));
        }

        [Fact]
        public async Task A_file_linked_to_a_film_by_hand_is_not_refiled()
        {
            var path = Write("Films", "Fargo.S01E01.mkv");
            using (var conn = Database.Open(_dbPath))
            {
                var id = conn.ExecuteScalar<long>("INSERT INTO movies (title, year) VALUES ('Fargo', 1996) RETURNING id");
                PlayTargetResolver.LinkFile(conn, id, path);
            }

            await ScanAsync();

            using var read = Database.Open(_dbPath);
            Assert.Equal(1, read.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM files WHERE file_path = @p AND movie_id IS NOT NULL AND episode_id IS NULL",
                new { p = path }));
            Assert.Equal(0, read.ExecuteScalar<long>("SELECT COUNT(*) FROM series"));
        }

        [Fact]
        public async Task Linking_an_episode_file_by_hand_takes_it_out_of_its_programme()
        {
            var path = Write("Show", "Show.S01E01.mkv");
            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            var id = conn.ExecuteScalar<long>("INSERT INTO movies (title) VALUES ('Something Else') RETURNING id");
            PlayTargetResolver.LinkFile(conn, id, path);

            var row = conn.QuerySingle<(long? MovieId, long? EpisodeId, long ByHand)>(
                "SELECT movie_id, episode_id, linked_by_hand FROM files WHERE file_path = @p", new { p = path });
            Assert.Equal(id, row.MovieId);
            Assert.Null(row.EpisodeId);
            Assert.Equal(1, row.ByHand);
        }

        [Fact]
        public async Task A_deleted_episode_is_marked_missing_and_listed_as_unavailable()
        {
            Write("Show", "Show.S01E01.mkv");
            var gone = Write("Show", "Show.S01E02.mkv");
            await ScanAsync();

            File.Delete(gone);
            var result = await ScanAsync();

            Assert.Equal(1, result.Missing);

            using var conn = Database.Open(_dbPath);
            var seriesId = conn.ExecuteScalar<long>("SELECT id FROM series");
            var episodes = new LocalSeriesLoader(_dbPath).Load(seriesId);

            Assert.Equal(2, episodes.Count);
            Assert.True(episodes.Single(e => e.EpisodeNumber == 1).IsAvailable);
            var missing = episodes.Single(e => e.EpisodeNumber == 2);
            Assert.False(missing.IsAvailable);
            Assert.Equal(gone, missing.AnyPath);
        }

        [Fact]
        public async Task An_episode_file_is_never_suggested_as_a_films_copy()
        {
            Write("TV", "Fargo.S01E01.mkv");
            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            var film = conn.ExecuteScalar<long>("INSERT INTO movies (title, year) VALUES ('Fargo', 1996) RETURNING id");

            var target = PlayTargetResolver.Resolve(conn, film, "Fargo", 1996);

            Assert.Equal(PlayTargetKind.None, target.Kind);
        }

        [Fact]
        public async Task A_file_that_is_not_an_episode_is_still_a_film()
        {
            // Absolute numbering is declined by the parser on purpose, so it stays what it was.
            Write("Anime", "Show - 137.mkv");

            await ScanAsync();

            using var conn = Database.Open(_dbPath);
            Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM series"));
        }

        private async Task<ScanResult> ScanAsync()
        {
            using var conn = Database.Open(_dbPath);
            return await new ScanService().ScanAsync(conn, new[] { _media });
        }

        private string Write(params string[] parts)
        {
            var path = Path.Combine(new[] { _media }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "video " + path);
            return path;
        }

        /// <summary>One film row per entry, each linked to its file, as an older scan wrote them.</summary>
        private void SeedOldCatalogue(params (string Title, string Path)[] films)
        {
            using var conn = Database.Open(_dbPath);
            foreach (var (title, path) in films)
            {
                var id = conn.ExecuteScalar<long>("INSERT INTO movies (title) VALUES (@title) RETURNING id", new { title });
                conn.Execute(
                    "INSERT INTO files (movie_id, file_path, size_bytes, updated_at) VALUES (@id, @path, @size, @updated)",
                    new
                    {
                        id,
                        path,
                        size = new FileInfo(path).Length,
                        updated = new FileInfo(path).LastWriteTimeUtc.ToString("o")
                    });
            }
        }
    }
}
