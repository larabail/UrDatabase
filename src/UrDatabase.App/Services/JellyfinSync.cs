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
        /// Refreshes the cache and returns how many films the server reported.
        /// </summary>
        /// <exception cref="JellyfinException">The server could not be reached or refused.</exception>
        public static async Task<int> RefreshAsync(
            JellyfinClient client,
            SqliteConnection conn,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            if (client is null) throw new ArgumentNullException(nameof(client));
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var movies = await client.GetMoviesAsync(progress, ct);

            ct.ThrowIfCancellationRequested();
            return JellyfinCache.Replace(conn, movies);
        }
    }
}
