using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

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

            // Fetched before the lane is taken, for the same reason the library is, and asked for
            // separately because it is a separate question: the library is what the server holds,
            // and this is what one person has half-watched of it.
            var resume = await TryGetResumeAsync(client, ct);

            ct.ThrowIfCancellationRequested();

            // The lane is taken here and not around the fetch above. Replacing the cache is one
            // transaction over the whole server library, so it is the longest write in the app and
            // the one most likely to collide with a scan — but holding a write lane across a
            // network call would block every other writer for as long as the server takes to
            // answer, which on a bad connection is fifteen seconds of a locked catalogue.
            return await DatabaseWriteLane.RunAsync(
                conn,
                _ =>
                {
                    var count = JellyfinCache.Replace(conn, movies);

                    // Null means the row could not be read, which is not the same as it being
                    // empty: the previous one is left exactly where it was. An empty list that the
                    // server did answer with is a real answer and does clear it.
                    if (resume is not null) JellyfinResumeCache.Replace(conn, resume);

                    return Task.FromResult(count);
                },
                ct);
        }

        /// <summary>
        /// The Continue watching row, or null when it could not be read.
        /// </summary>
        /// <remarks>
        /// Failing to read it is deliberately not failing the sync. The row is one endpoint on top
        /// of the library, and a server that will not answer it — an older build, a permission,
        /// a proxy rewriting a path — should cost the viewer their Continue watching row and not
        /// their entire library.
        /// </remarks>
        private static async Task<IReadOnlyList<JellyfinResumeItem>?> TryGetResumeAsync(
            JellyfinClient client,
            CancellationToken ct)
        {
            try
            {
                return await client.GetResumeAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", JellyfinClient.Redact($"could not read the resume list: {ex.Message}"));
                return null;
            }
        }
    }
}
