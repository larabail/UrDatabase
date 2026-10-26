using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// How much of OMDb's daily allowance this install has spent, kept in the catalogue's
    /// <c>omdb_usage</c> table so a restart does not reset the count.
    /// </summary>
    /// <remarks>
    /// OMDb's free tier is 1,000 requests a day. Every request is counted — the ones the details
    /// screen makes as well as the background backfill's — so the backfill can stop at its own
    /// share and leave the rest for somebody opening films. Days are UTC: OMDb does not say when
    /// its day turns over, and a fixed boundary is easier to reason about than a guessed one.
    /// </remarks>
    public static class OmdbUsage
    {
        /// <summary>The day a moment falls on, as the table keys it.</summary>
        public static string DayOf(DateTime utcNow) =>
            utcNow.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>What has been spent on one day. Zero and false for a day with no row.</summary>
        public static (int Requests, bool LimitReached) Read(SqliteConnection conn, string day)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT requests, limit_reached FROM omdb_usage WHERE day=@day";
            cmd.Parameters.AddWithValue("@day", day);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (0, false);

            return (reader.GetInt32(0), !reader.IsDBNull(1) && reader.GetInt64(1) != 0);
        }

        /// <summary>Counts one request against the day, and notes when OMDb said the limit is reached.</summary>
        public static Task RecordAsync(SqliteConnection conn, string day, bool limitReached, CancellationToken ct = default) =>
            DatabaseWriteLane.RunAsync(conn, async token =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO omdb_usage (day, requests, limit_reached) VALUES (@day, 1, @limit)
ON CONFLICT(day) DO UPDATE SET
    requests      = omdb_usage.requests + 1,
    limit_reached = MAX(omdb_usage.limit_reached, excluded.limit_reached)";
                cmd.Parameters.AddWithValue("@day", day);
                cmd.Parameters.AddWithValue("@limit", limitReached ? 1 : 0);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, ct);
    }
}
