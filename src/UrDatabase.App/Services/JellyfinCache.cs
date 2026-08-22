using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The server's library, remembered locally. Without this the window would be empty until the
    /// server answered, and would stay empty for good on a laptop that is out of the house — which
    /// is precisely when someone wants to look at what they own.
    ///
    /// Metadata only. No film is ever copied here.
    /// </summary>
    public static class JellyfinCache
    {
        /// <summary>
        /// Replaces the cached library with what the server just reported, in one transaction, so
        /// a sync that fails part way leaves the previous library intact rather than a half of it.
        /// Rows the server no longer lists are dropped: a film deleted upstairs should not linger
        /// as an item that cannot play.
        /// </summary>
        public static int Replace(SqliteConnection conn, IEnumerable<JellyfinMovie> movies)
        {
            var list = movies?.Where(m => !string.IsNullOrWhiteSpace(m.ItemId)).ToList() ?? new List<JellyfinMovie>();

            using var tx = conn.BeginTransaction();

            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM jellyfin_movies";
                clear.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO jellyfin_movies
    (item_id, title, year, genres, overview, runtime_minutes, community_rating, imdb_id, tmdb_id, image_tag, synced_at)
VALUES
    (@item, @title, @year, @genres, @overview, @runtime, @rating, @imdb, @tmdb, @tag, @synced)
ON CONFLICT(item_id) DO UPDATE SET
    title            = excluded.title,
    year             = excluded.year,
    genres           = excluded.genres,
    overview         = excluded.overview,
    runtime_minutes  = excluded.runtime_minutes,
    community_rating = excluded.community_rating,
    imdb_id          = excluded.imdb_id,
    tmdb_id          = excluded.tmdb_id,
    image_tag        = excluded.image_tag,
    synced_at        = excluded.synced_at;";

                var item = insert.Parameters.Add("@item", SqliteType.Text);
                var title = insert.Parameters.Add("@title", SqliteType.Text);
                var year = insert.Parameters.Add("@year", SqliteType.Integer);
                var genres = insert.Parameters.Add("@genres", SqliteType.Text);
                var overview = insert.Parameters.Add("@overview", SqliteType.Text);
                var runtime = insert.Parameters.Add("@runtime", SqliteType.Integer);
                var rating = insert.Parameters.Add("@rating", SqliteType.Real);
                var imdb = insert.Parameters.Add("@imdb", SqliteType.Text);
                var tmdb = insert.Parameters.Add("@tmdb", SqliteType.Text);
                var tag = insert.Parameters.Add("@tag", SqliteType.Text);
                var synced = insert.Parameters.Add("@synced", SqliteType.Text);

                var now = DateTime.UtcNow.ToString("o");

                foreach (var movie in list)
                {
                    item.Value = movie.ItemId;
                    title.Value = movie.Title ?? "";
                    year.Value = (object?)movie.Year ?? DBNull.Value;
                    genres.Value = movie.Genres ?? "";
                    overview.Value = movie.Overview ?? "";
                    runtime.Value = (object?)movie.RuntimeMinutes ?? DBNull.Value;
                    rating.Value = (object?)movie.CommunityRating ?? DBNull.Value;
                    imdb.Value = (object?)movie.ImdbId ?? DBNull.Value;
                    tmdb.Value = (object?)movie.TmdbId ?? DBNull.Value;
                    tag.Value = (object?)movie.ImageTag ?? DBNull.Value;
                    synced.Value = now;

                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return list.Count;
        }

        /// <summary>What the last sync saw, newest first, exactly as the grouped view wants it.</summary>
        public static IReadOnlyList<JellyfinMovie> Load(SqliteConnection conn)
        {
            var movies = new List<JellyfinMovie>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT item_id, title, year, genres, overview, runtime_minutes, community_rating, imdb_id, tmdb_id, image_tag
FROM jellyfin_movies
ORDER BY COALESCE(year, 0) DESC, title";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                movies.Add(new JellyfinMovie
                {
                    ItemId = reader.GetString(0),
                    Title = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Year = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    Genres = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Overview = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    RuntimeMinutes = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    CommunityRating = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    ImdbId = reader.IsDBNull(7) ? null : reader.GetString(7),
                    TmdbId = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ImageTag = reader.IsDBNull(9) ? null : reader.GetString(9)
                });
            }

            return movies;
        }

        /// <summary>When the cache was last written, or null when nothing has ever synced.</summary>
        public static DateTime? LastSyncedUtc(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MAX(synced_at) FROM jellyfin_movies";

            var value = cmd.ExecuteScalar();
            if (value is not string text || string.IsNullOrWhiteSpace(text)) return null;

            return DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : null;
        }

        /// <summary>Forgets the server's library. Used when Jellyfin is switched off in config.</summary>
        public static void Clear(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jellyfin_movies";
            cmd.ExecuteNonQuery();
        }
    }
}
