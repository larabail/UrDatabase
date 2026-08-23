using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the owner has taken out of their own Continue watching row, on disk.
    /// </summary>
    /// <remarks>
    /// In the catalogue rather than in configuration, and in a table of its own rather than beside
    /// the cached row. Both halves of that matter.
    ///
    /// It is local state about a specific library, which is what this database is for, and putting
    /// it in <c>appsettings.json</c> would have meant a list that grows without bound in a file a
    /// person is expected to read — and one more thing for the two explicit allowlists in
    /// <see cref="ConfigStore"/> and <c>SetupChoices</c> to silently erase.
    ///
    /// A separate table because <see cref="JellyfinResumeCache.Replace"/> deletes every row of
    /// <c>jellyfin_resume</c> and writes the server's answer back: a dismissal stored there would
    /// last until the next sync and no longer, which is minutes.
    ///
    /// Nothing here is ever sent to Jellyfin. Marking something unplayed on the server would hide
    /// it in every client in the house and throw away the position — this hides one thing in one
    /// app and leaves the position exactly where it was.
    /// </remarks>
    public static class ResumeDismissalStore
    {
        /// <summary>
        /// Takes one item out of the row, at the position it is at now.
        /// </summary>
        /// <remarks>
        /// Upserts rather than inserting, because dismissing something twice is a thing a person
        /// can do — it came back when the position moved, and they have dismissed it again — and
        /// the second dismissal is about the new position.
        /// </remarks>
        public static void Dismiss(SqliteConnection conn, string itemId, long positionTicks)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(itemId)) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO jellyfin_resume_dismissals (item_id, position_ticks, dismissed_at)
VALUES (@item, @position, @at)
ON CONFLICT(item_id) DO UPDATE SET
    position_ticks = excluded.position_ticks,
    dismissed_at   = excluded.dismissed_at;";

            cmd.Parameters.AddWithValue("@item", itemId.Trim());
            cmd.Parameters.AddWithValue("@position", positionTicks);
            cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));

            cmd.ExecuteNonQuery();
        }

        /// <summary>Undoes one dismissal, putting the item straight back in the row.</summary>
        public static void Restore(SqliteConnection conn, string itemId)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(itemId)) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jellyfin_resume_dismissals WHERE item_id = @item";
            cmd.Parameters.AddWithValue("@item", itemId.Trim());
            cmd.ExecuteNonQuery();
        }

        /// <summary>Everything currently dismissed.</summary>
        public static IReadOnlyList<ResumeDismissal> Load(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var dismissals = new List<ResumeDismissal>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT item_id, position_ticks FROM jellyfin_resume_dismissals";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                dismissals.Add(new ResumeDismissal(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1)));
            }

            return dismissals;
        }

        /// <summary>
        /// Forgets the dismissals that no longer hide anything, given what the server just said.
        /// Returns how many were dropped.
        /// </summary>
        /// <remarks>
        /// This is what stops the table becoming a blacklist that grows forever. It is only safe
        /// to call with a list the server genuinely answered with — see
        /// <see cref="ResumeDismissals.Stale"/> — because a failed fetch looks exactly like a
        /// server that has nothing part-watched at all.
        /// </remarks>
        public static int Prune(SqliteConnection conn, IEnumerable<JellyfinResumeItem>? resume)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var stale = ResumeDismissals.Stale(Load(conn), resume);
            if (stale.Count == 0) return 0;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jellyfin_resume_dismissals WHERE item_id = @item";
            var item = cmd.Parameters.Add("@item", SqliteType.Text);

            foreach (var dismissal in stale)
            {
                item.Value = dismissal.ItemId.Trim();
                cmd.ExecuteNonQuery();
            }

            return stale.Count;
        }

        /// <summary>Forgets every dismissal. Used when a server is disconnected.</summary>
        public static void Clear(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM jellyfin_resume_dismissals";
            cmd.ExecuteNonQuery();
        }
    }
}
