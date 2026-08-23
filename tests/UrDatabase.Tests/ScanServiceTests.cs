using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class ScanServiceTests : IDisposable
    {
        private readonly string _root;

        public ScanServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("movie.mkv")]
        [InlineData("movie.mp4")]
        [InlineData("movie.avi")]
        [InlineData("movie.mov")]
        [InlineData("movie.wmv")]
        [InlineData("movie.m4v")]
        [InlineData("movie.mpg")]
        [InlineData("movie.mpeg")]
        [InlineData("movie.ts")]
        [InlineData("movie.webm")]
        public void Recognises_video_extensions(string name)
        {
            Assert.True(ScanService.IsVideoFile(name));
        }

        [Theory]
        [InlineData("cover.jpg")]
        [InlineData("subtitles.srt")]
        [InlineData("notes.txt")]
        [InlineData("movie.mkv.part")]
        [InlineData("no-extension")]
        [InlineData("")]
        [InlineData(null)]
        public void Rejects_everything_else(string? name)
        {
            Assert.False(ScanService.IsVideoFile(name));
        }

        [Theory]
        [InlineData("MOVIE.MKV")]
        [InlineData("Movie.Mp4")]
        [InlineData("movie.MOV")]
        public void Extension_matching_is_case_insensitive_on_every_filesystem(string name)
        {
            Assert.True(ScanService.IsVideoFile(name));
        }

        [Fact]
        public void Recognises_video_files_by_full_path_on_either_separator()
        {
            Assert.True(ScanService.IsVideoFile(Path.Combine("/Volumes", "Media", "The Movie (1999).mkv")));
            Assert.True(ScanService.IsVideoFile(@"D:\Movies\The Movie (1999).mkv"));
        }

        [Fact]
        public void Enumerates_files_recursively()
        {
            var nested = Path.Combine(_root, "a", "b");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(_root, "top.mkv"), "x");
            File.WriteAllText(Path.Combine(nested, "deep.mp4"), "x");
            File.WriteAllText(Path.Combine(nested, "cover.jpg"), "x");

            var found = ScanService.EnumerateFilesSafe(_root).ToList();

            Assert.Equal(3, found.Count);
            Assert.Equal(2, found.Count(ScanService.IsVideoFile));
        }

        [Fact]
        public void Enumeration_survives_a_directory_it_cannot_read()
        {
            var readable = Path.Combine(_root, "readable");
            Directory.CreateDirectory(readable);
            File.WriteAllText(Path.Combine(readable, "movie.mkv"), "x");

            var blocked = Path.Combine(_root, "blocked");
            Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "hidden.mkv"), "x");

            var restricted = false;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(blocked, UnixFileMode.None);
                restricted = true;
            }

            try
            {
                var found = ScanService.EnumerateFilesSafe(_root).ToList();

                Assert.Contains(found, f => f.EndsWith("movie.mkv", StringComparison.Ordinal));
                if (restricted)
                    Assert.DoesNotContain(found, f => f.EndsWith("hidden.mkv", StringComparison.Ordinal));
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [Fact]
        public void Enumerating_a_missing_directory_yields_nothing_instead_of_throwing()
        {
            var found = ScanService.EnumerateFilesSafe(Path.Combine(_root, "nope")).ToList();

            Assert.Empty(found);
        }

        [Fact]
        public async Task Scan_records_only_video_files()
        {
            var moviesDir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(moviesDir);
            File.WriteAllText(Path.Combine(moviesDir, "The Movie (1999).mkv"), "x");
            File.WriteAllText(Path.Combine(moviesDir, "Another.MP4"), "x");
            File.WriteAllText(Path.Combine(moviesDir, "poster.jpg"), "x");

            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            var result = await scanner.ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(2, result.Inserted);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }

        [Fact]
        public async Task Rescanning_updates_rather_than_duplicates()
        {
            var moviesDir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(moviesDir);
            File.WriteAllText(Path.Combine(moviesDir, "The Movie.mkv"), "x");

            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            await scanner.ScanAsync(conn, new[] { moviesDir });
            await scanner.ScanAsync(conn, new[] { moviesDir });

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        [Fact]
        public async Task Missing_watch_folders_are_skipped_silently()
        {
            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            var result = await scanner.ScanAsync(conn, new[] { Path.Combine(_root, "gone"), @"D:\Movies" });

            Assert.Equal(0, result.FilesSeen);
            Assert.Empty(result.Roots);
            Assert.Equal(2, result.SkippedRoots.Count);
        }

        [Fact]
        public async Task Scanning_creates_the_movie_rows_the_library_is_built_from()
        {
            var moviesDir = MakeFiles("The Matrix (1999) 1080p.mkv", "Heat.1995.BluRay.x264.mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            var movies = conn.Query<string>(
                "SELECT title || ' (' || COALESCE(year, '?') || ')' FROM movies ORDER BY title").ToList();

            Assert.Equal(new[] { "Heat (1995)", "The Matrix (1999)" }, movies);
        }

        [Fact]
        public async Task Every_scanned_file_is_linked_to_its_movie()
        {
            var moviesDir = MakeFiles("The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            var linked = conn.QuerySingle<long>(
                "SELECT COUNT(*) FROM files f JOIN movies m ON m.id = f.movie_id WHERE m.title = 'The Matrix'");

            Assert.Equal(1L, linked);
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NULL"));
        }

        [Fact]
        public async Task Two_copies_of_one_film_share_a_single_movie_row()
        {
            var moviesDir = MakeFiles(
                "The Matrix (1999) 1080p.mkv",
                "The.Matrix.1999.2160p.BluRay.x265-GROUP.mkv",
                "The Matrix.mp4");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(3L, conn.QuerySingle<long>("SELECT COUNT(DISTINCT file_path) FROM files WHERE movie_id IS NOT NULL"));
        }

        [Fact]
        public async Task Rescanning_the_same_folder_creates_no_duplicate_movies_and_keeps_the_links()
        {
            var moviesDir = MakeFiles("The Matrix (1999).mkv", "Heat.1995.mkv", "Alien.mkv");

            using var conn = Database.Open(DbPath);
            var scanner = new ScanService();

            const string links = "SELECT file_path || ' -> ' || COALESCE(movie_id, 'none') FROM files ORDER BY file_path";

            await scanner.ScanAsync(conn, new[] { moviesDir });
            var first = conn.Query<string>(links).ToList();

            await scanner.ScanAsync(conn, new[] { moviesDir });
            var second = conn.Query<string>(links).ToList();

            Assert.Equal(3L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(3, first.Count);
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task A_movie_that_already_exists_is_linked_to_rather_than_duplicated()
        {
            // The state of anyone whose library was populated before this app could write movies.
            var moviesDir = MakeFiles("the.matrix.1999.1080p.mkv");

            using var conn = Database.Open(DbPath);
            conn.Execute("INSERT INTO movies (title, year, genres) VALUES ('The Matrix', 1999, 'Action')");

            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal("Action", conn.QuerySingle<string>("SELECT genres FROM movies"));
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NOT NULL"));
        }

        [Fact]
        public async Task A_filename_with_a_year_fills_in_a_movie_that_had_none()
        {
            var moviesDir = MakeFiles("The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            conn.Execute("INSERT INTO movies (title, year) VALUES ('The Matrix', NULL)");

            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1999, conn.QuerySingle<int?>("SELECT year FROM movies"));
        }

        [Fact]
        public async Task A_link_made_by_hand_is_never_overwritten_by_a_scan()
        {
            var moviesDir = MakeFiles("The Matrix (1999).mkv");
            var path = Path.Combine(moviesDir, "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            conn.Execute("INSERT INTO movies (title, year) VALUES ('Something Else', 2020)");
            var chosen = conn.QuerySingle<long>("SELECT id FROM movies");
            conn.Execute("INSERT INTO files (movie_id, file_path) VALUES (@id, @path)", new { id = chosen, path });

            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(chosen, conn.QuerySingle<long>("SELECT movie_id FROM files WHERE file_path = @path", new { path }));
        }

        [Fact]
        public async Task Scanned_movies_are_searchable_straight_away()
        {
            // Proves the FTS triggers saw the insert: the id written to files has to be the movie's
            // own, not whatever the trigger's write to the index left behind.
            var moviesDir = MakeFiles("Blade Runner 2049 (2017).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { moviesDir });

            var found = conn.Query<string>(
                "SELECT m.title FROM movies_fts f JOIN movies m ON m.id = f.rowid WHERE movies_fts MATCH 'blade'").ToList();

            Assert.Equal(new[] { "Blade Runner 2049" }, found);
        }

        [Fact]
        public async Task Progress_is_reported_rather_than_swallowed()
        {
            var moviesDir = MakeFiles("The Matrix (1999).mkv");
            var messages = new List<string>();

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { moviesDir }, new Progress<string>(messages.Add));

            // Progress<T> posts through the captured context, so give it a moment to drain.
            for (var i = 0; i < 50 && messages.Count < 2; i++) await Task.Delay(10);

            Assert.Contains(messages, m => m.StartsWith("Scanning:", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.Contains("1 added", StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_scan_owns_its_connection_and_commits_before_it_returns()
        {
            // The bug this guards: the click handler opened a connection, started the scan without
            // awaiting it and returned, disposing the connection underneath the scan. Every write
            // then failed into a progress message nobody read. ScanLibraryAsync owns the connection
            // for exactly as long as the scan, so a second connection sees the whole library the
            // moment the task completes.
            var moviesDir = MakeFiles("The Matrix (1999).mkv", "Heat.1995.mkv");

            var result = await ScanService.ScanLibraryAsync(DbPath, new[] { moviesDir });

            using var verify = Database.Open(DbPath);
            Assert.Equal(2, result.Inserted);
            Assert.Equal(2L, verify.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(2L, verify.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NOT NULL"));
        }

        [Fact]
        public async Task A_scan_handed_a_dead_connection_fails_loudly()
        {
            // The old handler let this happen silently. It must be an exception the UI can report.
            var moviesDir = MakeFiles("The Matrix (1999).mkv");

            var conn = Database.Open(DbPath);
            conn.Dispose();

            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => new ScanService().ScanAsync(conn, new[] { moviesDir }));
        }

        [Fact]
        public async Task A_cancelled_scan_keeps_what_it_already_catalogued()
        {
            var moviesDir = MakeFiles("The Matrix (1999).mkv");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { moviesDir }, null, cts.Token);

            // Nothing was written, but the transaction was closed cleanly rather than left open.
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            conn.Execute("INSERT INTO movies (title) VALUES ('still writable')");
        }

        [Fact]
        public async Task A_file_the_parser_cannot_read_is_still_only_catalogued_once()
        {
            // "+++" normalises to nothing, so an index keyed only on the normalised title never
            // found the row again and each scan inserted another copy of the same film.
            var moviesDir = MakeFiles("+++.mkv", "The Matrix (1999).mkv");

            using var conn = Database.Open(DbPath);
            var scanner = new ScanService();

            await scanner.ScanAsync(conn, new[] { moviesDir });
            await scanner.ScanAsync(conn, new[] { moviesDir });
            await scanner.ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NULL"));
        }

        [Fact]
        public async Task A_scan_that_spans_several_transactions_still_catalogues_every_file()
        {
            // The batch size is 200, so this crosses a commit boundary mid-folder.
            var names = Enumerable.Range(1, 250).Select(i => $"Film {i} ({1900 + i % 100}).mkv").ToArray();
            var moviesDir = MakeFiles(names);

            using var conn = Database.Open(DbPath);
            var result = await new ScanService().ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(250, result.Inserted);
            Assert.Equal(250L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id IS NULL"));
        }

        [Fact]
        public void The_library_can_still_be_read_while_a_scan_holds_its_transaction()
        {
            // Under Cache=Shared this threw "database table is locked" instead, and the window
            // reported an unreadable library — an empty page — for the length of every scan.
            using var writer = Database.Open(DbPath);
            writer.Execute("INSERT INTO movies (title, year) VALUES ('Committed', 1999)");

            using var tx = writer.BeginTransaction();
            writer.Execute("INSERT INTO movies (title, year) VALUES ('In flight', 2020)", transaction: tx);

            using var reader = Database.Open(DbPath);
            var titles = reader.Query<string>("SELECT title FROM movies ORDER BY title").ToList();

            Assert.Equal(new[] { "Committed" }, titles);
            tx.Commit();
        }

        private string DbPath => Path.Combine(_root, "test.db");

        private string MakeFiles(params string[] names)
        {
            var dir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(dir);
            foreach (var name in names) File.WriteAllText(Path.Combine(dir, name), "x");
            return dir;
        }

        // ---------- a film renamed by a corrected TMDB match ----------

        private static long CountMovies(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM movies";
            return (long)cmd.ExecuteScalar()!;
        }

        /// <summary>
        /// The hazard that made renaming impossible until now. The scanner resolves what it parses
        /// out of a filename, so a film renamed to its real title no longer answers to the name on
        /// disk — and the next scan, finding nothing, used to insert a second row for a film
        /// already in the catalogue.
        /// </summary>
        [Fact]
        public async Task A_renamed_film_is_not_catalogued_twice_by_the_next_scan()
        {
            var moviesDir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(moviesDir);
            File.WriteAllText(Path.Combine(moviesDir, "El Drama (2026).mkv"), "x");

            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            await scanner.ScanAsync(conn, new[] { moviesDir });
            Assert.Equal(1L, CountMovies(conn));

            // What "Wrong film?" does.
            var id = conn.ExecuteScalar<long>("SELECT id FROM movies");
            await MovieMatch.SaveAsync(conn, id, 901, "/right.jpg", "The Drama");
            Assert.Equal("The Drama", conn.ExecuteScalar<string>("SELECT title FROM movies WHERE id=@id", new { id }));

            await scanner.ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(1L, CountMovies(conn));
            Assert.Equal("The Drama", conn.ExecuteScalar<string>("SELECT title FROM movies WHERE id=@id", new { id }));
        }

        /// <summary>
        /// And a second copy of a renamed film, arriving after the rename, joins the row it belongs
        /// to rather than starting another one — the file on disk still carries the old name.
        /// </summary>
        [Fact]
        public async Task A_new_copy_of_a_renamed_film_joins_the_film_it_belongs_to()
        {
            var moviesDir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(moviesDir);
            File.WriteAllText(Path.Combine(moviesDir, "El Drama (2026).mkv"), "x");

            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            await scanner.ScanAsync(conn, new[] { moviesDir });
            var id = conn.ExecuteScalar<long>("SELECT id FROM movies");
            await MovieMatch.SaveAsync(conn, id, 901, null, "The Drama");

            File.WriteAllText(Path.Combine(moviesDir, "El Drama (2026) 1080p.mkv"), "x");
            await scanner.ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(1L, CountMovies(conn));
            Assert.Equal(2L, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM files WHERE movie_id=@id", new { id }));
        }
    }
}
