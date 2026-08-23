using System;
using System.Collections.Generic;
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
    /// The row a rename leaves behind, and getting rid of it.
    ///
    /// Correcting a film's TMDB match renames the row and keeps the scanned name in
    /// <c>movies.scan_title</c> so the next scan finds the row it already made. That works from
    /// the build that introduced it onwards — and a catalogue is a file on disk that older builds,
    /// and builds on other branches, go on opening. Anything scanning it without knowing to look
    /// for the alias catalogues the film a second time under the name on disk, and because a scan
    /// leaves an existing <c>files.movie_id</c> alone, that second row never gets a file. What is
    /// left is a blank card that cannot be opened, played, matched or removed.
    ///
    /// Nothing in the app could clear one up before this, which is why a real library had two.
    /// </summary>
    public class DiscardedNameTests : IDisposable
    {
        private readonly string _root;

        public DiscardedNameTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-discarded-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- the rule ---------------------------------------------------------------------------

        [Fact]
        public void An_empty_row_under_a_name_another_row_discarded_is_debris()
        {
            var rows = new[]
            {
                Row(2, "The Drama", 2026, scanTitle: "El Drama", hasFiles: true, hasTmdbId: true),
                Row(6, "El Drama", 2026)
            };

            Assert.Equal(new[] { 6L }, DiscardedNames.Find(rows));
        }

        [Fact]
        public void The_name_is_matched_the_way_the_scanner_matches_names()
        {
            // The debris is named from a filename and the alias from whatever the parser made of
            // one, so the two agree on case, accents, punctuation and "&" only if this uses the
            // scanner's own key. Matching literally would leave the row exactly where it was.
            var rows = new[]
            {
                Row(1, "The Mandalorian and Grogu", 2026, scanTitle: "Star.Wars.The.Mandalorian.and.Grogu", hasFiles: true, hasTmdbId: true),
                Row(2, "Star Wars The Mandalorian and Grogu", 2026),
                Row(3, "Amélie", 2001, scanTitle: "Amelie", hasFiles: true, hasTmdbId: true),
                Row(4, "AMELIE", 2001)
            };

            Assert.Equal(new[] { 2L, 4L }, DiscardedNames.Find(rows));
        }

        [Fact]
        public void A_year_that_disagrees_is_a_different_film()
        {
            // Two films share a title far too often for the name alone to be enough — a remake is
            // the ordinary case, not the exotic one.
            var rows = new[]
            {
                Row(1, "Dune", 2021, scanTitle: "Dune", hasFiles: true, hasTmdbId: true),
                Row(2, "Dune", 1984)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Theory]
        // Each of these is a reason to leave the row alone, and each costs only a row staying.
        [InlineData(true, false, false, false, null)]   // a file is linked to it
        [InlineData(false, true, false, false, null)]   // something identified it
        [InlineData(false, false, true, false, null)]   // it has artwork
        [InlineData(false, false, false, true, null)]   // it has genres
        [InlineData(false, false, false, false, "Something Else")] // it has been renamed itself
        public void A_row_that_holds_anything_at_all_is_kept(
            bool hasFiles, bool hasTmdbId, bool hasPoster, bool hasGenres, string? ownScanTitle)
        {
            var rows = new[]
            {
                Row(1, "The Drama", 2026, scanTitle: "El Drama", hasFiles: true, hasTmdbId: true),
                Row(2, "El Drama", 2026, scanTitle: ownScanTitle, hasFiles: hasFiles,
                    hasTmdbId: hasTmdbId, hasPoster: hasPoster, hasGenres: hasGenres)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void The_row_that_owns_the_name_is_never_the_one_removed()
        {
            // A correction that changed only the case leaves a row whose title and scan_title are
            // the same film. It is one row, it owns its own discarded name, and removing it would
            // delete the film outright.
            var rows = new[] { Row(1, "El Drama", 2026, scanTitle: "el drama") };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void A_catalogue_nobody_has_renamed_anything_in_is_left_entirely_alone()
        {
            var rows = new[]
            {
                Row(1, "The Matrix", 1999, hasFiles: true),
                Row(2, "Heat", 1995),
                Row(3, "Heat", 1995)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void A_name_that_normalises_to_nothing_claims_no_other_row()
        {
            // "+++.mkv" leaves nothing to key on. Treating that as a name would make every such
            // row the alias owner of every other one.
            var rows = new[]
            {
                Row(1, "Real Film", 2020, scanTitle: "+++", hasFiles: true),
                Row(2, "???", 2020)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        // ---- against a real catalogue -------------------------------------------------------------

        [Fact]
        public async Task A_scan_clears_out_the_row_an_older_build_left_behind()
        {
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);

            // Exactly what a build with no alias in its index does with the same file: it fails to
            // find the renamed row and inserts a second one, which never gets the file because the
            // upsert leaves an existing movie_id alone.
            var debris = LeaveDebris(conn, "El Drama", 2026);
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));

            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(new[] { real }, conn.Query<long>("SELECT id FROM movies").ToList());
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE id = @id", new { id = debris }));

            // The film it was a duplicate of is untouched, correction and file included.
            Assert.Equal("The Drama", conn.QuerySingle<string>("SELECT title FROM movies"));
            Assert.Equal(1325734, conn.QuerySingle<int>("SELECT tmdb_id FROM movies"));
            Assert.Equal(real, conn.QuerySingle<long>("SELECT movie_id FROM files"));
        }

        [Fact]
        public async Task Correcting_a_match_clears_out_a_row_already_sitting_under_the_old_name()
        {
            // The other half: a rename is the moment a discarded name appears, so it is the moment
            // to notice something empty already answering to it — without waiting for a scan.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            var debris = LeaveDebris(conn, "El Drama", 2026);

            await MovieMatch.SaveAsync(conn, real, tmdbId: 1325734, posterPath: null, title: "The Drama");

            Assert.Equal(new[] { real }, conn.Query<long>("SELECT id FROM movies").ToList());
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE id = @id", new { id = debris }));
        }

        [Fact]
        public async Task The_search_index_forgets_the_row_too()
        {
            // The FTS index is kept by triggers rather than by anything this writes, so the only
            // way to know they fired is to search. A row that survived here would be a film you
            // could still type back into existence and then not be able to open.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            LeaveDebris(conn, "El Drama", 2026);

            Assert.Equal(2, Search(conn, "drama*").Count);

            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(new[] { "The Drama" }, Search(conn, "drama*"));

            // Read the index on its own. Joining movies hides exactly the failure this is looking
            // for: a rowid left in movies_fts for a row that is no longer in movies.
            Assert.Equal(
                conn.Query<long>("SELECT id FROM movies ORDER BY id").ToList(),
                conn.Query<long>("SELECT rowid FROM movies_fts ORDER BY rowid").ToList());
            Assert.Empty(conn.Query<long>("SELECT rowid FROM movies_fts WHERE movies_fts MATCH 'el'"));
        }

        [Fact]
        public async Task A_cached_rating_survives_the_row_being_removed()
        {
            // imdb_ratings.movie_id is ON DELETE SET NULL, so the answer OMDb already gave is kept
            // under its IMDb id rather than being fetched again.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            var debris = LeaveDebris(conn, "El Drama", 2026);

            conn.Execute(
                "INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at) VALUES ('tt1', @id, 7.5, 'now')",
                new { id = debris });

            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(7.5, conn.QuerySingle<double>("SELECT rating FROM imdb_ratings WHERE imdb_id = 'tt1'"));
            Assert.Null(conn.QuerySingleOrDefault<long?>("SELECT movie_id FROM imdb_ratings WHERE imdb_id = 'tt1'"));
        }

        [Fact]
        public async Task A_cancelled_scan_sweeps_nothing()
        {
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            LeaveDebris(conn, "El Drama", 2026);

            using var cancelled = new System.Threading.CancellationTokenSource();
            cancelled.Cancel();
            var result = await new ScanService().ScanAsync(conn, new[] { films }, null, cancelled.Token);

            Assert.Equal(ScanStatus.Cancelled, result.Status);
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
        }

        [Fact]
        public async Task A_scan_of_an_ordinary_library_removes_nothing()
        {
            // The whole suite is worthless if the sweep can touch a film somebody has. Two films,
            // one of them corrected, and a re-scan has to leave both exactly where they are.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");
            Write(films, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var drama = conn.QuerySingle<long>("SELECT id FROM movies WHERE title = 'El Drama'");
            Correct(conn, drama, "The Drama", tmdbId: 1325734);

            var before = conn.Query<(long Id, string Title)>("SELECT id AS Id, title AS Title FROM movies ORDER BY id").ToList();
            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(before, conn.Query<(long Id, string Title)>("SELECT id AS Id, title AS Title FROM movies ORDER BY id").ToList());
            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public async Task The_swept_row_does_not_come_back_on_the_next_scan()
        {
            // It was created by a scan in the first place, so a sweep the scanner then undoes
            // would be a library that flickers rather than one that is fixed. The alias is what
            // stops it: the file resolves to the row that owns the name.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            LeaveDebris(conn, "El Drama", 2026);

            await new ScanService().ScanAsync(conn, new[] { films });
            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(new[] { real }, conn.Query<long>("SELECT id FROM movies").ToList());
        }

        [Fact]
        public async Task Recording_a_poster_alone_sweeps_nothing()
        {
            // The poster loader writes through the same method, with no title, several times a
            // second on a fresh library. It discards no name, so it has nothing to look for — and
            // reading the whole catalogue each time would be a real cost for a certain answer.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            var debris = LeaveDebris(conn, "El Drama", 2026);

            await MovieMatch.SaveAsync(conn, real, tmdbId: 1325734, posterPath: "/tmp/p.jpg");

            Assert.Equal(2L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE id = @id", new { id = debris }));
        }

        [Fact]
        public async Task A_film_whose_file_is_missing_is_not_debris()
        {
            // The overlap with retiring a film whose file has gone, and the dangerous one. Such a
            // film is out of the library and its row holds a corrected match that has to outlive
            // the file — so it looks empty from the wall and is anything but. A files row is a
            // files row whether or not a scan can still find what it names.
            var films = MakeFolder("Films");
            var path = Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies");
            Correct(conn, real, "The Drama", tmdbId: 1325734);

            // A second row for the same film, and this one really is debris.
            var debris = LeaveDebris(conn, "El Drama", 2026);

            File.Delete(path);
            var second = await new ScanService().ScanAsync(conn, new[] { films });
            Assert.Equal(1, second.Missing);

            // The real row survives with its file row, its mark and its correction; only the
            // empty duplicate goes.
            Assert.Equal(new[] { real }, conn.Query<long>("SELECT id FROM movies").ToList());
            Assert.Equal(0L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE id = @id", new { id = debris }));
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files WHERE movie_id = @id", new { id = real }));
            Assert.NotNull(conn.QuerySingle<string?>("SELECT missing_since FROM files"));
            Assert.Equal(1325734, conn.QuerySingle<int>("SELECT tmdb_id FROM movies"));
        }

        [Fact]
        public void A_row_whose_only_file_is_marked_missing_still_counts_as_having_one()
        {
            // The same guard at the level of the rule, stated directly: HasFiles asks whether the
            // catalogue names a file, not whether a scan could find it.
            var rows = new[]
            {
                Row(1, "The Drama", 2026, scanTitle: "El Drama", hasFiles: true, hasTmdbId: true),
                Row(2, "El Drama", 2026, hasFiles: true)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void The_row_that_owns_a_name_is_kept_even_when_it_holds_nothing_else()
        {
            // A chain: A used to be called "El Drama", B used to be called "The Drama". Neither is
            // debris, because a former name of its own is the mark of a film somebody corrected.
            var rows = new[]
            {
                Row(1, "The Drama", 2026, scanTitle: "El Drama"),
                Row(2, "Something", 2026, scanTitle: "The Drama")
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        // ---- the guards that keep a real film ---------------------------------------------------

        [Fact]
        public void A_name_two_rows_have_both_discarded_identifies_neither()
        {
            // Ambiguity is a reason to do nothing. Two films that were each called "Heat" before
            // being corrected say nothing about which of them an empty "Heat" row belongs to.
            var rows = new[]
            {
                Row(1, "Heat", 1995, scanTitle: "Fire", hasFiles: true, hasTmdbId: true),
                Row(2, "Blaze", 1995, scanTitle: "Fire", hasFiles: true, hasTmdbId: true),
                Row(3, "Fire", 1995)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void A_row_older_than_the_rename_is_not_something_the_rename_caused()
        {
            // The debris is inserted by a scan that ran after the correction, so it always has the
            // higher id. An older row under the same name is a film that was already there.
            var rows = new[]
            {
                Row(1, "El Drama", 2026),
                Row(2, "The Drama", 2026, scanTitle: "El Drama", hasFiles: true, hasTmdbId: true)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void A_row_that_discarded_a_name_but_has_no_file_authorises_nothing()
        {
            // The whole mechanism is that the file stayed on the original row and the duplicate
            // never got one. An owner with no file is not that story, so it is not this bug.
            var rows = new[]
            {
                Row(1, "The Drama", 2026, scanTitle: "El Drama", hasTmdbId: true),
                Row(2, "El Drama", 2026)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public void A_row_that_discarded_a_name_without_being_identified_authorises_nothing()
        {
            // Only a TMDB correction discards a name, and a correction writes the id with it. A
            // scan_title with no tmdb_id was not written by anything this app does.
            var rows = new[]
            {
                Row(1, "The Drama", 2026, scanTitle: "El Drama", hasFiles: true),
                Row(2, "El Drama", 2026)
            };

            Assert.Empty(DiscardedNames.Find(rows));
        }

        [Fact]
        public async Task A_scan_that_could_not_record_a_file_sweeps_nothing()
        {
            // The failure that makes an ordinary row look like debris: EnsureMovieAsync commits
            // the movie row and the file that belonged on it never lands, so a perfectly real film
            // is momentarily indistinguishable from a duplicate. A scan that hit one has no
            // business concluding anything about an empty row.
            //
            // Forced with a trigger because no filename can do it: the parser is written never to
            // return an empty title. What is being simulated is any error while recording a file,
            // which is what counts.Failed exists to report.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies WHERE title = 'El Drama'");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            LeaveDebris(conn, "El Drama", 2026);

            conn.Execute(
                @"CREATE TRIGGER refuse_the_file BEFORE INSERT ON files
                  WHEN new.file_path LIKE '%Unrecordable%'
                  BEGIN SELECT RAISE(ABORT, 'refused'); END;");

            Write(films, "Unrecordable Film (2019).mkv");
            var failed = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(ScanStatus.Completed, failed.Status);
            Assert.Equal(1, failed.Failed);
            Assert.Equal(3L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));

            // Once the scan is clean again, the sweep happens as usual.
            conn.Execute("DROP TRIGGER refuse_the_file");
            File.Delete(Path.Combine(films, "Unrecordable Film (2019).mkv"));

            var clean = await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(0, clean.Failed);
            Assert.DoesNotContain("El Drama", conn.Query<string>("SELECT title FROM movies").ToList());
            Assert.Contains("The Drama", conn.Query<string>("SELECT title FROM movies").ToList());
        }

        [Fact]
        public async Task A_file_linked_between_the_look_and_the_delete_saves_the_row()
        {
            // The read and the delete are two statements over a catalogue with more than one
            // writer, and not every writer takes the lane. The delete therefore re-asks, so a row
            // that gained a file in between stays.
            var films = MakeFolder("Films");
            Write(films, "El Drama (2026).mkv");
            var other = Write(films, "Heat (1995).mkv");

            using var conn = Database.Open(DbPath);
            await new ScanService().ScanAsync(conn, new[] { films });

            var real = conn.QuerySingle<long>("SELECT id FROM movies WHERE title = 'El Drama'");
            Correct(conn, real, "The Drama", tmdbId: 1325734);
            var debris = LeaveDebris(conn, "El Drama", 2026);

            // Find still says the row is debris, because that is what the catalogue said when it
            // was read. The guard on the delete is the only thing standing between that answer
            // and a file attached to nothing.
            PlayTargetResolver.LinkFile(conn, debris, other);

            using var tx = conn.BeginTransaction();
            var swept = await conn.ExecuteAsync(
                @"DELETE FROM movies
                  WHERE id = @id
                    AND tmdb_id IS NULL AND scan_title IS NULL
                    AND (poster_path IS NULL OR TRIM(poster_path) = '')
                    AND (genres IS NULL OR TRIM(genres) = '')
                    AND NOT EXISTS (SELECT 1 FROM files WHERE files.movie_id = movies.id)",
                new { id = debris }, tx);
            tx.Commit();

            Assert.Equal(0, swept);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE id = @id", new { id = debris }));

            // And the real sweep agrees, through its own path.
            await new ScanService().ScanAsync(conn, new[] { films });
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE id = @id", new { id = debris }));
        }

        // ---- helpers ------------------------------------------------------------------------------

        private string DbPath => Path.Combine(_root, "movies.db");

        private static CatalogueName Row(
            long id,
            string title,
            int? year,
            string? scanTitle = null,
            bool hasFiles = false,
            bool hasTmdbId = false,
            bool hasPoster = false,
            bool hasGenres = false) =>
            new CatalogueName
            {
                Id = id,
                Title = title,
                Year = year,
                ScanTitle = scanTitle,
                HasFiles = hasFiles,
                HasTmdbId = hasTmdbId,
                HasPoster = hasPoster,
                HasGenres = hasGenres
            };

        /// <summary>What correcting a match writes: the new title, the id, and the scanned name.</summary>
        private static void Correct(Microsoft.Data.Sqlite.SqliteConnection conn, long id, string title, int tmdbId) =>
            conn.Execute(
                "UPDATE movies SET title = @title, tmdb_id = @tmdbId, scan_title = COALESCE(scan_title, title) WHERE id = @id",
                new { id, title, tmdbId });

        /// <summary>
        /// The row an older build leaves: the film catalogued again under the name on disk, with
        /// no file, because the upsert leaves an existing <c>movie_id</c> alone.
        /// </summary>
        private static long LeaveDebris(Microsoft.Data.Sqlite.SqliteConnection conn, string title, int? year) =>
            conn.ExecuteScalar<long>(
                "INSERT INTO movies (title, year) VALUES (@title, @year) RETURNING id",
                new { title, year });

        private static List<string> Search(Microsoft.Data.Sqlite.SqliteConnection conn, string match) =>
            conn.Query<string>(
                "SELECT m.title FROM movies_fts f JOIN movies m ON m.id = f.rowid WHERE movies_fts MATCH @match ORDER BY m.id",
                new { match }).ToList();

        private string MakeFolder(string relative)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Write(string folder, string name, string body = "x")
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, body);
            return path;
        }
    }
}
