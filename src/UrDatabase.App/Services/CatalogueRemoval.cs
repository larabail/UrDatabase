using System;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services;

public static class CatalogueRemoval
{
    // The caller holds DatabaseWriteLane. No path is opened or deleted here.
    public static bool Remove(SqliteConnection conn, long localId, string? remoteId)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (localId <= 0 && string.IsNullOrWhiteSpace(remoteId))
            throw new ArgumentException("A catalogued film is required.");

        using var tx = conn.BeginTransaction();
        var removed = 0;
        if (localId > 0)
        {
            conn.Execute("DELETE FROM files WHERE movie_id = @id", new { id = localId }, tx);
            removed += conn.Execute("DELETE FROM movies WHERE id = @id", new { id = localId }, tx);
        }

        if (!string.IsNullOrWhiteSpace(remoteId))
        {
            var remoteRemoved = conn.Execute("DELETE FROM jellyfin_movies WHERE item_id = @id",
                new { id = remoteId }, tx);
            removed += remoteRemoved;
            if (remoteRemoved > 0)
            {
                conn.Execute("DELETE FROM jellyfin_resume WHERE item_id = @id", new { id = remoteId }, tx);
                conn.Execute("DELETE FROM jellyfin_resume_dismissals WHERE item_id = @id", new { id = remoteId }, tx);
            }
        }

        if (removed == 0) return false;
        foreach (var key in Watchlist.KeysFor(localId, remoteId))
        {
            conn.Execute("DELETE FROM watchlist WHERE item_key = @key", new { key }, tx);
            conn.Execute("DELETE FROM film_problems WHERE film_key = @key", new { key }, tx);
        }

        tx.Commit();
        return true;
    }

    public static string ConfirmRemoval(string title) =>
        $"Remove \"{title}\" from the catalogue?\n\n"
        + "Video files on this computer and on Jellyfin will not be deleted or changed. "
        + "A later folder scan or Jellyfin sync can add the film again.\n\n"
        + "Your rating, review and viewings are kept. Its catalogue match, Watchlist entries "
        + "and reported problems are removed.";
}
