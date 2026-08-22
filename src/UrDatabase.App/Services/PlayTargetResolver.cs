using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides which file the Play button opens for a catalogued film.
    ///
    /// The catalogue already knows the answer. <c>ScanService</c> writes <c>files.movie_id</c> for
    /// every file it records, and there is an index on it; that link is the only statement in the
    /// database about which film a file <em>is</em>. Resolution used to ignore it — it read every
    /// path in the table and returned the first filename containing the title — so the film
    /// <em>It</em> played <c>Spirited Away.mkv</c>, a remake played its original, and a path left
    /// behind by a deleted file beat one that was actually there.
    ///
    /// So the order here is: the link, then nothing. A filename that merely looks right comes back
    /// as <see cref="PlayTargetKind.Suggested"/> for a person to confirm and link, and is never an
    /// automatic play target. That is the issue's own prescription and it is the right one: the
    /// cost of refusing is a click, and the cost of guessing wrong is the wrong film.
    ///
    /// This lives in a service rather than in the details window because a window's code-behind
    /// cannot be tested without a UI thread, which is precisely why the original rule shipped
    /// unexamined.
    /// </summary>
    public static class PlayTargetResolver
    {
        /// <summary>
        /// Files the catalogue says belong to this film, best first.
        ///
        /// The tie-break is largest, then most recently updated, then path. A library with two
        /// prints of one film is ordinary rather than exceptional, so this has to be decided
        /// rather than left to whatever order SQLite returns rows in — the same click twice
        /// should not open two different files.
        ///
        /// Size first because it is the only thing the catalogue records that tracks picture
        /// quality: a 2160p remux is larger than the 720p rip beside it, and given the choice a
        /// user wants the better print. <c>updated_at</c> breaks a genuine tie towards the copy
        /// most recently written, which is the more likely one to have been deliberately put
        /// there, and the path breaks the remaining tie so the result is a total order and never
        /// a coin flip.
        /// </summary>
        private const string LinkedFilesSql = @"
SELECT file_path AS FilePath
FROM files
WHERE movie_id = @movie_id
ORDER BY COALESCE(size_bytes, 0) DESC, COALESCE(updated_at, '') DESC, file_path ASC;
";

        /// <summary>
        /// Candidates for a suggestion: files no other film has claimed. A file already linked to
        /// a different movie is, by the catalogue's own account, not this film, so offering it
        /// would be proposing to break a link that something already got right.
        /// </summary>
        private const string UnclaimedFilesSql = @"
SELECT file_path
FROM files
WHERE movie_id IS NULL OR movie_id = @movie_id
ORDER BY file_path ASC;
";

        /// <summary>
        /// Resolves what Play should do for <paramref name="movieId"/>.
        /// </summary>
        /// <param name="fileExists">
        /// How to test that a path is still on disk. Injectable so the rules can be tested without
        /// a filesystem; defaults to <see cref="File.Exists"/>.
        /// </param>
        public static PlayTarget Resolve(
            SqliteConnection conn,
            long movieId,
            string? title,
            int? year,
            Func<string, bool>? fileExists = null)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var exists = fileExists ?? File.Exists;

            var linked = conn.Query<string>(LinkedFilesSql, new { movie_id = movieId });
            foreach (var path in linked)
            {
                // A row can outlive the file it describes — nothing prunes `files` when something
                // is deleted or a drive is unmounted — so the link is necessary but not
                // sufficient, and the next linked file gets its turn.
                if (!string.IsNullOrWhiteSpace(path) && exists(path))
                    return PlayTarget.Linked(path);
            }

            return Suggest(conn, movieId, title, year, exists);
        }

        /// <summary>
        /// The best unclaimed filename for this title, if there is one worth showing. Used for a
        /// catalogue written before the scanner filled <c>movie_id</c> in, and for the film whose
        /// only copy was moved somewhere the scan has not reached.
        /// </summary>
        private static PlayTarget Suggest(
            SqliteConnection conn,
            long movieId,
            string? title,
            int? year,
            Func<string, bool> exists)
        {
            if (string.IsNullOrWhiteSpace(title)) return PlayTarget.None;

            var unclaimed = conn.Query<string>(UnclaimedFilesSql, new { movie_id = movieId })
                .Where(path => !string.IsNullOrWhiteSpace(path) && exists(path))
                .ToList();

            if (unclaimed.Count == 0) return PlayTarget.None;

            var suggestion = MovieFileMatcher.FindBestMatch(unclaimed, title, year);
            return suggestion is null ? PlayTarget.None : PlayTarget.Suggested(suggestion);
        }

        /// <summary>
        /// Records that <paramref name="filePath"/> is this film, so the choice survives closing
        /// the window. Without this a manual link lasted exactly as long as the dialog, which made
        /// the suggestion above pointless — there was nothing a user could do with it that stuck.
        ///
        /// Unlike a scan, this overwrites an existing link. A scan's link comes from parsing a
        /// filename and defers to whatever is already recorded; this one comes from a person
        /// pointing at the file, which is better evidence than a filename has ever been.
        /// </summary>
        public static void LinkFile(SqliteConnection conn, long movieId, string filePath)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A file path is required.", nameof(filePath));

            var info = new FileInfo(filePath);
            var size = info.Exists ? info.Length : 0L;
            var created = info.Exists ? info.CreationTimeUtc.ToString("o") : null;
            var modified = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null;

            const string sql = @"
INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at)
VALUES (@movie_id, @file_path, @size_bytes, @created_at, @updated_at)
ON CONFLICT(file_path) DO UPDATE SET
    movie_id   = excluded.movie_id,
    size_bytes = excluded.size_bytes,
    updated_at = excluded.updated_at;
";

            conn.Execute(sql, new
            {
                movie_id = movieId,
                file_path = filePath,
                size_bytes = size,
                created_at = created,
                updated_at = modified
            });
        }
    }
}
