using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Cataloguing a film that arrived outside a scan — today, one downloaded from the Jellyfin
    /// server.
    ///
    /// Run against a real SQLite file rather than a mock, because what is being asserted is that
    /// this write and the scanner's write agree with each other. A mock would happily let the two
    /// drift apart and every test would still pass while a downloaded film quietly appeared twice
    /// in the library.
    /// </summary>
    public class DownloadedFileCatalogueTests : IDisposable
    {
        private readonly string _root;

        public DownloadedFileCatalogueTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-record-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string WriteFilm(string name)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, "not really a film");
            return path;
        }

        [Fact]
        public async Task A_downloaded_film_becomes_a_movie_and_a_file()
        {
            var path = WriteFilm("Arrival (2016).mkv");
            using var conn = Database.Open(Path.Combine(_root, "movies.db"));

            var movieId = await ScanService.RecordSingleFileAsync(conn, path);

            var title = conn.QuerySingle<string>("SELECT title FROM movies WHERE id = @id", new { id = movieId });
            var year = conn.QuerySingle<int?>("SELECT year FROM movies WHERE id = @id", new { id = movieId });
            var linked = conn.QuerySingle<long>("SELECT movie_id FROM files WHERE file_path = @path", new { path });

            Assert.Equal("Arrival", title);
            Assert.Equal(2016, year);
            Assert.Equal(movieId, linked);
        }

        /// <summary>
        /// The reason it is playable immediately: the window reads <c>movies</c>, and finds the
        /// file through <c>files</c>, without anything having to run a scan first.
        /// </summary>
        [Fact]
        public async Task The_film_is_findable_the_moment_it_finishes()
        {
            var path = WriteFilm("Arrival (2016).mkv");
            using var conn = Database.Open(Path.Combine(_root, "movies.db"));

            await ScanService.RecordSingleFileAsync(conn, path);

            var hit = conn.QuerySingle<string>(
                "SELECT m.title FROM movies_fts f JOIN movies m ON m.id = f.rowid WHERE movies_fts MATCH @q",
                new { q = "Arrival" });

            Assert.Equal("Arrival", hit);
        }

        [Fact]
        public async Task Recording_the_same_download_twice_changes_nothing()
        {
            var path = WriteFilm("Arrival (2016).mkv");
            using var conn = Database.Open(Path.Combine(_root, "movies.db"));

            var first = await ScanService.RecordSingleFileAsync(conn, path);
            var second = await ScanService.RecordSingleFileAsync(conn, path);

            Assert.Equal(first, second);
            Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM files"));
        }

        /// <summary>
        /// The case that would otherwise fork the library: a film already known from a scan, then
        /// downloaded from the server. Two files, one film.
        /// </summary>
        [Fact]
        public async Task A_download_joins_the_film_that_is_already_in_the_catalogue()
        {
            var scanned = Path.Combine(_root, "scanned");
            Directory.CreateDirectory(scanned);
            File.WriteAllText(Path.Combine(scanned, "arrival.2016.bluray.x264-GROUP.mkv"), "x");

            var dbPath = Path.Combine(_root, "movies.db");
            await ScanService.ScanLibraryAsync(dbPath, new[] { scanned });

            using var conn = Database.Open(dbPath);
            var existing = conn.QuerySingle<long>("SELECT id FROM movies");

            var downloaded = WriteFilm("Arrival (2016).mkv");
            var movieId = await ScanService.RecordSingleFileAsync(conn, downloaded);

            Assert.Equal(existing, movieId);
            Assert.Equal(1, conn.QuerySingle<int>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(2, conn.QuerySingle<int>("SELECT COUNT(*) FROM files"));
        }

        /// <summary>
        /// A later scan of the download folder must agree with what the download already wrote,
        /// rather than inserting a second copy of the film beside it.
        /// </summary>
        [Fact]
        public async Task A_later_scan_of_the_download_folder_adds_nothing()
        {
            var path = WriteFilm("Arrival (2016).mkv");
            var dbPath = Path.Combine(_root, "movies.db");

            using (var conn = Database.Open(dbPath))
            {
                await ScanService.RecordSingleFileAsync(conn, path);
            }

            await ScanService.ScanLibraryAsync(dbPath, new[] { _root });

            using var after = Database.Open(dbPath);
            Assert.Equal(1, after.QuerySingle<int>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1, after.QuerySingle<int>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public async Task The_recorded_size_is_the_size_on_disk()
        {
            var path = WriteFilm("Arrival (2016).mkv");
            var expected = new FileInfo(path).Length;

            using var conn = Database.Open(Path.Combine(_root, "movies.db"));
            await ScanService.RecordSingleFileAsync(conn, path);

            var size = conn.QuerySingle<long>("SELECT size_bytes FROM files WHERE file_path = @path", new { path });
            Assert.Equal(expected, size);
        }

        [Fact]
        public async Task A_film_with_no_year_in_its_name_is_still_catalogued()
        {
            var path = WriteFilm("Arrival.mkv");
            using var conn = Database.Open(Path.Combine(_root, "movies.db"));

            var movieId = await ScanService.RecordSingleFileAsync(conn, path);

            Assert.Equal("Arrival", conn.QuerySingle<string>("SELECT title FROM movies WHERE id = @id", new { id = movieId }));
            Assert.Null(conn.QuerySingle<int?>("SELECT year FROM movies WHERE id = @id", new { id = movieId }));
        }

        [Fact]
        public async Task A_path_is_required()
        {
            using var conn = Database.Open(Path.Combine(_root, "movies.db"));

            await Assert.ThrowsAsync<ArgumentException>(() => ScanService.RecordSingleFileAsync(conn, "  "));
        }
    }
}
