using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The line under the library, and the fact that it has to keep moving.
    ///
    /// "Posters present: 648/6000" was computed once, when the library was read, and then never
    /// again. Artwork goes on arriving for minutes afterwards, and every poster changed the first
    /// number without changing the sentence — so the only thing on screen reporting progress sat
    /// still for the whole of it, which is what a person with six thousand films reported as the
    /// bar being stuck. It was not stuck. It was written once.
    /// </summary>
    public class LibraryTallyTests : IDisposable
    {
        private readonly string _dir;
        private readonly TempLog _log = new();

        public LibraryTallyTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-tally-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string DbPath => Path.Combine(_dir, "movies.db");

        private static LibraryTally Tally(int total, int withPosters) =>
            new(LocalCount: total,
                LocalWithPosters: withPosters,
                RemoteCount: 0,
                HasLocalDatabase: true,
                DatabasePath: "/somewhere/movies.db",
                RemoteSeriesCount: 0);

        // ---------- re-describing ----------

        [Fact]
        public void The_line_can_be_written_again_with_a_new_count()
        {
            var tally = Tally(total: 6000, withPosters: 648);

            Assert.Equal("Posters present: 648/6000", tally.Describe());
            Assert.Equal("Posters present: 649/6000", tally.WithPosters(649).Describe());
            Assert.Equal("Posters present: 6000/6000", tally.WithPosters(6000).Describe());
        }

        /// <summary>
        /// A record, so re-describing cannot quietly change the library the numbers are about.
        /// </summary>
        [Fact]
        public void Rewriting_the_count_leaves_everything_else_alone()
        {
            var tally = Tally(total: 6000, withPosters: 648);
            var later = tally.WithPosters(5000);

            Assert.Equal(648, tally.LocalWithPosters);
            Assert.Equal(6000, later.LocalCount);
            Assert.Equal(tally.DatabasePath, later.DatabasePath);
        }

        [Fact]
        public void A_server_library_is_still_described_beside_the_posters()
        {
            var tally = new LibraryTally(
                LocalCount: 10,
                LocalWithPosters: 2,
                RemoteCount: 412,
                HasLocalDatabase: true,
                DatabasePath: "/somewhere/movies.db",
                RemoteSeriesCount: 7);

            Assert.Equal(
                "Posters present: 3/10 · 412 films and 7 series on the Jellyfin server",
                tally.WithPosters(3).Describe());
        }

        // ---------- where it comes from ----------

        /// <summary>
        /// The loader has to hand the numbers over, or the window has nothing to rewrite the line
        /// from and is back to describing the library as it was when it was opened.
        /// </summary>
        [Fact]
        public void A_library_read_carries_the_numbers_its_line_was_written_from()
        {
            SeedCatalogue(films: 5, withPosters: 2);

            var view = new LibraryLoader(new MovieRepository(DbPath)).Load(query: null);

            Assert.NotNull(view.Tally);
            Assert.Equal(5, view.Tally!.LocalCount);
            Assert.Equal(2, view.Tally.LocalWithPosters);
            Assert.Equal(view.Status, view.Tally.Describe());
        }

        /// <summary>
        /// A read that failed says so, and there is nothing to count. The window must not then
        /// overwrite the explanation with a poster tally of zero.
        /// </summary>
        [Fact]
        public void A_read_that_failed_carries_no_numbers()
        {
            File.WriteAllText(DbPath, "this is not a database");

            var failures = new List<Exception>();
            var view = new LibraryLoader(new MovieRepository(DbPath), onQueryFailed: failures.Add).Load(query: null);

            Assert.Null(view.Tally);
            Assert.StartsWith("Could not read the library:", view.Status, StringComparison.Ordinal);
            Assert.NotEmpty(failures);
        }

        /// <summary>
        /// The count is of films this computer can play, which is what the sentence claims. A film
        /// only on the server has its artwork from the server and is counted on the other side of
        /// the line.
        /// </summary>
        [Fact]
        public void Only_films_on_this_computer_are_counted()
        {
            SeedCatalogue(films: 3, withPosters: 1);

            var server = new List<UiMovie>
            {
                new() { Title = "Something Remote", Year = 2001, Source = MovieSource.Jellyfin, RemoteId = "abc" }
            };

            var view = new LibraryLoader(new MovieRepository(DbPath)).Load(query: null, remote: server);

            Assert.Equal(3, view.Tally!.LocalCount);
            Assert.Equal(1, view.Tally.RemoteCount);
        }

        private void SeedCatalogue(int films, int withPosters)
        {
            using var conn = Database.Open(DbPath);

            for (var i = 1; i <= films; i++)
            {
                conn.Execute(
                    "INSERT INTO movies (id, title, year, poster_path) VALUES (@id, @title, 1999, @poster)",
                    new { id = i, title = $"Film {i}", poster = i <= withPosters ? $"/p{i}.jpg" : null });

                conn.Execute(
                    "INSERT INTO files (movie_id, file_path, last_seen_at) VALUES (@id, @path, datetime('now'))",
                    new { id = i, path = $"/films/{i}.mkv" });
            }
        }
    }
}
