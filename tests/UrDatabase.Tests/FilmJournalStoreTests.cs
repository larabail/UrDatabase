using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Ratings and viewings against a real SQLite file in a temporary directory.
    /// </summary>
    public class FilmJournalStoreTests : IDisposable
    {
        private static readonly DateTime Monday = new(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Tuesday = Monday.AddDays(1);

        private readonly TempLog _log = new();
        private readonly string _dir;
        private readonly string _dbPath;
        private readonly SqliteConnection _conn;

        public FilmJournalStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-journal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _dbPath = Path.Combine(_dir, "movies.db");
            Assert.StartsWith(Path.GetTempPath(), _dbPath);

            _conn = Database.Open(_dbPath);
        }

        public void Dispose()
        {
            _conn.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
            _log.Dispose();
        }

        private static FilmIdentity Matrix(int? tmdb = 603) => new(tmdb, "The Matrix", 1999);

        [Fact]
        public void A_film_never_rated_has_no_rating()
        {
            Assert.Null(FilmJournalStore.GetRating(_conn, Matrix()));
        }

        [Fact]
        public void A_rating_and_review_are_saved_and_read_back()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(), 9, "  Still holds up.  ", Monday);

            var rating = FilmJournalStore.GetRating(_conn, Matrix());

            Assert.NotNull(rating);
            Assert.Equal(9, rating!.Stars);
            Assert.Equal("Still holds up.", rating.Review);
            Assert.Equal("tmdb:603", rating.Key);
            Assert.Equal(603, rating.TmdbId);
            Assert.Equal(Monday, rating.UpdatedAtUtc);
        }

        [Fact]
        public void Saving_again_replaces_the_rating_rather_than_adding_one()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(), 9, null, Monday);
            FilmJournalStore.SaveRating(_conn, Matrix(), 7, "Less than I remembered", Tuesday);

            Assert.Equal(7, FilmJournalStore.GetRating(_conn, Matrix())!.Stars);
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM film_ratings"));
        }

        [Fact]
        public void A_review_with_no_stars_is_kept()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(), null, "Need to see it again", Monday);

            var rating = FilmJournalStore.GetRating(_conn, Matrix());
            Assert.Null(rating!.Stars);
            Assert.Equal("Need to see it again", rating.Review);
        }

        [Fact]
        public void Taking_away_both_stars_and_review_deletes_the_row()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(), 9, "Great", Monday);
            FilmJournalStore.SaveRating(_conn, Matrix(), null, "   ", Tuesday);

            Assert.Null(FilmJournalStore.GetRating(_conn, Matrix()));
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM film_ratings"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        [InlineData(-1)]
        public void A_rating_outside_one_to_ten_is_refused(int stars)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                FilmJournalStore.SaveRating(_conn, Matrix(), stars, null, Monday));
            Assert.Null(FilmJournalStore.GetRating(_conn, Matrix()));
        }

        [Fact]
        public void The_database_itself_refuses_a_rating_outside_one_to_ten()
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO film_ratings (film_key, title, rating, updated_at) VALUES ('x', 'X', 11, 'now')";

            Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        }

        [Fact]
        public void A_film_with_no_title_and_no_id_cannot_be_rated()
        {
            Assert.Throws<ArgumentException>(() =>
                FilmJournalStore.SaveRating(_conn, new FilmIdentity(null, " ", null), 5, null, Monday));
        }

        [Theory]
        [InlineData(null, 7, 7)]
        [InlineData(7, 7, null)]
        [InlineData(7, 3, 3)]
        public void Clicking_the_current_star_takes_the_rating_away(int? current, int clicked, int? expected)
        {
            Assert.Equal(expected, FilmJournalStore.Toggle(current, clicked));
        }

        [Fact]
        public void Clicking_a_star_that_does_not_exist_is_refused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FilmJournalStore.Toggle(null, 11));
        }

        [Theory]
        [InlineData("2026-10-24", true)]
        [InlineData("2026-10-25", false)]
        [InlineData("1888-01-01", true)]
        [InlineData("1887-12-31", false)]
        public void Only_a_day_that_has_already_happened_is_believable(string day, bool expected)
        {
            var today = new DateOnly(2026, 10, 24);
            Assert.Equal(expected, FilmJournalStore.IsPlausibleDay(DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture), today));
        }

        [Fact]
        public void Viewings_come_back_newest_day_first_with_their_companions_and_note()
        {
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2024, 1, 2), new[] { "Ana" }, null, Monday);
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2026, 10, 3), new[] { "Ana", "Luis", "ana" }, " Cinema ", Monday);
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2019, 6, 1), null, null, Monday);

            var viewings = FilmJournalStore.LoadViewings(_conn, Matrix());

            Assert.Equal(
                new[] { new DateOnly(2026, 10, 3), new DateOnly(2024, 1, 2), new DateOnly(2019, 6, 1) },
                viewings.Select(v => v.WatchedOn));
            Assert.Equal(new[] { "Ana", "Luis" }, viewings[0].Companions);
            Assert.Equal("Cinema", viewings[0].Note);
            Assert.Empty(viewings[2].Companions);
            Assert.Equal("", viewings[2].Note);
        }

        [Fact]
        public void The_day_is_stored_as_a_plain_calendar_date()
        {
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2026, 10, 3), null, null, Monday);

            Assert.Equal("2026-10-03", Scalar("SELECT watched_on FROM film_viewings"));
        }

        [Fact]
        public void A_viewing_can_be_removed_without_touching_the_others()
        {
            var first = FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2024, 1, 2), null, null, Monday);
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2025, 1, 2), null, null, Monday);

            Assert.True(FilmJournalStore.RemoveViewing(_conn, first));
            Assert.False(FilmJournalStore.RemoveViewing(_conn, first));

            Assert.Equal(new DateOnly(2025, 1, 2), Assert.Single(FilmJournalStore.LoadViewings(_conn, Matrix())).WatchedOn);
        }

        [Fact]
        public void Another_films_viewings_are_not_mixed_in()
        {
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2024, 1, 2), null, null, Monday);
            FilmJournalStore.AddViewing(_conn, new FilmIdentity(11, "Star Wars", 1977), new DateOnly(2024, 1, 3), null, null, Monday);

            Assert.Single(FilmJournalStore.LoadViewings(_conn, Matrix()));
        }

        [Fact]
        public void Known_companions_span_every_film_most_frequent_first()
        {
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2024, 1, 2), new[] { "Luis" }, null, Monday);
            FilmJournalStore.AddViewing(_conn, new FilmIdentity(11, "Star Wars", 1977), new DateOnly(2024, 1, 3), new[] { "Ana", "Luis" }, null, Monday);
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2024, 1, 4), null, null, Monday);

            Assert.Equal(new[] { "Luis", "Ana" }, FilmJournalStore.KnownCompanions(_conn));
        }

        [Fact]
        public void A_rating_given_before_the_film_was_identified_follows_it_onto_its_tmdb_id()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(tmdb: null), 8, "Before", Monday);
            FilmJournalStore.AddViewing(_conn, Matrix(tmdb: null), new DateOnly(2020, 1, 1), new[] { "Ana" }, null, Monday);

            // Read through the identified film without writing anything: still found.
            Assert.Equal(8, FilmJournalStore.GetRating(_conn, Matrix())!.Stars);
            Assert.Single(FilmJournalStore.LoadViewings(_conn, Matrix()));

            // The next write moves both onto the TMDB key.
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2026, 1, 1), null, null, Tuesday);

            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM film_ratings WHERE film_key LIKE 'title:%'"));
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM film_viewings WHERE film_key LIKE 'title:%'"));
            Assert.Equal("tmdb:603", FilmJournalStore.GetRating(_conn, Matrix())!.Key);
            Assert.Equal(2, FilmJournalStore.LoadViewings(_conn, Matrix()).Count);
        }

        [Fact]
        public void When_both_keys_hold_a_rating_the_most_recent_opinion_wins()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(), 5, "Old", Monday);
            FilmJournalStore.SaveRating(_conn, Matrix(tmdb: null), 9, "New", Tuesday);

            FilmJournalStore.Rekey(_conn, Matrix());

            var rating = FilmJournalStore.GetRating(_conn, Matrix());
            Assert.Equal(9, rating!.Stars);
            Assert.Equal("tmdb:603", rating.Key);
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM film_ratings"));
        }

        [Fact]
        public void When_the_tmdb_rating_is_newer_the_title_one_is_dropped()
        {
            FilmJournalStore.SaveRating(_conn, Matrix(tmdb: null), 5, "Old", Monday);

            // Inserted directly, so the newer tmdb row exists before any rekey has run.
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO film_ratings (film_key, tmdb_id, title, year, rating, updated_at)
                                    VALUES ('tmdb:603', 603, 'The Matrix', 1999, 9, @at)";
                cmd.Parameters.AddWithValue("@at", Tuesday.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            FilmJournalStore.Rekey(_conn, Matrix());

            Assert.Equal(9, FilmJournalStore.GetRating(_conn, Matrix())!.Stars);
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM film_ratings"));
        }

        [Fact]
        public void Identifying_a_film_under_a_new_spelling_adopts_what_was_saved_under_the_old_one()
        {
            var before = new FilmIdentity(null, "Amelie", 2001);
            var after = new FilmIdentity(194, "Amélie", 2001);

            FilmJournalStore.SaveRating(_conn, before, 9, "Paris", Monday);
            FilmJournalStore.AddViewing(_conn, before, new DateOnly(2020, 2, 14), new[] { "Ana" }, null, Monday);

            // The renamed film's own keys do not reach the old rows on their own.
            Assert.Null(FilmJournalStore.GetRating(_conn, after));

            FilmJournalStore.Adopt(_conn, before.Key, after);

            var rating = FilmJournalStore.GetRating(_conn, after);
            Assert.Equal(9, rating!.Stars);
            Assert.Equal("tmdb:194", rating.Key);
            Assert.Equal("tmdb:194", Assert.Single(FilmJournalStore.LoadViewings(_conn, after)).Key);
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM film_ratings WHERE film_key LIKE 'title:%'"));
        }

        [Fact]
        public void A_film_moved_from_one_tmdb_id_to_another_does_not_take_its_history_along()
        {
            var wrong = new FilmIdentity(1, "Solaris", 2002);
            FilmJournalStore.SaveRating(_conn, wrong, 4, null, Monday);

            FilmJournalStore.Adopt(_conn, wrong.Key, new FilmIdentity(593, "Solaris", 1972));

            Assert.Equal(4, FilmJournalStore.GetRating(_conn, wrong)!.Stars);
            Assert.Null(FilmJournalStore.GetRating(_conn, new FilmIdentity(593, "Solaris", 1972)));
        }

        [Fact]
        public void Adopting_onto_a_film_that_is_still_unidentified_does_nothing()
        {
            var before = new FilmIdentity(null, "Amelie", 2001);
            FilmJournalStore.SaveRating(_conn, before, 9, null, Monday);

            FilmJournalStore.Adopt(_conn, before.Key, new FilmIdentity(null, "Amélie", 2001));

            Assert.Equal("title:amelie|2001", FilmJournalStore.GetRating(_conn, before)!.Key);
        }

        [Fact]
        public void Correcting_a_mismatched_film_moves_its_history_off_the_wrong_tmdb_id()
        {
            var wrong = new FilmIdentity(1, "Solaris", 2002);
            var right = new FilmIdentity(593, "Solaris", 1972);

            FilmJournalStore.SaveRating(_conn, wrong, 8, "Slow, wonderful", Monday);
            FilmJournalStore.AddViewing(_conn, wrong, new DateOnly(2025, 3, 1), new[] { "Ana" }, null, Monday);

            FilmJournalStore.Correct(_conn, wrong.Key, right);

            Assert.Null(FilmJournalStore.GetRating(_conn, wrong));
            Assert.Empty(FilmJournalStore.LoadViewings(_conn, wrong));

            var rating = FilmJournalStore.GetRating(_conn, right);
            Assert.Equal(8, rating!.Stars);
            Assert.Equal("Slow, wonderful", rating.Review);
            Assert.Equal(593, rating.TmdbId);
            Assert.Equal("tmdb:593", Assert.Single(FilmJournalStore.LoadViewings(_conn, right)).Key);
        }

        [Fact]
        public void Correcting_onto_a_film_already_rated_keeps_the_newer_rating_and_both_sets_of_viewings()
        {
            var wrong = new FilmIdentity(1, "Solaris", 2002);
            var right = new FilmIdentity(593, "Solaris", 1972);

            FilmJournalStore.SaveRating(_conn, right, 6, "Old opinion", Monday);
            FilmJournalStore.AddViewing(_conn, right, new DateOnly(2010, 1, 1), null, null, Monday);
            FilmJournalStore.SaveRating(_conn, wrong, 9, "New opinion", Tuesday);
            FilmJournalStore.AddViewing(_conn, wrong, new DateOnly(2025, 3, 1), null, null, Tuesday);

            FilmJournalStore.Correct(_conn, wrong.Key, right);

            Assert.Equal("New opinion", FilmJournalStore.GetRating(_conn, right)!.Review);
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM film_ratings"));
            Assert.Equal(2, FilmJournalStore.LoadViewings(_conn, right).Count);
        }

        [Fact]
        public void Correcting_an_unidentified_film_moves_its_title_keyed_history_too()
        {
            var before = new FilmIdentity(null, "Amelie", 2001);
            var after = new FilmIdentity(194, "Amélie", 2001);
            FilmJournalStore.SaveRating(_conn, before, 9, null, Monday);

            FilmJournalStore.Correct(_conn, before.Key, after);

            Assert.Equal("tmdb:194", FilmJournalStore.GetRating(_conn, after)!.Key);
        }

        [Fact]
        public void A_rating_outlives_the_catalogue_row_it_was_made_against()
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO movies (title, year, tmdb_id) VALUES ('The Matrix', 1999, 603)";
                cmd.ExecuteNonQuery();
            }

            FilmJournalStore.SaveRating(_conn, Matrix(), 10, null, Monday);
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2026, 1, 1), null, null, Monday);

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM movies";
                cmd.ExecuteNonQuery();
            }

            Assert.Equal(10, FilmJournalStore.GetRating(_conn, Matrix())!.Stars);
            Assert.Single(FilmJournalStore.LoadViewings(_conn, Matrix()));
        }

        [Fact]
        public void A_hand_edited_day_that_cannot_be_read_is_skipped_not_thrown()
        {
            FilmJournalStore.AddViewing(_conn, Matrix(), new DateOnly(2026, 1, 1), null, null, Monday);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO film_viewings (film_key, title, watched_on, created_at)
                                    VALUES ('tmdb:603', 'The Matrix', 'last tuesday', 'now')";
                cmd.ExecuteNonQuery();
            }

            Assert.Single(FilmJournalStore.LoadViewings(_conn, Matrix()));
        }

        [Fact]
        public async Task The_async_journal_writes_and_reads_through_the_path_it_was_given()
        {
            var journal = new FilmJournal(_dbPath, () => Monday);

            await journal.SaveRatingAsync(Matrix(), 8, "Good");
            var id = await journal.AddViewingAsync(Matrix(), new DateOnly(2026, 10, 3), new[] { "Ana" }, null);

            var entry = await journal.LoadAsync(Matrix());

            Assert.Equal(8, entry.Rating!.Stars);
            Assert.Equal(id, Assert.Single(entry.Viewings).Id);
            Assert.Equal(new[] { "Ana" }, entry.KnownCompanions);

            Assert.True(await journal.RemoveViewingAsync(id));
            Assert.Empty((await journal.LoadAsync(Matrix())).Viewings);

            await journal.SaveRatingAsync(Matrix(tmdb: null), 6, null);
            await journal.AdoptAsync(Matrix(tmdb: null).Key, Matrix());
            Assert.Equal("tmdb:603", (await journal.LoadAsync(Matrix())).Rating!.Key);
        }

        [Fact]
        public async Task A_film_that_cannot_be_named_loads_as_empty()
        {
            var journal = new FilmJournal(_dbPath);

            Assert.Same(FilmJournalEntry.Empty, await journal.LoadAsync(new FilmIdentity(null, "", null)));
        }

        private object? Scalar(string sql)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
    }
}
