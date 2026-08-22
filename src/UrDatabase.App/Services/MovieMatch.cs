using System;
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
        /// Records the film and its artwork together, because they are one answer: writing a
        /// poster without the id it came from is how the catalogue ended up holding artwork it
        /// could not explain or replace.
        /// </summary>
        /// <param name="posterPath">
        /// A cached file or a TMDB URL. Null leaves whatever is already there — the automatic
        /// loader has nothing to write when TMDB has no artwork, and blanking the column in that
        /// case would throw away a poster somebody had chosen by hand.
        /// </param>
        public static Task SaveAsync(SqliteConnection conn, long movieId, int tmdbId, string? posterPath, CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, async token =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = posterPath is null
                    ? "UPDATE movies SET tmdb_id=@tmdb WHERE id=@id"
                    : "UPDATE movies SET tmdb_id=@tmdb, poster_path=@poster WHERE id=@id";

                cmd.Parameters.AddWithValue("@tmdb", tmdbId);
                cmd.Parameters.AddWithValue("@id", movieId);
                if (posterPath is not null) cmd.Parameters.AddWithValue("@poster", posterPath);

                // ConfigureAwait(false) as everywhere else on this path: the lane is entered from
                // a UI event handler, and an uncontended lane never yields, so without this the
                // continuation is posted back to a UI thread that may be waiting on the result.
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, ct);
    }
}
