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

        /// <summary>
        /// Moves an entry from one key to another, keeping when it was added. Returns whether
        /// anything changed. See <see cref="Watchlist.Adoptions"/> for why.
        /// </summary>
        /// <remarks>
        /// When the target is already on the list, the old entry is dropped and the earlier of
        /// the two dates is kept. Neither copy is lost and the film does not move up the shelf.
        /// </remarks>
        public static bool Rekey(SqliteConnection conn, string? fromKey, string? toKey)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(fromKey) || string.IsNullOrWhiteSpace(toKey)) return false;

            var from = fromKey.Trim();
            var to = toKey.Trim();
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return false;

            using var tx = conn.BeginTransaction();

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("@from", from);
            cmd.Parameters.AddWithValue("@to", to);

            cmd.CommandText = "SELECT added_at FROM watchlist WHERE item_key = @from";
            if (cmd.ExecuteScalar() is not string fromAddedAt)
            {
                tx.Rollback();
                return false;
            }

            cmd.CommandText = "SELECT added_at FROM watchlist WHERE item_key = @to";
            var toAddedAt = cmd.ExecuteScalar() as string;

            if (toAddedAt is null)
            {
                cmd.CommandText = "UPDATE watchlist SET item_key = @to WHERE item_key = @from";
                cmd.ExecuteNonQuery();
            }
            else
            {
                // Both are round-trip UTC timestamps written by Add, so they order as text.
                if (string.CompareOrdinal(fromAddedAt, toAddedAt) < 0)
                {
                    cmd.CommandText = "UPDATE watchlist SET added_at = @at WHERE item_key = @to";
                    cmd.Parameters.AddWithValue("@at", fromAddedAt);
                    cmd.ExecuteNonQuery();
                }

                cmd.CommandText = "DELETE FROM watchlist WHERE item_key = @from";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return true;
        }

        /// <summary>
        /// Moves a server film's entry onto the catalogue row a download of it has just become.
        /// </summary>
        public static bool AdoptDownload(SqliteConnection conn, string? remoteId, long localId)
        {
            if (string.IsNullOrWhiteSpace(remoteId) || localId <= 0) return false;

            return Rekey(conn, Watchlist.FilmKey(remoteId), Watchlist.LocalKey(localId));
        }

        /// <summary>Applies every move <see cref="Watchlist.Adoptions"/> found. Returns how many changed.</summary>
        public static int Adopt(SqliteConnection conn, IEnumerable<(string From, string To)>? moves)
        {
            var changed = 0;
            foreach (var (from, to) in moves ?? Array.Empty<(string, string)>())
                if (Rekey(conn, from, to)) changed++;

            return changed;
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
