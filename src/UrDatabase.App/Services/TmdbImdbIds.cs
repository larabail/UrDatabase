using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// Which IMDb title a TMDB film is, as TMDB said, kept in <c>tmdb_imdb_ids</c>.
    /// </summary>
    /// <remarks>
    /// Keyed on the TMDB id rather than on a catalogue row, so one answer serves a local film and
    /// a server film alike, and a corrected match follows its new TMDB id to the right IMDb title
    /// with nothing to unlink. An empty <c>imdb_id</c> records that TMDB has none, which is an
    /// answer and is not asked for again.
    /// </remarks>
    public static partial class TmdbImdbIds
    {
        [GeneratedRegex(@"^tt\d{5,}$", RegexOptions.CultureInvariant)]
        private static partial Regex ImdbIdPattern();

        /// <summary>The id when it looks like an IMDb title id, and null otherwise.</summary>
        public static string? Normalise(string? imdbId)
        {
            var trimmed = (imdbId ?? "").Trim();
            return ImdbIdPattern().IsMatch(trimmed) ? trimmed : null;
        }

        /// <summary>
        /// What is recorded for a TMDB film: whether TMDB has been asked, and the IMDb id it gave,
        /// which is null when it had none.
        /// </summary>
        public static (bool Known, string? ImdbId) Read(SqliteConnection conn, int tmdbId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT imdb_id FROM tmdb_imdb_ids WHERE tmdb_id=@id";
            cmd.Parameters.AddWithValue("@id", tmdbId);

            var value = cmd.ExecuteScalar();
            if (value is null || value is DBNull) return (false, null);

            return (true, Normalise(value as string));
        }

        /// <summary>Records TMDB's answer, through the write lane.</summary>
        public static Task SaveAsync(SqliteConnection conn, int tmdbId, string? imdbId, CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, token => WriteAsync(conn, tmdbId, imdbId, token), ct);

        /// <summary>
        /// Records TMDB's answer on a connection that is already in the write lane.
        /// </summary>
        internal static async Task WriteAsync(SqliteConnection conn, int tmdbId, string? imdbId, CancellationToken ct)
        {
            if (tmdbId <= 0) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO tmdb_imdb_ids (tmdb_id, imdb_id, fetched_at) VALUES (@tmdb, @imdb, @fetched)
ON CONFLICT(tmdb_id) DO UPDATE SET imdb_id = excluded.imdb_id, fetched_at = excluded.fetched_at";
            cmd.Parameters.AddWithValue("@tmdb", tmdbId);
            cmd.Parameters.AddWithValue("@imdb", Normalise(imdbId) ?? "");
            cmd.Parameters.AddWithValue("@fetched", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
