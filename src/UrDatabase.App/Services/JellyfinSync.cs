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
        /// Refreshes the cache and returns what the server reported.
        /// </summary>
        /// <remarks>
        /// Films and television in one pass, and written in one transaction, so the two halves of
        /// the cache can never describe two different minutes. Seasons and episodes are not
        /// fetched here on purpose: a library of two hundred shows is thousands of episodes, and a
        /// sync that walked them all would take minutes to fill in a screen almost nobody has
        /// open. They are asked for when a series is opened — see <see cref="SeriesLoader"/>.
        ///
        /// The Continue watching row rides along in the same transaction, for the same reason: the
        /// row and the library it points into must never describe two different minutes either.
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
            await DatabaseWriteLane.RunAsync(
                conn,
                _ =>
                {
                    var count = JellyfinCache.Replace(conn, contents);

                    // Null means the row could not be read, which is not the same as it being
                    // empty: the previous one is left exactly where it was. An empty list that the
                    // server did answer with is a real answer and does clear it.
                    if (resume is not null)
                    {
                        JellyfinResumeCache.Replace(conn, resume);

                        // A dismissal only lasts as long as the position it was made at, so this
                        // is where one expires: the server has just said where everything is, and
                        // anything it now disagrees with is a dismissal about a viewing that has
                        // moved on. Only ever with an answer the server actually gave — pruning
                        // against a failed fetch would forget every dismissal the first time the
                        // app was opened away from home.
                        ResumeDismissalStore.Prune(conn, resume);
                    }

                    return Task.FromResult(count);
                },
                ct);

            return new JellyfinSyncResult(contents.Movies.Count, contents.Series.Count);
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
