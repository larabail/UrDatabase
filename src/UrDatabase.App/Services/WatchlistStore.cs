using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// The watchlist, on disk.
    /// </summary>
    /// <remarks>
    /// In the catalogue, in a table of its own, for the reasons <see cref="ResumeDismissalStore"/>
    /// gives: it is local state about this library, it grows with use, and no sync or scan may
    /// replace it. Not a column on <c>movies</c>, because a film that only the server holds has
    /// no row there to carry one.
    ///
    /// Nothing here is sent to Jellyfin. Its nearest equivalent is a favourite, which is shared
    /// with every client in the house and means something else.
    /// </remarks>
    public static class WatchlistStore
    {
        /// <summary>
        /// Puts a film on the watchlist. <paramref name="keys"/> is every key it answers to — see
        /// <see cref="Watchlist.KeysFor(Models.UiMovie)"/> — and the first is the one written.
        /// </summary>
        /// <remarks>
        /// A film already on the list under any of its keys is left alone, so adding it twice
        /// neither duplicates it nor moves it to the front of the shelf.
        /// </remarks>
        public static void Add(SqliteConnection conn, IReadOnlyList<string>? keys, DateTime? now = null)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var clean = Clean(keys);
            if (clean.Count == 0) return;
            if (ContainsAny(conn, clean)) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO watchlist (item_key, added_at) VALUES (@key, @at)";
            cmd.Parameters.AddWithValue("@key", clean[0]);
            cmd.Parameters.AddWithValue("@at", (now ?? DateTime.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        /// <summary>Takes a film off the watchlist, under every key it answers to.</summary>
        public static void Remove(SqliteConnection conn, IEnumerable<string>? keys)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var clean = Clean(keys);
            if (clean.Count == 0) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM watchlist WHERE item_key = @key COLLATE NOCASE";
            var key = cmd.Parameters.Add("@key", SqliteType.Text);

            foreach (var k in clean)
            {
                key.Value = k;
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Adds or removes, whichever <paramref name="onWatchlist"/> asks for.</summary>
        public static void Set(SqliteConnection conn, IReadOnlyList<string>? keys, bool onWatchlist)
        {
            if (onWatchlist) Add(conn, keys);
            else Remove(conn, keys);
        }

        /// <summary>Everything on the watchlist.</summary>
        public static IReadOnlyList<WatchlistEntry> Load(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var entries = new List<WatchlistEntry>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT item_key, added_at FROM watchlist";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var at = DateTime.TryParse(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed)
                    ? parsed.ToUniversalTime()
                    : DateTime.MinValue;

                entries.Add(new WatchlistEntry(reader.GetString(0), at));
            }

            return entries;
        }

        private static bool ContainsAny(SqliteConnection conn, IReadOnlyList<string> keys)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM watchlist WHERE item_key = @key COLLATE NOCASE LIMIT 1";
            var key = cmd.Parameters.Add("@key", SqliteType.Text);

            foreach (var k in keys)
            {
                key.Value = k;
                if (cmd.ExecuteScalar() is not null) return true;
            }

            return false;
        }

        private static List<string> Clean(IEnumerable<string>? keys) =>
            (keys ?? Array.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
