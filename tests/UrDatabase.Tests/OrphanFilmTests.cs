using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Film rows with no file at all: the cards a rescan left behind once television moved out of
    /// the films, sitting in the unmatched count with nothing to play.
    ///
    /// Against real files in a temporary folder and a real SQLite catalogue beside them. Nothing
    /// here touches the real app data directory.
    /// </summary>
    public class OrphanFilmTests : IDisposable
    {
        private readonly string _root;
        private readonly string _media;
        private readonly string _dbPath;
        private readonly TempLog _log = new();

        public OrphanFilmTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-orphans-" + Guid.NewGuid().ToString("N"));
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

        // ------------------------------------------------- cause 1: an emptied row kept for nothing

        /// <summary>
        /// The reported bug. An earlier scan cleaned "Show MicroHD" down to "Show" and kept the
        /// noisy name in scan_title — automatically, with nobody involved. That used to read as
        /// "identified", so once the episode was re-filed the row stayed for ever: no file, no
        /// match, one more unmatched film.
        /// </summary>
        [Fact]
        public async Task An_episode_row_an_earlier_scan_retitled_goes_once_its_file_is_refiled()
        {
            var path = Write("Show", "Season 01", "02 - Title.mkv");
            var id = SeedFilm("Show", path);
            Exec("UPDATE movies SET scan_title = 'Show MicroHD' WHERE id = @id", new { id });

            await ScanAsync();

            Assert.Equal(0, Count("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM files WHERE episode_id IS NOT NULL AND movie_id IS NULL"));
        }

        /// <summary>
        /// The poster pass and #99's sole-candidate match identify episode rows too. That is a
        /// guess about a guess, not a person's say-so, and it does not keep a row with no file.
        /// </summary>
        [Fact]
        public async Task An_episode_row_matched_automatically_goes_once_its_file_is_refiled()
        {
            var path = Write("Show", "Season 01", "02 - Title.mkv");
            var id = SeedFilm("Show", path);
            Exec("UPDATE movies SET tmdb_id = 42, poster_path = '/p.jpg', genres = 'Drama' WHERE id = @id", new { id });

            await ScanAsync();

            Assert.Equal(0, Count("SELECT COUNT(*) FROM movies"));
        }

        [Fact]
        public async Task An_episode_row_whose_match_was_chosen_by_hand_is_kept()
        {
            var path = Write("Show", "Season 01", "02 - Title.mkv");
            var id = SeedFilm("Show", path);

            using (var conn = Database.Open(_dbPath))
                await MovieMatch.SaveAsync(conn, id, 42, "/p.jpg", "A Film Somebody Chose", byHand: true);

            await ScanAsync();

            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies WHERE id = @id AND matched_by_hand = 1", new { id }));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM files WHERE episode_id IS NOT NULL"));
        }

        [Fact]
        public async Task The_automatic_poster_match_does_not_count_as_chosen_by_hand()
        {
            var id = SeedFilm("Heat", Write("Films", "Heat (1995).mkv"));

            using (var conn = Database.Open(_dbPath))
                await MovieMatch.SaveAsync(conn, id, 949, "/p.jpg");

            Assert.Equal(0, Count("SELECT matched_by_hand FROM movies WHERE id = @id", new { id }));
        }

        // ------------------------------------------------- cause 2: rows that never had a file

        /// <summary>
        /// Before #94 a scan that parsed a cleaner name than the row it had linked inserted a new,
        /// empty row for it and left the file where it was. The poster pass then dressed that row
        /// up, so it was not even recognisably debris. The next scan clears it.
        /// </summary>
        [Fact]
        public async Task A_scan_removes_a_film_row_that_has_no_file_at_all()
        {
            var film = Write("Films", "Origen.2010.MicroHD.mkv");
            SeedFilm("Origen MicroHD", film);
            var empty = SeedFilm("Origen", path: null);
            Exec("UPDATE movies SET tmdb_id = 27205, poster_path = '/o.jpg', genres = 'Science Fiction', year = 2010 WHERE id = @empty", new { empty });

            await ScanAsync();

            Assert.Equal(0, Count("SELECT COUNT(*) FROM movies WHERE id = @empty", new { empty }));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM files WHERE movie_id IS NOT NULL"));
        }

        /// <summary>
        /// The other way to lose a row's file: somebody linked it to a different film by hand. The
        /// row they moved it away from is the scanner's guess they overruled.
        /// </summary>
        [Fact]
        public async Task A_row_whose_file_was_linked_elsewhere_by_hand_goes_at_the_next_scan()
        {
            var path = Write("Films", "Heat (1995).mkv");
            await ScanAsync();

            using (var conn = Database.Open(_dbPath))
            {
                var other = conn.ExecuteScalar<long>("INSERT INTO movies (title, year) VALUES ('Heat', 1986) RETURNING id");
                PlayTargetResolver.LinkFile(conn, other, path);
            }

            await ScanAsync();

            Assert.Equal(new[] { 1986L }, Ids("SELECT year FROM movies"));
        }

        // ------------------------------------------------- cause 3: a moved file must not fork its film

        /// <summary>
        /// A file that moved was resolved by its name alone. When that name no longer matched the
        /// row it was linked to — an old noisy title, a corrected one — a new row was inserted for
        /// it, and the file stayed linked to the old one, so the new row was born empty.
        /// </summary>
        [Fact]
        public async Task A_moved_file_does_not_create_an_empty_row_beside_its_film()
        {
            var original = Write("Old", "Origen.2010.MicroHD.mkv");
            var id = SeedFilm("Origen 2010 MicroHD", original);

            var moved = Path.Combine(_media, "New", "Origen.2010.MicroHD.mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.Move(original, moved);

            var result = await ScanAsync();

            Assert.Equal(1, result.Moved);
            Assert.Equal(0, result.MoviesAdded);
            Assert.Equal(new[] { id }, Ids("SELECT id FROM movies"));
            Assert.Equal(id, Count("SELECT movie_id FROM files WHERE file_path = @moved", new { moved }));
        }

        // ------------------------------------------------- what is never an orphan

        /// <summary>
        /// A drive that is not plugged in holds files the catalogue still names. Its films have
        /// file rows, marked missing or not, and are nobody's debris.
        /// </summary>
        [Fact]
        public async Task A_film_whose_file_is_on_an_unplugged_drive_is_kept()
        {
            var unplugged = Path.Combine(_root, "external", "Heat (1995).mkv");
            var id = SeedFilm("Heat", unplugged);
            var marked = SeedFilm("Ronin", Path.Combine(_root, "external", "Ronin (1998).mkv"));
            Exec("UPDATE files SET missing_since = '2026-01-01T00:00:00Z' WHERE movie_id = @marked", new { marked });

            var result = await ScanAsync(Path.Combine(_root, "external"));
            Assert.Single(result.SkippedRoots);

            ForgetMigration();
            using (Database.Open(_dbPath)) { }

            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies WHERE id = @id", new { id }));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies WHERE id = @marked", new { marked }));
        }

        [Theory]
        [InlineData("INSERT INTO film_ratings (film_key, title, rating, updated_at) VALUES ('title:lost film|', 'Lost Film', 7, '2026-01-01')")]
        [InlineData("INSERT INTO film_viewings (film_key, title, watched_on, created_at) VALUES ('title:lost film|', 'Lost Film', '2026-01-01', '2026-01-01')")]
        [InlineData("INSERT INTO watchlist (item_key, added_at) VALUES ('local:1', '2026-01-01')")]
        [InlineData("INSERT INTO film_problems (film_key, problems, flagged_at) VALUES ('local:1', 'audio', '2026-01-01')")]
        public async Task A_film_row_with_no_file_but_something_a_person_wrote_is_kept(string userData)
        {
            var id = SeedFilm("Lost Film", path: null);
            Assert.Equal(1, id);
            Exec(userData);

            await ScanAsync();

            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies WHERE id = 1"));
        }

        /// <summary>
        /// A rating is kept under the film's TMDB key, which a second row of the same film shares.
        /// While that row still has its file, the rating is still shown — so it is not a reason to
        /// keep the empty duplicate.
        /// </summary>
        [Fact]
        public async Task A_rating_shared_with_a_film_that_still_has_its_file_does_not_keep_the_empty_copy()
        {
            var real = SeedFilm("Origen", Write("Films", "Origen (2010).mkv"));
            var empty = SeedFilm("Origen", path: null);
            Exec("UPDATE movies SET tmdb_id = 27205, year = 2010");
            Exec("INSERT INTO film_ratings (film_key, tmdb_id, title, year, rating, updated_at) VALUES ('tmdb:27205', 27205, 'Origen', 2010, 9, '2026-01-01')");

            await ScanAsync();

            Assert.Equal(new[] { real }, Ids("SELECT id FROM movies"));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM film_ratings"));
            Assert.NotEqual(real, empty);
        }

        /// <summary>
        /// A correction made before this column existed is still somebody's choice. It wrote a
        /// TMDB id and kept the scanned name together; the automatic retitle only ever stripped
        /// release tags, so a scan_title that is not just the title plus tags was a person.
        /// </summary>
        [Fact]
        public async Task A_correction_made_before_the_marker_existed_is_still_treated_as_by_hand()
        {
            var byHand = SeedFilm("The Wrong Trousers", path: null);
            Exec("UPDATE movies SET tmdb_id = 531, scan_title = 'Wallace Gromit 2' WHERE id = @byHand", new { byHand });
            var automatic = SeedFilm("Origen", path: null);
            Exec("UPDATE movies SET tmdb_id = 27205, scan_title = 'Origen MicroHD' WHERE id = @automatic", new { automatic });

            await ScanAsync();

            Assert.Equal(new[] { byHand }, Ids("SELECT id FROM movies"));
        }

        // ------------------------------------------------- existing libraries, on upgrade

        [Fact]
        public void Opening_an_upgraded_library_clears_file_less_rows_once()
        {
            var kept = SeedFilm("Heat", Path.Combine(_root, "elsewhere", "Heat (1995).mkv"));
            var orphan = SeedFilm("Show 1x02", path: null);
            var rated = SeedFilm("Rated", path: null);
            Exec("INSERT INTO film_ratings (film_key, title, rating, updated_at) VALUES ('title:rated|', 'Rated', 6, '2026-01-01')");

            ForgetMigration();
            using (Database.Open(_dbPath)) { }

            Assert.Equal(new[] { kept, rated }, Ids("SELECT id FROM movies ORDER BY id"));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM applied_migrations WHERE name = @name", new { name = Database.RemoveFileLessFilms }));

            // Once only: a row made file-less afterwards waits for a scan, which can say why.
            var later = SeedFilm("Later", path: null);
            using (Database.Open(_dbPath)) { }
            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies WHERE id = @later", new { later }));
            Assert.NotEqual(orphan, later);
        }

        // ------------------------------------------------- explaining and removing what stays

        [Fact]
        public void A_film_with_a_file_has_no_standing_to_explain()
        {
            var id = SeedFilm("Heat", Path.Combine(_root, "elsewhere", "Heat (1995).mkv"));

            using var conn = Database.Open(_dbPath);
            Assert.Null(OrphanFilms.Describe(conn, id));
        }

        [Fact]
        public void A_kept_file_less_film_says_why_it_was_kept()
        {
            var id = SeedFilm("Lost Film", path: null);
            Exec("INSERT INTO film_ratings (film_key, title, rating, updated_at) VALUES ('title:lost film|', 'Lost Film', 7, '2026-01-01')");
            Exec("INSERT INTO watchlist (item_key, added_at) VALUES (@k, '2026-01-01')", new { k = Watchlist.LocalKey(id) });

            using var conn = Database.Open(_dbPath);
            var standing = OrphanFilms.Describe(conn, id);

            Assert.NotNull(standing);
            Assert.Equal(new[] { OrphanKeepReason.Rated, OrphanKeepReason.OnWatchlist }, standing!.Reasons);
            Assert.Contains("no file", OrphanFilms.Explain(standing), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rated it", OrphanFilms.Explain(standing));
            Assert.Contains("Watchlist", OrphanFilms.Explain(standing));
        }

        [Fact]
        public void Removing_a_file_less_film_takes_its_row_watchlist_place_and_problem_and_keeps_the_journal()
        {
            var id = SeedFilm("Lost Film", path: null);
            var local = Watchlist.LocalKey(id);
            Exec("INSERT INTO film_ratings (film_key, title, rating, updated_at) VALUES ('title:lost film|', 'Lost Film', 7, '2026-01-01')");
            Exec("INSERT INTO watchlist (item_key, added_at) VALUES (@local, '2026-01-01')", new { local });
            Exec("INSERT INTO film_problems (film_key, problems, flagged_at) VALUES (@local, 'audio', '2026-01-01')", new { local });

            using (var conn = Database.Open(_dbPath))
                Assert.True(OrphanFilms.Remove(conn, id));

            Assert.Equal(0, Count("SELECT COUNT(*) FROM movies"));
            Assert.Equal(0, Count("SELECT COUNT(*) FROM watchlist"));
            Assert.Equal(0, Count("SELECT COUNT(*) FROM film_problems"));
            Assert.Equal(1, Count("SELECT COUNT(*) FROM film_ratings"));
        }

        [Fact]
        public void A_film_that_has_a_file_cannot_be_removed()
        {
            var id = SeedFilm("Heat", Path.Combine(_root, "elsewhere", "Heat (1995).mkv"));
            Exec("UPDATE files SET missing_since = '2026-01-01T00:00:00Z'");

            using (var conn = Database.Open(_dbPath))
                Assert.False(OrphanFilms.Remove(conn, id));

            Assert.Equal(1, Count("SELECT COUNT(*) FROM movies WHERE id = @id", new { id }));
        }

        [Fact]
        public void The_confirmation_says_what_is_kept_and_what_goes()
        {
            var standing = new OrphanFilmStanding(7, new[] { OrphanKeepReason.Rated, OrphanKeepReason.ProblemReported });

            var message = OrphanFilms.ConfirmRemoval("Lost Film", standing);

            Assert.Contains("\u201cLost Film\u201d", message);
            Assert.Contains("rating", message);
            Assert.Contains("problem", message);
        }

        // ------------------------------------------------- helpers

        private async Task<UrDatabase.Models.ScanResult> ScanAsync(params string[] extraRoots)
        {
            using var conn = Database.Open(_dbPath);
            return await new ScanService().ScanAsync(conn, new[] { _media }.Concat(extraRoots));
        }

        private string Write(params string[] parts)
        {
            var path = Path.Combine(new[] { _media }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "video " + path);
            return path;
        }

        /// <summary>A film row, and a file row for it when a path is given, as an older scan wrote them.</summary>
        private long SeedFilm(string title, string? path)
        {
            using var conn = Database.Open(_dbPath);
            var id = conn.ExecuteScalar<long>("INSERT INTO movies (title) VALUES (@title) RETURNING id", new { title });
            if (path is null) return id;

            var info = new FileInfo(path);
            conn.Execute(
                "INSERT INTO files (movie_id, file_path, size_bytes, updated_at) VALUES (@id, @path, @size, @updated)",
                new
                {
                    id,
                    path,
                    size = info.Exists ? info.Length : 1234L,
                    updated = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null
                });
            return id;
        }

        /// <summary>Makes the catalogue look like one written before the one-time cleanup shipped.</summary>
        private void ForgetMigration() =>
            Exec("DELETE FROM applied_migrations WHERE name = @name", new { name = Database.RemoveFileLessFilms });

        private void Exec(string sql, object? args = null)
        {
            using var conn = Database.Open(_dbPath);
            conn.Execute(sql, args);
        }

        private long Count(string sql, object? args = null)
        {
            using var conn = Database.Open(_dbPath);
            return conn.ExecuteScalar<long>(sql, args);
        }

        private long[] Ids(string sql)
        {
            using var conn = Database.Open(_dbPath);
            return conn.Query<long>(sql).ToArray();
        }
    }
}
