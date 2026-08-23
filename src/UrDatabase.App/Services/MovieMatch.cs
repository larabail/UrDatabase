using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// Which TMDB film a catalogued movie is, as recorded in <c>movies.tmdb_id</c>.
    ///
    /// Before this existed the answer was re-derived from the title on every open, so a film TMDB
    /// searched badly was described by the wrong record every single time and there was nothing a
    /// person could do about it. Storing the identification makes it correctable: the picker
    /// writes here, and everything that describes the film reads from here first.
    /// </summary>
    public static class MovieMatch
    {
        /// <summary>
        /// The TMDB film this movie has been identified as, or null when nothing has identified it
        /// yet. Null is also what a catalogue written before the column existed returns, which is
        /// the same thing as far as any caller is concerned.
        /// </summary>
        public static int? ReadTmdbId(SqliteConnection conn, long movieId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT tmdb_id FROM movies WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", movieId);

            var value = cmd.ExecuteScalar();
            if (value is null || value is DBNull) return null;

            var id = Convert.ToInt64(value);
            return id > 0 && id <= int.MaxValue ? (int)id : null;
        }

        /// <summary>
        /// The name to store for a film somebody has just identified, or null when there is nothing
        /// worth writing.
        /// </summary>
        /// <remarks>
        /// A rename is refused when TMDB offered no name at all, and skipped when the catalogue
        /// already agrees — so correcting a film whose title was right all along, which is the
        /// ordinary case when only the poster was wrong, does not touch the title column or
        /// pointlessly fill in <c>scan_title</c>.
        ///
        /// Case and spacing count as a difference. "el drama" and "El Drama" are the same film and
        /// the same key to every matching rule in the app, but they are not equally good to read,
        /// and taking TMDB's spelling is the point of having asked.
        /// </remarks>
        public static string? RenameTo(string? currentTitle, string? tmdbTitle)
        {
            var wanted = (tmdbTitle ?? "").Trim();
            if (wanted.Length == 0) return null;

            return string.Equals(wanted, (currentTitle ?? "").Trim(), StringComparison.Ordinal) ? null : wanted;
        }

        /// <summary>
        /// Records the film and its artwork together, because they are one answer: writing a
        /// poster without the id it came from is how the catalogue ended up holding artwork it
        /// could not explain or replace.
        /// </summary>
        /// <param name="posterPath">
        /// A cached file or a TMDB URL. Null leaves whatever is already there — the automatic
        /// loader has nothing to write when TMDB has no artwork, and blanking the column in that
        /// case would throw away a poster somebody had chosen by hand.
        /// </param>
        /// <param name="title">
        /// What TMDB calls the film, when a person chose it. Null leaves the catalogued name
        /// alone, which is what the automatic loader wants: it identifies films by their title in
        /// the first place, so writing that title back would say nothing and would overwrite a
        /// correction made by hand with a guess.
        /// </param>
        public static Task SaveAsync(
            SqliteConnection conn,
            long movieId,
            int tmdbId,
            string? posterPath,
            string? title = null,
            CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, async token =>
            {
                var columns = new List<string> { "tmdb_id=@tmdb" };
                if (posterPath is not null) columns.Add("poster_path=@poster");

                if (title is not null)
                {
                    // The scanned name is preserved on the way past, and only when there is not one
                    // already: renaming twice must keep the name the scanner actually parsed, not
                    // the previous correction. COALESCE also backfills a row catalogued before the
                    // column existed, whose scan_title is null but whose title is still the scan's.
                    columns.Add("scan_title=COALESCE(scan_title, title)");
                    columns.Add("title=@title");
                }

                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"UPDATE movies SET {string.Join(", ", columns)} WHERE id=@id";

                cmd.Parameters.AddWithValue("@tmdb", tmdbId);
                cmd.Parameters.AddWithValue("@id", movieId);
                if (posterPath is not null) cmd.Parameters.AddWithValue("@poster", posterPath);
                if (title is not null) cmd.Parameters.AddWithValue("@title", title);

                // ConfigureAwait(false) as everywhere else on this path: the lane is entered from
                // a UI event handler, and an uncontended lane never yields, so without this the
                // continuation is posted back to a UI thread that may be waiting on the result.
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                // Only when a name was actually discarded. A rename is the moment the catalogue
                // gains a discarded name, so it is the moment to notice that some empty row is
                // already sitting under it — that row can only be this film catalogued a second
                // time by something that did not know to look for the alias, and it is now
                // provably redundant, because the row that owns the name is the one just
                // corrected.
                //
                // The poster loader comes through here too, with no title, several times a second
                // on a fresh library. Reading the whole catalogue for each of those would be a
                // sweep looking for debris that by definition cannot have appeared.
                //
                // Inside the lane and before it is given back, so the rename and the tidying are
                // one write rather than two a reader can catch between.
                if (title is not null) await SweepDiscardedAsync(conn, token).ConfigureAwait(false);
            }, ct);

        /// <summary>
        /// Best effort, deliberately. Correcting a film's match is the user's action and it has
        /// already succeeded by the time this runs; failing it over a tidy-up would report a
        /// correction as broken when the thing they asked for is written down and correct.
        /// </summary>
        private static async Task SweepDiscardedAsync(SqliteConnection conn, CancellationToken ct)
        {
            try
            {
                await DiscardedNames.SweepAsync(conn, tx: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The next completed scan sweeps too, so nothing is lost by giving up here — but
                // a sweep that always fails changes nothing visible and would leave no trace.
                AppLog.Write("app.log", $"could not sweep discarded names: {ex.Message}");
            }
        }
    }
}
