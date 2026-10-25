using System;
using System.IO;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What the film page is allowed to write, and when. The page shows a blank journal while the
    /// real one loads; a write made from that blank must never reach the stored review.
    /// </summary>
    public class FilmJournalEditsTests : IDisposable
    {
        private static readonly DateTime Monday = new(2026, 10, 5, 20, 0, 0, DateTimeKind.Utc);

        private readonly TempLog _log = new();
        private readonly string _dir;
        private readonly SqliteConnection _conn;

        public FilmJournalEditsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-edits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var dbPath = Path.Combine(_dir, "movies.db");
            Assert.StartsWith(Path.GetTempPath(), dbPath);
            _conn = Database.Open(dbPath);
        }

        public void Dispose()
        {
            _conn.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch { }
            _log.Dispose();
        }

        [Fact]
        public void Nothing_can_be_edited_before_the_journal_has_loaded()
        {
            Assert.False(FilmJournalEdits.CanEdit(loaded: false));
            Assert.True(FilmJournalEdits.CanEdit(loaded: true));
        }

        [Fact]
        public void A_star_clicked_before_the_journal_has_loaded_writes_nothing()
        {
            Assert.Null(FilmJournalEdits.StarClicked(loaded: false, currentStars: null, clicked: 5, reviewInBox: ""));
        }

        [Fact]
        public void A_star_clicked_once_loaded_writes_the_new_rating_with_the_review_in_the_box()
        {
            Assert.Equal(new RatingWrite(5, "Great"), FilmJournalEdits.StarClicked(true, 8, 5, " Great "));
            Assert.Equal(new RatingWrite(null, "Great"), FilmJournalEdits.StarClicked(true, 8, 8, "Great"));
        }

        [Fact]
        public void Leaving_the_review_box_before_the_journal_has_loaded_writes_nothing()
        {
            Assert.Null(FilmJournalEdits.ReviewLeft(loaded: false, currentStars: null, savedReview: "", reviewInBox: ""));
            Assert.Null(FilmJournalEdits.ReviewLeft(loaded: false, currentStars: null, savedReview: "", reviewInBox: "typed early"));
        }

        [Fact]
        public void Leaving_the_review_box_unchanged_writes_nothing()
        {
            Assert.Null(FilmJournalEdits.ReviewLeft(true, 8, "Great", " Great "));
        }

        [Fact]
        public void Leaving_an_edited_review_box_writes_the_review_with_the_current_stars()
        {
            Assert.Equal(new RatingWrite(8, "Better"), FilmJournalEdits.ReviewLeft(true, 8, "Great", "Better"));
        }

        /// <summary>
        /// The bug itself: the page had not loaded the stored journal yet, so it believed there
        /// were no stars and an empty review, and a star click upserted exactly that.
        /// </summary>
        [Fact]
        public void A_save_made_before_the_journal_has_loaded_does_not_overwrite_the_stored_review()
        {
            var film = new FilmIdentity(603, "The Matrix", 1999);
            FilmJournalStore.SaveRating(_conn, film, 9, "Still holds up", Monday);

            // What the page holds before the load arrives: the blank placeholder.
            var blank = FilmJournalEntry.Empty;
            var write = FilmJournalEdits.StarClicked(loaded: false, blank.Rating?.Stars, clicked: 3, reviewInBox: "");
            if (write is not null)
                FilmJournalStore.SaveRating(_conn, film, write.Stars, write.Review, Monday.AddDays(1));

            var stored = FilmJournalStore.GetRating(_conn, film);
            Assert.Equal(9, stored!.Stars);
            Assert.Equal("Still holds up", stored.Review);
        }
    }
}
