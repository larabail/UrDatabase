using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// One refresh of the cached server library: fetch, then write.
    ///
    /// The order is the whole point. The cache is only replaced once the server has answered in
    /// full, so a sync attempted from a coffee shop fails without emptying the library the owner
    /// was looking at. Anything else would turn "no network" into "no films".
    /// </summary>
    public static class JellyfinSync
    {
        /// <summary>
        /// Refreshes the cache and returns what the server reported.
        /// </summary>
        /// <remarks>
        /// Films and television in one pass, and written in one transaction, so the two halves of
        /// the cache can never describe two different minutes. Seasons and episodes are not
        /// fetched here on purpose: a library of two hundred shows is thousands of episodes, and a
        /// sync that walked them all would take minutes to fill in a screen almost nobody has
        /// open. They are asked for when a series is opened — see <see cref="SeriesLoader"/>.
        /// </remarks>
        /// <exception cref="JellyfinException">The server could not be reached or refused.</exception>
        public static async Task<JellyfinSyncResult> RefreshAsync(
            JellyfinClient client,
            SqliteConnection conn,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var contents = await client.GetLibraryAsync(progress, ct);

            ct.ThrowIfCancellationRequested();

            // The lane is taken here and not around the fetch above. Replacing the cache is one
            // transaction over the whole server library, so it is the longest write in the app and
            // the one most likely to collide with a scan — but holding a write lane across a
            // network call would block every other writer for as long as the server takes to
            // answer, which on a bad connection is fifteen seconds of a locked catalogue.
            await DatabaseWriteLane.RunAsync(
                conn,
                _ => Task.FromResult(JellyfinCache.Replace(conn, contents)),
                ct);

            return new JellyfinSyncResult(contents.Movies.Count, contents.Series.Count);
        }
    }
}
