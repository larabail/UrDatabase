using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What the Play button is allowed to open, asserted against a real database.
    ///
    /// The bug this fixes was not in any one rule but in which source of truth was consulted:
    /// <c>files.movie_id</c> holds the link the scanner wrote, and resolution ignored it in
    /// favour of asking whether some filename contained the title. So these tests go through
    /// SQLite rather than mocking it — the link is the thing under test.
    /// </summary>
    public class PlayTargetResolverTests : IDisposable
    {
        private readonly string _root;

        public PlayTargetResolverTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-play-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private SqliteConnection OpenDatabase() => Database.Open(Path.Combine(_root, "movies.db"));

        private string WriteFile(string name, long size = 1)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        private static long AddMovie(SqliteConnection conn, string title, int? year) =>
            conn.QuerySingle<long>(
                "INSERT INTO movies (title, year) VALUES (@title, @year) RETURNING id",
                new { title, year });

        private static void AddFile(SqliteConnection conn, long? movieId, string path, long size = 1, string? updatedAt = null) =>
            conn.Execute(
                "INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at) " +
                "VALUES (@movieId, @path, @size, @updatedAt, @updatedAt)",
                new { movieId, path, size, updatedAt = updatedAt ?? "2024-01-01T00:00:00.0000000Z" });

        /// <summary>
        /// The bug, in the smallest shape that shows it. "It" appears inside "Spirited" — the old
        /// resolver read every path in the table, found the first stem containing the title, and
        /// opened somebody else's film.
        /// </summary>
        [Fact]
        public void A_two_letter_title_never_opens_an_unrelated_film()
        {
            using var conn = OpenDatabase();

            var it = AddMovie(conn, "It", 2017);
            var spiritedAway = AddMovie(conn, "Spirited Away", 2001);
            AddFile(conn, spiritedAway, WriteFile("Spirited Away.mkv"));

            var target = PlayTargetResolver.Resolve(conn, it, "It", 2017);

            Assert.Equal(PlayTargetKind.None, target.Kind);
            Assert.Null(target.FilePath);
        }

        [Fact]
        public void The_linked_file_is_what_plays()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);
            var linked = WriteFile("It (2017) 1080p.mkv");
            AddFile(conn, movieId, linked);

            var target = PlayTargetResolver.Resolve(conn, movieId, "It", 2017);

            Assert.Equal(PlayTargetKind.Linked, target.Kind);
            Assert.Equal(linked, target.FilePath);
        }

        /// <summary>
        /// A film linked to a file whose name says nothing about it still plays. Under the old
        /// rule the name was the only evidence there was, so a renamed file was unplayable.
        /// </summary>
        [Fact]
        public void A_link_beats_the_filename()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Amélie", 2001);
            var linked = WriteFile("disc02-final-cut.mkv");
            AddFile(conn, movieId, linked);

            var target = PlayTargetResolver.Resolve(conn, movieId, "Amélie", 2001);

            Assert.Equal(PlayTargetKind.Linked, target.Kind);
            Assert.Equal(linked, target.FilePath);
        }

        [Fact]
        public void A_linked_file_that_is_no_longer_on_disk_is_not_a_play_target()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Heat", 1995);
            AddFile(conn, movieId, Path.Combine(_root, "Heat (1995).mkv"));

            var target = PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995);

            Assert.Equal(PlayTargetKind.None, target.Kind);
        }

        [Fact]
        public void A_missing_linked_file_gives_way_to_one_that_exists()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Heat", 1995);
            AddFile(conn, movieId, Path.Combine(_root, "gone.mkv"), size: 900);
            var present = WriteFile("Heat (1995).mkv", size: 10);
            AddFile(conn, movieId, present, size: 10);

            var target = PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995);

            Assert.Equal(PlayTargetKind.Linked, target.Kind);
            Assert.Equal(present, target.FilePath);
        }

        /// <summary>
        /// Two prints of one film is the ordinary case, not an error, so it must not be a coin
        /// flip. The largest file wins: size is the closest thing the catalogue has to picture
        /// quality, and it is recorded for every row the scanner writes.
        /// </summary>
        [Fact]
        public void The_largest_linked_file_wins()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Heat", 1995);
            var small = WriteFile("Heat (1995) 720p.mkv", size: 16);
            var large = WriteFile("Heat (1995) 2160p.mkv", size: 64);
            AddFile(conn, movieId, small, size: 16);
            AddFile(conn, movieId, large, size: 64);

            Assert.Equal(large, PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995).FilePath);
        }

        [Fact]
        public void Equal_sized_prints_break_the_tie_on_the_most_recent_and_then_the_path()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Heat", 1995);
            var older = WriteFile("Heat a.mkv", size: 16);
            var newer = WriteFile("Heat b.mkv", size: 16);
            AddFile(conn, movieId, older, size: 16, updatedAt: "2024-01-01T00:00:00.0000000Z");
            AddFile(conn, movieId, newer, size: 16, updatedAt: "2025-06-01T00:00:00.0000000Z");

            Assert.Equal(newer, PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995).FilePath);
        }

        [Fact]
        public void Resolution_is_stable_when_size_and_time_are_identical()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Heat", 1995);
            var b = WriteFile("Heat b.mkv", size: 16);
            var a = WriteFile("Heat a.mkv", size: 16);
            AddFile(conn, movieId, b, size: 16);
            AddFile(conn, movieId, a, size: 16);

            Assert.Equal(a, PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995).FilePath);
        }

        /// <summary>
        /// A remake is a different film with the same title, which is exactly the case a
        /// substring match cannot see.
        /// </summary>
        [Fact]
        public void A_remake_does_not_borrow_the_originals_file()
        {
            using var conn = OpenDatabase();

            var original = AddMovie(conn, "Dune", 1984);
            var remake = AddMovie(conn, "Dune", 2021);
            AddFile(conn, original, WriteFile("Dune (1984).mkv"));

            var target = PlayTargetResolver.Resolve(conn, remake, "Dune", 2021);

            Assert.Equal(PlayTargetKind.None, target.Kind);
            Assert.Null(target.FilePath);
        }

        /// <summary>
        /// An unlinked file may still be the film — a database written before the scanner filled
        /// <c>movie_id</c> in has rows like this. It is offered, never opened.
        /// </summary>
        [Fact]
        public void An_unlinked_file_is_offered_as_a_suggestion_and_not_played()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Blade Runner 2049", 2017);
            var candidate = WriteFile("Blade Runner 2049 (2017) 2160p.mkv");
            AddFile(conn, null, candidate);

            var target = PlayTargetResolver.Resolve(conn, movieId, "Blade Runner 2049", 2017);

            Assert.Equal(PlayTargetKind.Suggested, target.Kind);
            Assert.Equal(candidate, target.FilePath);
            Assert.True(target.NeedsConfirmation);
        }

        /// <summary>
        /// A file another film already owns is by construction not this film, so it is not even
        /// worth suggesting.
        /// </summary>
        [Fact]
        public void A_file_another_film_owns_is_never_suggested()
        {
            using var conn = OpenDatabase();

            var owner = AddMovie(conn, "The Movie", 1999);
            var other = AddMovie(conn, "The Movie", 2010);
            AddFile(conn, owner, WriteFile("The Movie.mkv"));

            Assert.Equal(PlayTargetKind.None, PlayTargetResolver.Resolve(conn, other, "The Movie", 2010).Kind);
        }

        [Fact]
        public void A_suggestion_has_to_exist_on_disk_too()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Blade Runner 2049", 2017);
            AddFile(conn, null, Path.Combine(_root, "Blade Runner 2049 (2017).mkv"));

            Assert.Equal(PlayTargetKind.None, PlayTargetResolver.Resolve(conn, movieId, "Blade Runner 2049", 2017).Kind);
        }

        [Fact]
        public void Linking_a_file_by_hand_survives_reopening_the_film()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);
            var chosen = WriteFile("disc01.mkv");

            PlayTargetResolver.LinkFile(conn, movieId, chosen);

            var target = PlayTargetResolver.Resolve(conn, movieId, "It", 2017);

            Assert.Equal(PlayTargetKind.Linked, target.Kind);
            Assert.Equal(chosen, target.FilePath);
        }

        /// <summary>
        /// A person choosing a file is better evidence than a filename, so a manual link
        /// overrides an existing one rather than deferring to it the way a scan does.
        /// </summary>
        [Fact]
        public void Linking_a_file_by_hand_overrides_the_scanners_guess()
        {
            using var conn = OpenDatabase();

            var wrong = AddMovie(conn, "It", 2017);
            var right = AddMovie(conn, "It Follows", 2014);
            var path = WriteFile("It Follows (2014).mkv");
            AddFile(conn, wrong, path);

            PlayTargetResolver.LinkFile(conn, right, path);

            Assert.Equal(PlayTargetKind.Linked, PlayTargetResolver.Resolve(conn, right, "It Follows", 2014).Kind);
            Assert.Equal(PlayTargetKind.None, PlayTargetResolver.Resolve(conn, wrong, "It", 2017).Kind);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public void Linking_records_the_size_so_the_tie_break_can_use_it()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Heat", 1995);
            var path = WriteFile("Heat.mkv", size: 128);

            PlayTargetResolver.LinkFile(conn, movieId, path);

            Assert.Equal(128L, conn.QuerySingle<long>("SELECT size_bytes FROM files WHERE file_path = @path", new { path }));
        }

        /// <summary>
        /// The app hands a path to the operating system's "open this", which will run a script as
        /// readily as it plays a film. A picker filter is advisory — macOS honours it loosely and
        /// the dialog offers "All files" besides — so the refusal has to be here, where it cannot
        /// be walked around.
        /// </summary>
        [Fact]
        public void Linking_refuses_a_file_that_is_not_a_video()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);
            var script = WriteFile("evil.command");

            var ex = Assert.Throws<ArgumentException>(() => PlayTargetResolver.LinkFile(conn, movieId, script));

            Assert.Contains("not a video file", ex.Message);
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Theory]
        [InlineData("run.sh")]
        [InlineData("run.command")]
        [InlineData("setup.exe")]
        [InlineData("payload.bat")]
        [InlineData("notes.txt")]
        [InlineData("noextension")]
        public void Linking_refuses_anything_the_scanner_would_not_have_recorded(string name)
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);

            Assert.Throws<ArgumentException>(() => PlayTargetResolver.LinkFile(conn, movieId, WriteFile(name)));
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public void Linking_refuses_a_file_that_is_not_there()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);

            var ex = Assert.Throws<ArgumentException>(
                () => PlayTargetResolver.LinkFile(conn, movieId, Path.Combine(_root, "absent.mkv")));

            Assert.Contains("no longer there", ex.Message);
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public void Linking_refuses_a_blank_path()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);

            Assert.Throws<ArgumentException>(() => PlayTargetResolver.LinkFile(conn, movieId, "   "));
        }

        /// <summary>
        /// A catalogue is ordinary local state — restored from a backup, copied between machines,
        /// or written by a build predating the check above. A row naming a script is therefore
        /// still possible, and must not become something Play will open.
        /// </summary>
        [Fact]
        public void A_linked_row_naming_a_script_is_not_a_play_target()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);
            AddFile(conn, movieId, WriteFile("evil.command"));

            Assert.Equal(PlayTargetKind.None, PlayTargetResolver.Resolve(conn, movieId, "It", 2017).Kind);
        }

        [Fact]
        public void A_real_film_still_plays_when_a_script_is_linked_beside_it()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);
            AddFile(conn, movieId, WriteFile("evil.command"), size: 9_000);
            var film = WriteFile("It (2017).mkv", size: 10);
            AddFile(conn, movieId, film, size: 10);

            var target = PlayTargetResolver.Resolve(conn, movieId, "It", 2017);

            Assert.Equal(PlayTargetKind.Linked, target.Kind);
            Assert.Equal(film, target.FilePath);
        }

        [Fact]
        public void A_script_is_never_offered_as_a_suggestion_either()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "Orphan Print", null);
            AddFile(conn, null, WriteFile("Orphan Print.command"));

            Assert.Equal(PlayTargetKind.None, PlayTargetResolver.Resolve(conn, movieId, "Orphan Print", null).Kind);
        }

        [Fact]
        public void Every_extension_the_scanner_accepts_can_be_linked()
        {
            using var conn = OpenDatabase();

            foreach (var extension in ScanService.SupportedExtensions)
            {
                var movieId = AddMovie(conn, "Film" + extension.Trim('.'), 2020);
                var path = WriteFile("film" + extension);

                PlayTargetResolver.LinkFile(conn, movieId, path);

                Assert.Equal(PlayTargetKind.Linked, PlayTargetResolver.Resolve(conn, movieId, "ignored", null).Kind);
            }
        }

        [Fact]
        public void A_film_with_nothing_anywhere_resolves_to_nothing()
        {
            using var conn = OpenDatabase();

            var movieId = AddMovie(conn, "It", 2017);

            var target = PlayTargetResolver.Resolve(conn, movieId, "It", 2017);

            Assert.Equal(PlayTargetKind.None, target.Kind);
            Assert.False(target.NeedsConfirmation);
        }

        /// <summary>
        /// The scanner's own output has to resolve, or the fix would be correct and useless.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task A_scanned_library_plays_the_film_that_was_asked_for()
        {
            var films = Path.Combine(_root, "Films");
            Directory.CreateDirectory(films);
            File.WriteAllText(Path.Combine(films, "It (2017) 1080p.mkv"), "x");
            File.WriteAllText(Path.Combine(films, "Spirited Away.mkv"), "x");

            var dbPath = Path.Combine(_root, "scanned.db");
            await ScanService.ScanLibraryAsync(dbPath, new[] { films });

            using var conn = Database.Open(dbPath);

            var spirited = conn.QuerySingle<long>("SELECT id FROM movies WHERE title = 'Spirited Away'");
            var it = conn.QuerySingle<long>("SELECT id FROM movies WHERE title = 'It'");

            Assert.Equal(
                Path.Combine(films, "It (2017) 1080p.mkv"),
                PlayTargetResolver.Resolve(conn, it, "It", 2017).FilePath);

            Assert.Equal(
                Path.Combine(films, "Spirited Away.mkv"),
                PlayTargetResolver.Resolve(conn, spirited, "Spirited Away", null).FilePath);
        }
    }
}
