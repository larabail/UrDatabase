using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Where the server said you had got to, remembered locally.
    ///
    /// Cached for exactly the reason the library is: the Continue watching row is the first thing
    /// on the page, and a row that only appeared once a server had answered would be missing every
    /// time the window opened and permanently missing on a laptop away from home. Positions, and —
    /// for an episode, which nothing else caches until its series is opened — the few facts a card
    /// has to print.
    /// </summary>
    public static class JellyfinResumeCache
    {
        /// <summary>
        /// Replaces the cached row with what the server just reported, in one transaction.
        /// </summary>
        /// <remarks>
        /// Called only after a successful fetch, so a sync that could not reach the server leaves
        /// the previous row in place rather than emptying it. An empty list from a server that
        /// <em>did</em> answer is a real answer — nothing is part-watched any more — and clears it.
        /// </remarks>
        public static int Replace(SqliteConnection conn, IEnumerable<JellyfinResumeItem> items)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var list = items?.Where(i => i is not null && !string.IsNullOrWhiteSpace(i.ItemId)).ToList()
                       ?? new List<JellyfinResumeItem>();

            using var tx = conn.BeginTransaction();

            using (var clear = conn.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM jellyfin_resume";
                clear.ExecuteNonQuery();
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO jellyfin_resume (
    item_id, position_ticks, runtime_ticks, played_percentage, sort_order,
    item_type, series_id, series_name, season_number, episode_number, name, synced_at)
VALUES (
    @item, @position, @runtime, @percentage, @sort,
    @type, @series, @seriesName, @season, @episode, @name, @synced)
ON CONFLICT(item_id) DO UPDATE SET
    position_ticks    = excluded.position_ticks,
    runtime_ticks     = excluded.runtime_ticks,
    played_percentage = excluded.played_percentage,
    sort_order        = excluded.sort_order,
    item_type         = excluded.item_type,
    series_id         = excluded.series_id,
    series_name       = excluded.series_name,
    season_number     = excluded.season_number,
    episode_number    = excluded.episode_number,
    name              = excluded.name,
    synced_at         = excluded.synced_at;";

                var item = insert.Parameters.Add("@item", SqliteType.Text);
                var position = insert.Parameters.Add("@position", SqliteType.Integer);
                var runtime = insert.Parameters.Add("@runtime", SqliteType.Integer);
                var percentage = insert.Parameters.Add("@percentage", SqliteType.Real);
                var sort = insert.Parameters.Add("@sort", SqliteType.Integer);
                var type = insert.Parameters.Add("@type", SqliteType.Text);
                var series = insert.Parameters.Add("@series", SqliteType.Text);
                var seriesName = insert.Parameters.Add("@seriesName", SqliteType.Text);
                var season = insert.Parameters.Add("@season", SqliteType.Integer);
                var episode = insert.Parameters.Add("@episode", SqliteType.Integer);
                var name = insert.Parameters.Add("@name", SqliteType.Text);
                var synced = insert.Parameters.Add("@synced", SqliteType.Text);

                var now = DateTime.UtcNow.ToString("o");
                var order = 0;

                foreach (var entry in list)
                {
                    item.Value = entry.ItemId.Trim();
                    position.Value = entry.PositionTicks;
                    runtime.Value = (object?)entry.RuntimeTicks ?? DBNull.Value;
                    percentage.Value = (object?)entry.PlayedPercentage ?? DBNull.Value;
                    sort.Value = order++;
                    type.Value = entry.IsEpisode ? JellyfinResumeItem.EpisodeType : JellyfinResumeItem.MovieType;

                    // Written as null rather than as empty strings and zeroes on a film. "This is
                    // not an episode" and "this is episode nought of nothing" are different facts,
                    // and only one of them is true of a film.
                    series.Value = Text(entry.SeriesId);
                    seriesName.Value = Text(entry.SeriesName);
                    season.Value = (object?)entry.SeasonNumber ?? DBNull.Value;
                    episode.Value = (object?)entry.EpisodeNumber ?? DBNull.Value;
                    name.Value = Text(entry.Name);

                    synced.Value = now;

                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return list.Count;
        }

        private static object Text(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        /// <summary>What the last sync saw, in the order the server put it in.</summary>
        public static IReadOnlyList<JellyfinResumeItem> Load(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var items = new List<JellyfinResumeItem>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT item_id, position_ticks, runtime_ticks, played_percentage, sort_order,
       item_type, series_id, series_name, season_number, episode_number, name
FROM jellyfin_resume
ORDER BY sort_order, item_id";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new JellyfinResumeItem
                {
                    ItemId = reader.GetString(0),
                    PositionTicks = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    RuntimeTicks = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    PlayedPercentage = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    SortOrder = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),

                    // Null is a row written before television was in the row, and those were all
                    // films.
                    ItemType = reader.IsDBNull(5) ? JellyfinResumeItem.MovieType : reader.GetString(5),
                    SeriesId = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    SeriesName = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    SeasonNumber = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    EpisodeNumber = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    Name = reader.IsDBNull(10) ? "" : reader.GetString(10)
                });
            }

            return items;
        }

        /// <summary>Forgets the row. Used when Jellyfin is switched off in config.</summary>
        public static void Clear(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jellyfin_resume";
            cmd.ExecuteNonQuery();
        }
    }
}
