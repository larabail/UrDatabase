using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// IMDb ratings with a SQLite-backed cache in front of the lookup. The upstream free tier is
    /// 1,000 requests a day shared across every user of the app, so each answer — including "this
    /// title has no rating" — is remembered and never requested twice.
    /// </summary>
    public sealed class ImdbRatingService : IDisposable
    {
        private readonly IImdbRatingLookup _lookup;
        private readonly bool _ownsLookup;

        public ImdbRatingService(IImdbRatingLookup lookup, bool ownsLookup = false)
        {
            _lookup = lookup;
            _ownsLookup = ownsLookup;
        }

        public bool IsConfigured => _lookup.IsAvailable;

        public async Task<double?> GetRatingAsync(SqliteConnection conn, string? imdbId, long? movieId = null, CancellationToken ct = default)
        {
            // No IMDb id means no exact match is possible, so the lookup is skipped entirely
            // rather than guessed at from the title.
            if (string.IsNullOrWhiteSpace(imdbId)) return null;

            if (TryReadCache(conn, imdbId, out var cached)) return cached;

            // Unavailable means the feature is off: no request, and no substituting another
            // service's number under IMDb's name.
            if (!_lookup.IsAvailable) return null;

            var rating = await _lookup.LookupAsync(imdbId, ct);
            WriteCache(conn, imdbId, movieId, rating);
            return rating;
        }

        private static bool TryReadCache(SqliteConnection conn, string imdbId, out double? rating)
        {
            rating = null;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT rating FROM imdb_ratings WHERE imdb_id = @id";
                cmd.Parameters.AddWithValue("@id", imdbId);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return false;

                // A row with a NULL rating means "asked already, there is none".
                rating = reader.IsDBNull(0) ? null : reader.GetDouble(0);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"rating cache read failed for {imdbId}: {ex.Message}");
                return false;
            }
        }

        private static void WriteCache(SqliteConnection conn, string imdbId, long? movieId, double? rating)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO imdb_ratings (imdb_id, movie_id, rating, fetched_at, source)
VALUES (@id, @movie, @rating, @fetched, 'omdb')
ON CONFLICT(imdb_id) DO UPDATE SET
    movie_id   = COALESCE(excluded.movie_id, imdb_ratings.movie_id),
    rating     = excluded.rating,
    fetched_at = excluded.fetched_at,
    source     = excluded.source;";
                cmd.Parameters.AddWithValue("@id", imdbId);
                cmd.Parameters.AddWithValue("@movie", (object?)movieId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rating", (object?)rating ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fetched", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLog.Write("omdb.log", $"rating cache write failed for {imdbId}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_ownsLookup && _lookup is IDisposable disposable) disposable.Dispose();
        }
    }
}
