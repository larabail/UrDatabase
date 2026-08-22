using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The whole point of a scan, asserted end to end: films on disk become cards on screen.
    ///
    /// This walks the same path the window does — scan, then the query <c>LoadMovies</c> runs,
    /// then the grouping the view binds to — because every individual piece passing while the
    /// library still renders empty is exactly the failure this pull request exists to fix.
    /// </summary>
    public class LibraryAfterScanTests : IDisposable
    {
        private const string LoadMoviesSql =
            "SELECT id AS Id, title AS Title, year AS Year, genres AS Genres, poster_path AS PosterPath " +
            "FROM movies ORDER BY COALESCE(year,0) DESC, title";

        private readonly string _root;

        public LibraryAfterScanTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public async Task A_scanned_folder_becomes_a_library_a_user_can_see()
        {
            var films = Path.Combine(_root, "Films");
            Directory.CreateDirectory(films);
            foreach (var name in new[]
                     {
                         "The Matrix (1999) 1080p.mkv",
                         "heat.1995.bluray.x264-GROUP.mkv",
                         "Blade Runner 2049 [2017].mp4",
                         "Amélie.mkv",
                     })
            {
                File.WriteAllText(Path.Combine(films, name), "x");
            }

            var dbPath = Path.Combine(_root, "movies.db");
            var updated = await ScanService.ScanLibraryAsync(dbPath, new[] { films });

            using var conn = Database.Open(dbPath);
            var movies = conn.Query<UiMovie>(LoadMoviesSql).ToList();

            var genres = LibraryGrouping.BuildGenreList(movies);
            var groups = genres
                .Where(g => g != LibraryGrouping.AllGenres)
                .Select(g => LibraryGrouping.ItemsForGenre(movies, g))
                .Where(items => items.Count > 0)
                .ToList();

            Assert.Equal(4, updated);
            Assert.Equal(4, movies.Count);

            // No TMDB key in a test run, so nothing has a genre yet. The library still has to be
            // reachable: before this change the grouped view iterated genres only and rendered a
            // blank page for exactly this state.
            Assert.NotEmpty(groups);
            Assert.Equal(4, groups.Sum(g => g.Count));

            Assert.Equal(
                new[] { "Blade Runner 2049", "The Matrix", "Heat", "Amélie" },
                movies.Select(m => m.Title));
        }

        [Fact]
        public async Task A_second_scan_leaves_the_same_library_behind()
        {
            var films = Path.Combine(_root, "Films");
            Directory.CreateDirectory(films);
            File.WriteAllText(Path.Combine(films, "The Matrix (1999).mkv"), "x");
            File.WriteAllText(Path.Combine(films, "The Matrix (1999) 4K.mp4"), "x");

            var dbPath = Path.Combine(_root, "movies.db");
            await ScanService.ScanLibraryAsync(dbPath, new[] { films });
            await ScanService.ScanLibraryAsync(dbPath, new[] { films });

            using var conn = Database.Open(dbPath);
            var movies = conn.Query<UiMovie>(LoadMoviesSql).ToList();

            Assert.Single(movies);
            Assert.Equal("The Matrix", movies[0].Title);
            Assert.Equal(1999, movies[0].Year);
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id = @id", new { id = movies[0].Id }));
        }
    }
}
