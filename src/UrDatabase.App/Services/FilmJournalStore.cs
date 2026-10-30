using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The owner's ratings and viewings, on disk in the catalogue.
    /// </summary>
    /// <remarks>
    /// Local only. UrActor keeps the same things — a ten-star rating, a review, a calendar of what
    /// was watched when and with whom — but in its own Firebase project behind a personal login,
    /// and writing there means writing into friends' data too. That sync is deliberately not done
    /// here; the tables are shaped like UrActor's (ten points, TMDB ids, yyyy-MM-dd days) so it can
    /// be added without converting anything.
    ///
    /// Rows are found under both of a film's keys — see <see cref="FilmIdentity"/> — and moved
    /// onto the TMDB key by <see cref="Rekey"/> before every write, so a rating given before a film
    /// was identified is neither lost nor duplicated once it is.
    /// </remarks>
    public static class FilmJournalStore
    {
        public const int MinStars = 1;
        public const int MaxStars = 10;

        private const string DateFormat = "yyyy-MM-dd";

        /// <summary>
        /// What clicking a star does: the same star again takes the rating away, any other sets it.
        /// </summary>
        public static int? Toggle(int? current, int clicked)
        {
            if (clicked < MinStars || clicked > MaxStars)
                throw new ArgumentOutOfRangeException(nameof(clicked), clicked, "A rating is one to ten.");

            return current == clicked ? null : clicked;
        }

        public static FilmRating? GetRating(SqliteConnection conn, FilmIdentity film)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (film?.Key is not string key) return null;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT film_key, tmdb_id, title, year, rating, review, updated_at
FROM film_ratings
WHERE film_key = @key OR film_key = @titleKey
ORDER BY (film_key = @key) DESC, updated_at DESC
LIMIT 1";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@titleKey", (object?)film.TitleKey ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new FilmRating
            {
                Key = reader.GetString(0),
                TmdbId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                Title = reader.GetString(2),
                Year = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Stars = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Review = reader.IsDBNull(5) ? "" : reader.GetString(5),
                UpdatedAtUtc = ParseInstant(reader.GetString(6))
            };
        }

        /// <summary>
        /// Writes the rating and review together. With no stars and no review there is nothing to
        /// keep, so the row is deleted rather than left behind empty.
        /// </summary>
        public static void SaveRating(SqliteConnection conn, FilmIdentity film, int? stars, string? review, DateTime nowUtc)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (film?.Key is not string key) throw new ArgumentException("The film has no title to file it under.", nameof(film));
            if (stars is not null && (stars < MinStars || stars > MaxStars))
                throw new ArgumentOutOfRangeException(nameof(stars), stars, "A rating is one to ten.");

            var text = string.IsNullOrWhiteSpace(review) ? null : review.Trim();

            using var tx = conn.BeginTransaction();
            Rekey(conn, tx, film);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            if (stars is null && text is null)
            {
                cmd.CommandText = "DELETE FROM film_ratings WHERE film_key = @key";
                cmd.Parameters.AddWithValue("@key", key);
            }
            else
            {
                cmd.CommandText = @"
INSERT INTO film_ratings (film_key, tmdb_id, title, year, rating, review, updated_at)
VALUES (@key, @tmdb, @title, @year, @rating, @review, @at)
ON CONFLICT(film_key) DO UPDATE SET
    tmdb_id    = excluded.tmdb_id,
    title      = excluded.title,
    year       = excluded.year,
    rating     = excluded.rating,
    review     = excluded.review,
    updated_at = excluded.updated_at;";
                cmd.Parameters.AddWithValue("@key", key);
                cmd.Parameters.AddWithValue("@tmdb", (object?)film.TmdbId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@title", film.Title);
                cmd.Parameters.AddWithValue("@year", (object?)film.Year ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rating", (object?)stars ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@review", (object?)text ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@at", FormatInstant(nowUtc));
            }

            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        /// <summary>Every viewing of the film, most recent day first.</summary>
        public static IReadOnlyList<FilmViewing> LoadViewings(SqliteConnection conn, FilmIdentity film)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var viewings = new List<FilmViewing>();
            if (film?.Key is not string key) return viewings;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, film_key, watched_on, companions, note
FROM film_viewings
WHERE film_key = @key OR film_key = @titleKey
ORDER BY watched_on DESC, id DESC";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@titleKey", (object?)film.TitleKey ?? DBNull.Value);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // A row whose day cannot be read is skipped rather than thrown: it is somebody's
                // hand edit, and the rest of the list is still worth showing.
                if (!TryParseDay(reader.GetString(2), out var day)) continue;

                viewings.Add(new FilmViewing
                {
                    Id = reader.GetInt64(0),
                    Key = reader.GetString(1),
                    WatchedOn = day,
                    Companions = Companions.FromJson(reader.IsDBNull(3) ? null : reader.GetString(3)),
                    Note = reader.IsDBNull(4) ? "" : reader.GetString(4)
                });
            }

            return viewings;
        }

        /// <summary>Records one viewing and returns its id.</summary>
        public static long AddViewing(
            SqliteConnection conn,
            FilmIdentity film,
            DateOnly watchedOn,
            IEnumerable<string?>? companions,
            string? note,
            DateTime nowUtc)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (film?.Key is not string key) throw new ArgumentException("The film has no title to file it under.", nameof(film));

            var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

            using var tx = conn.BeginTransaction();
            Rekey(conn, tx, film);

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO film_viewings (film_key, tmdb_id, title, year, watched_on, companions, note, created_at)
VALUES (@key, @tmdb, @title, @year, @day, @companions, @note, @at);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@tmdb", (object?)film.TmdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", film.Title);
            cmd.Parameters.AddWithValue("@year", (object?)film.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@day", FormatDay(watchedOn));
            cmd.Parameters.AddWithValue("@companions", (object?)Companions.ToJson(companions) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@note", (object?)text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@at", FormatInstant(nowUtc));

            var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            tx.Commit();
            return id;
        }

        /// <summary>Forgets one viewing. False when there was no such row.</summary>
        public static bool RemoveViewing(SqliteConnection conn, long id)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM film_viewings WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }

        /// <summary>Everybody the owner has logged watching anything with, most often first.</summary>
        public static IReadOnlyList<string> KnownCompanions(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var lists = new List<IReadOnlyList<string>>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT companions FROM film_viewings
WHERE companions IS NOT NULL
ORDER BY watched_on DESC, id DESC";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                lists.Add(Companions.FromJson(reader.GetString(0)));

            return Companions.Suggestions(lists);
        }

        /// <summary>Everything the film page needs, in one read.</summary>
        public static FilmJournalEntry Load(SqliteConnection conn, FilmIdentity film) => new()
        {
            Rating = GetRating(conn, film),
            Viewings = LoadViewings(conn, film),
            KnownCompanions = KnownCompanions(conn)
        };

        /// <summary>
        /// Moves rows written under the film's title key onto its TMDB key, once it has one.
        /// </summary>
        /// <remarks>
        /// Viewings simply move: two lists of evenings are one longer list. Ratings cannot both
        /// survive, so when there is one under each key the more recently changed one is kept —
        /// that is the opinion the owner last expressed.
        /// </remarks>
        public static void Rekey(SqliteConnection conn, FilmIdentity film)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            using var tx = conn.BeginTransaction();
            Rekey(conn, tx, film);
            tx.Commit();
        }

        private static void Rekey(SqliteConnection conn, SqliteTransaction tx, FilmIdentity film)
        {
            if (film?.Key is not string key || film.TitleKey is not string titleKey) return;
            Move(conn, tx, titleKey, key, film.TmdbId);
        }

        /// <summary>Keeps title-keyed notes reachable when a scan cleans an unidentified film's name.</summary>
        internal static void RenameUnidentified(
            SqliteConnection conn, SqliteTransaction tx,
            string? oldTitle, int? oldYear, string? newTitle, int? newYear)
        {
            var oldKey = FilmKey.ForTitle(oldTitle, oldYear)
                ?? throw new ArgumentException("The previous film title is required.", nameof(oldTitle));
            var newKey = FilmKey.ForTitle(newTitle, newYear)
                ?? throw new ArgumentException("The cleaned film title is required.", nameof(newTitle));
            Move(conn, tx, oldKey, newKey, tmdbId: null);
        }

        /// <summary>
        /// Moves the rows of a film the page knew under another title onto its TMDB key.
        /// </summary>
        /// <remarks>
        /// <see cref="Rekey"/> only finds rows under the title the film has now. Identifying a film
        /// through <b>Wrong film?</b> also renames it to TMDB's spelling — "Amelie" becomes
        /// "Amélie" — and the rows saved a minute earlier are under the old one, which nothing
        /// would ever produce again. The page knows both names at that moment, so it says which.
        ///
        /// Only ever from a title key, and only onto a TMDB key. A film that moves from one TMDB id
        /// to another without anybody saying so is a re-match between two real films, either of
        /// which may be in the library in its own right, so its history is not dragged across;
        /// the owner's own correction goes through <see cref="Correct"/> instead.
        /// </remarks>
        public static void Adopt(SqliteConnection conn, string? fromKey, FilmIdentity film)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (film?.Key is not string key || film.TmdbId is null) return;
            if (string.IsNullOrEmpty(fromKey) || !fromKey.StartsWith(FilmKey.TitlePrefix, StringComparison.Ordinal)) return;

            using var tx = conn.BeginTransaction();
            Move(conn, tx, fromKey, key, film.TmdbId);
            Rekey(conn, tx, film);
            tx.Commit();
        }

        /// <summary>
        /// Moves everything recorded against one key onto a film the owner has just said it
        /// really is — the <b>Wrong film?</b> correction, and nothing else.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="Adopt"/> this also moves from a TMDB key. A film mis-matched to A
        /// and corrected to B was B all along, so the evenings logged against A were B's: left
        /// where they were, A's page would show the owner's history with a film they may never
        /// have seen. Only an explicit correction is trusted to say that; an automatic re-match
        /// never is. When B already has a rating, the more recently changed one wins.
        /// </remarks>
        public static void Correct(SqliteConnection conn, string? fromKey, FilmIdentity film)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (film?.Key is not string key || string.IsNullOrEmpty(fromKey)) return;

            using var tx = conn.BeginTransaction();
            Move(conn, tx, fromKey, key, film.TmdbId);
            Rekey(conn, tx, film);
            tx.Commit();
        }

        private static void Move(SqliteConnection conn, SqliteTransaction tx, string titleKey, string key, int? tmdbId)
        {
            if (string.Equals(key, titleKey, StringComparison.Ordinal)) return;

            using (var move = conn.CreateCommand())
            {
                move.Transaction = tx;
                move.CommandText = "UPDATE film_viewings SET film_key = @key, tmdb_id = @tmdb WHERE film_key = @titleKey";
                move.Parameters.AddWithValue("@key", key);
                move.Parameters.AddWithValue("@tmdb", (object?)tmdbId ?? DBNull.Value);
                move.Parameters.AddWithValue("@titleKey", titleKey);
                move.ExecuteNonQuery();
            }

            string? keyed = UpdatedAt(conn, tx, key);
            string? titled = UpdatedAt(conn, tx, titleKey);
            if (titled is null) return;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@titleKey", titleKey);
            cmd.Parameters.AddWithValue("@tmdb", (object?)tmdbId ?? DBNull.Value);

            if (keyed is null || string.CompareOrdinal(titled, keyed) > 0)
            {
                cmd.CommandText = @"
DELETE FROM film_ratings WHERE film_key = @key;
UPDATE film_ratings SET film_key = @key, tmdb_id = @tmdb WHERE film_key = @titleKey;";
            }
            else
            {
                cmd.CommandText = "DELETE FROM film_ratings WHERE film_key = @titleKey";
            }

            cmd.ExecuteNonQuery();
        }

        private static string? UpdatedAt(SqliteConnection conn, SqliteTransaction tx, string key)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT updated_at FROM film_ratings WHERE film_key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() as string;
        }

        /// <summary>Formats a day for the database. Invariant, so a locale cannot change what is written.</summary>
        public static string FormatDay(DateOnly day) => day.ToString(DateFormat, CultureInfo.InvariantCulture);

        /// <summary>
        /// Whether a day could really be one the owner watched something on: not in the future —
        /// the date box can be typed into, and a mistyped year is the usual way to get there — and
        /// not before films existed.
        /// </summary>
        public static bool IsPlausibleDay(DateOnly day, DateOnly today) =>
            day <= today && day.Year >= 1888;

        public static bool TryParseDay(string? text, out DateOnly day) =>
            DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

        private static string FormatInstant(DateTime utc) =>
            (utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime()).ToString("o", CultureInfo.InvariantCulture);

        private static DateTime ParseInstant(string text) =>
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                ? at.ToUniversalTime()
                : DateTime.MinValue;
    }
}
