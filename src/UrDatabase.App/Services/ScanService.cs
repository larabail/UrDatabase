using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    public class ScanService
    {
        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".ts", ".webm"
        };

        public static IReadOnlyCollection<string> SupportedExtensions => VideoExtensions;

        /// <summary>
        /// How many files share a write transaction. Small enough that poster enrichment and the
        /// window's own reads are never locked out for long, large enough that a big library is not
        /// one fsync per file.
        /// </summary>
        private const int FilesPerTransaction = 200;

        /// <summary>What became of one file the scan walked past. The categories do not overlap.</summary>
        private enum FileOutcome
        {
            /// <summary>Already accounted for by this scan, because two watch folders overlap.</summary>
            AlreadySeen,
            Inserted,
            Moved,
            Updated,
            Unchanged,
        }

        /// <summary>
        /// True when the path looks like a movie file. The comparison is deliberately
        /// case-insensitive: <c>.MKV</c> and <c>.mkv</c> are the same file type even on a
        /// case-sensitive filesystem.
        /// </summary>
        public static bool IsVideoFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && VideoExtensions.Contains(extension);
        }

        /// <summary>
        /// Opens a connection, scans, and closes it — in that order.
        ///
        /// The window's click handler used to open a connection, start the scan without awaiting
        /// it and return, disposing the connection while the scan was still writing through it.
        /// Owning the connection here means there is no shape in which a caller can dispose it
        /// early, and the returned Task completes only once every row is committed.
        /// </summary>
        public static async Task<ScanResult> ScanLibraryAsync(string dbPath, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            using var conn = Database.Open(dbPath);
            return await new ScanService().ScanAsync(conn, folders, progress, ct);
        }

        /// <summary>
        /// Walks the watch folders and brings the catalogue up to date: every video file gets a row
        /// in <c>files</c>, a canonical row in <c>movies</c>, and a link between them.
        ///
        /// The movie row is the whole point. The window reads <c>movies</c> and nothing else, so a
        /// scan that only filled <c>files</c> left the library looking empty however many films
        /// were on disk.
        ///
        /// A scan is also the only thing that can notice a film is gone, and it used to be
        /// incapable of it. Every write was an upsert of a path the walk had just found, so the
        /// catalogue could only grow: a deleted file, an unplugged drive and a folder somebody
        /// renamed all left their rows behind, untouched and indistinguishable, and a film dragged
        /// somewhere else became a second row beside the first. Fixing that needs two things this
        /// method now does. It stamps which scan last saw each row, so "not seen" is something
        /// recorded rather than inferred; and it opens a <c>scans</c> row for that fact to be
        /// about — one that knows whether it ran to the end, because only a scan that looked
        /// everywhere may conclude anything from not having found something.
        ///
        /// Nothing is deleted here, and that is a decision rather than an omission. From inside
        /// this process a film you deleted and a film on a drive you unplugged are the same
        /// absence, so the scan marks it and leaves the reading to a person.
        /// </summary>
        public async Task<ScanResult> ScanAsync(SqliteConnection conn, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (folders is null) throw new ArgumentNullException(nameof(folders));

            var requested = folders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

            // Split before anything is written, because the difference decides what this scan is
            // allowed to conclude. A folder that is not there was not searched, so nothing under
            // it may be called missing — which is the whole reason unplugging an external drive
            // does not cost somebody their catalogue.
            var walked = requested.Where(Directory.Exists).ToList();
            var skipped = requested.Where(f => !Directory.Exists(f)).ToList();

            var scanId = await ScanSessions.BeginAsync(conn, walked, skipped, DateTimeOffset.UtcNow);

            var movies = await LoadMovieIndexAsync(conn);
            var knownMovies = movies.Count;
            var files = await ScanFileIndex.LoadAsync(conn);

            var counts = new ScanCounts();
            var unreadable = new List<string>();
            var status = ScanStatus.Completed;

            try
            {
                foreach (var folder in walked)
                {
                    if (status == ScanStatus.Cancelled) break;
                    progress?.Report($"Scanning: {folder}");

                    if (!await ScanFolderAsync(conn, folder, scanId, movies, files, counts, unreadable, progress, ct))
                        status = ScanStatus.Cancelled;
                }

                // Only ever after a scan that finished. A cancelled one stopped somewhere
                // arbitrary, so everything it had not reached yet looks exactly like everything
                // that is gone, and it has no way to tell those apart.
                if (status == ScanStatus.Completed)
                    counts.Missing = await MarkMissingAsync(conn, files, walked, unreadable, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                await CloseQuietlyAsync(
                    conn,
                    ScanResult.From(scanId, ScanStatus.Failed, counts, movies.Count - knownMovies, walked, skipped));
                throw;
            }

            var result = ScanResult.From(scanId, status, counts, movies.Count - knownMovies, walked, skipped);
            await ScanSessions.FinishAsync(conn, result, DateTimeOffset.UtcNow);

            progress?.Report(result.Summary);
            return result;
        }

        /// <summary>
        /// Walks one folder, committing in batches. Returns false when the scan was cancelled.
        ///
        /// Batched rather than a commit per file, which cost a large library thousands of fsyncs,
        /// and rather than one transaction for a whole folder, which would hold the write lock
        /// long enough to starve poster enrichment on a big library.
        /// </summary>
        private static async Task<bool> ScanFolderAsync(
            SqliteConnection conn,
            string folder,
            long scanId,
            MovieIndex movies,
            ScanFileIndex files,
            ScanCounts counts,
            ICollection<string> unreadable,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            IDisposable? lease = null;
            SqliteTransaction? tx = null;
            var completed = false;

            try
            {
                // A turn in the write lane, taken per batch rather than per scan. The poster
                // loader and a Jellyfin sync write to the same file, and holding the lane for a
                // whole library would shut them out for the length of a scan — the starvation
                // FilesPerTransaction already exists to prevent.
                //
                // Not cancellable, deliberately. The lease and the transaction are replaced as a
                // pair, and a cancellation thrown between them would leave the cleanup below
                // committing a transaction that no longer exists. Cancellation is picked up by the
                // enumerator instead, and the wait here is one other writer's turn rather than
                // anything unbounded.
                //
                // Both are taken inside the try, and that is the whole reason this shape is worth
                // the nulls. Opening the transaction can fail — on a connection somebody disposed
                // underneath the scan, most obviously — and taking the lane on the line above a
                // throw meant the lane was never given back. Nothing in the process could write
                // again, including the code trying to record that the scan had failed, so a
                // reported failure became a silent hang instead.
                lease = await DatabaseWriteLane.EnterAsync(conn, CancellationToken.None);
                tx = conn.BeginTransaction();

                var sinceCommit = 0;

                foreach (var path in EnumerateFilesSafe(folder, ct, unreadable))
                {
                    if (ct.IsCancellationRequested) break;
                    if (!IsVideoFile(path)) continue;

                    try
                    {
                        Tally(counts, await RecordFileAsync(conn, tx, movies, files, scanId, path));
                    }
                    catch (Exception ex)
                    {
                        counts.Failed++;
                        progress?.Report($"Error: {ex.Message} ({path})");
                    }

                    if (++sinceCommit < FilesPerTransaction) continue;

                    tx.Commit();
                    tx.Dispose();
                    tx = null;
                    lease.Dispose();
                    lease = null;

                    lease = await DatabaseWriteLane.EnterAsync(conn, CancellationToken.None);
                    tx = conn.BeginTransaction();
                    sinceCommit = 0;
                }

                completed = !ct.IsCancellationRequested;
                tx.Commit();
            }
            catch (OperationCanceledException)
            {
                // Whatever was catalogued before the cancellation is worth keeping: every write
                // here is idempotent, so a resumed scan simply carries on.
                tx?.Commit();
            }
            finally
            {
                tx?.Dispose();
                lease?.Dispose();
            }

            return completed;
        }

        private static void Tally(ScanCounts counts, FileOutcome outcome)
        {
            switch (outcome)
            {
                case FileOutcome.Inserted: counts.Inserted++; break;
                case FileOutcome.Moved: counts.Moved++; break;
                case FileOutcome.Updated: counts.Updated++; break;
                case FileOutcome.Unchanged: counts.Unchanged++; break;
            }
        }

        /// <summary>
        /// Writes a file the catalogue has never seen. Still an upsert: the in-memory index is a
        /// snapshot, and a second copy of the app writing the same path between the snapshot and
        /// here would otherwise turn a scan into a constraint violation.
        /// </summary>
        private const string InsertFileSql = @"
INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at, last_seen_at, last_seen_scan_id)
VALUES (@movie_id, @file_path, @size_bytes, @created_at, @updated_at, @last_seen_at, @scan_id)
ON CONFLICT(file_path) DO UPDATE SET
    movie_id          = COALESCE(files.movie_id, excluded.movie_id),
    size_bytes        = excluded.size_bytes,
    updated_at        = excluded.updated_at,
    last_seen_at      = excluded.last_seen_at,
    last_seen_scan_id = excluded.last_seen_scan_id,
    missing_since     = NULL
RETURNING id;
";

        /// <summary>
        /// Updates the row for a file that already had one, whether it is still where it was or is
        /// being relinked to a path it has moved to.
        ///
        /// <c>movie_id</c> is coalesced rather than assigned: a link a person made by hand is
        /// better evidence than a filename has ever been, and a scan must not overwrite it.
        /// <c>created_at</c> is coalesced the other way round, keeping what was recorded when the
        /// file cannot be stat'd now, because a value that was true once beats a null.
        /// </summary>
        private const string UpdateFileSql = @"
UPDATE files SET
    movie_id          = COALESCE(movie_id, @movie_id),
    file_path         = @file_path,
    size_bytes        = @size_bytes,
    created_at        = COALESCE(@created_at, created_at),
    updated_at        = @updated_at,
    last_seen_at      = @last_seen_at,
    last_seen_scan_id = @scan_id,
    missing_since     = NULL
WHERE id = @id;
";

        /// <summary>
        /// Writes one file and the movie it belongs to, and says which of those it was.
        ///
        /// Every path this scan walks past is stamped with <c>last_seen_at</c> and the scan's id,
        /// including one that has not changed since the last scan. That is the point of the
        /// column: a row nobody stamped is a row nothing found, and a file that is exactly as it
        /// was is still very much there.
        /// </summary>
        private static async Task<FileOutcome> RecordFileAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            MovieIndex movies,
            ScanFileIndex files,
            long scanId,
            string path)
        {
            var info = new FileInfo(path);
            var size = info.Exists ? info.Length : 0L;
            var created = info.Exists ? info.CreationTimeUtc.ToString("o") : null;
            var modified = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null;
            var seenAt = ScanSessions.Timestamp(DateTimeOffset.UtcNow);

            var existing = files.ByPath(path);

            // Two watch folders where one contains the other. Doing the work twice would count the
            // same file twice and report a library as double the size it is.
            if (existing is not null && files.WasSeen(existing)) return FileOutcome.AlreadySeen;

            var movieId = await EnsureMovieAsync(conn, tx, movies, FilenameParser.Parse(path));

            var outcome = FileOutcome.Unchanged;
            if (existing is null)
            {
                existing = files.FindMoved(path, size);
                if (existing is not null) outcome = FileOutcome.Moved;
            }
            else if (existing.DiffersFrom(size, modified, movieId))
            {
                outcome = FileOutcome.Updated;
            }

            var parameters = new
            {
                id = existing?.Id ?? 0L,
                movie_id = movieId,
                file_path = path,
                size_bytes = size,
                created_at = created,
                updated_at = modified,
                last_seen_at = seenAt,
                scan_id = scanId,
            };

            if (existing is null)
            {
                var id = await conn.ExecuteScalarAsync<long>(InsertFileSql, parameters, tx);

                files.Add(new ScanFileRow
                {
                    Id = id,
                    MovieId = movieId,
                    FilePath = path,
                    SizeBytes = size,
                    UpdatedAt = modified,
                });

                return FileOutcome.Inserted;
            }

            await conn.ExecuteAsync(UpdateFileSql, parameters, tx);

            if (outcome == FileOutcome.Moved) files.Repath(existing, path);

            existing.MovieId ??= movieId;
            existing.SizeBytes = size;
            existing.UpdatedAt = modified;
            existing.MissingSince = null;
            files.MarkSeen(existing);

            return outcome;
        }

        /// <summary>
        /// Marks every row a completed scan did not see, under a folder it actually walked, and
        /// returns how many that was.
        ///
        /// Marked, never deleted. The issue this closes is explicit about it and it is the right
        /// call: this process cannot tell a film somebody deleted from a film on a drive that is
        /// not plugged in, and only one of those readings is recoverable from. The mark is also
        /// what gives an eventual prune something to count from — a row missing since March across
        /// several scans is a very different claim from a row missing since a minute ago.
        ///
        /// Batched the same way the walk is, and for the same reason: a folder that lost a
        /// thousand files should not hold the write lane for as long as it takes to say so.
        /// </summary>
        private static async Task<int> MarkMissingAsync(
            SqliteConnection conn,
            ScanFileIndex files,
            IReadOnlyCollection<string> walked,
            IReadOnlyCollection<string> unreadable,
            DateTimeOffset markedAt)
        {
            var gone = files.Unseen(walked, unreadable);
            if (gone.Count == 0) return 0;

            const string sql = "UPDATE files SET missing_since = @missing_since WHERE id = @id AND missing_since IS NULL;";
            var stamp = ScanSessions.Timestamp(markedAt);

            foreach (var batch in gone.Chunk(FilesPerTransaction))
            {
                await DatabaseWriteLane.RunAsync(
                    conn,
                    async _ =>
                    {
                        using var tx = conn.BeginTransaction();
                        await conn.ExecuteAsync(
                            sql,
                            batch.Select(row => new { id = row.Id, missing_since = stamp }),
                            tx);
                        tx.Commit();
                    },
                    CancellationToken.None);
            }

            foreach (var row in gone) row.MissingSince = stamp;

            return gone.Count;
        }

        /// <summary>
        /// Records how a scan ended when it is already on its way out with an exception.
        ///
        /// Best effort by design. The usual reason a scan throws is that the database went away
        /// underneath it, in which case this cannot work either — and letting it throw would
        /// replace the caller's exception with a worse one that hides what actually happened.
        /// </summary>
        private static async Task CloseQuietlyAsync(SqliteConnection conn, ScanResult result)
        {
            try
            {
                await ScanSessions.FinishAsync(conn, result, DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                // Deliberately swallowed; see above.
            }
        }

        /// <summary>
        /// Returns the id of the movie a parsed filename belongs to, creating the row when nothing
        /// matches. Matching happens in <see cref="MovieIndex"/> rather than in SQL so that two
        /// spellings of one title collapse onto a single row.
        /// </summary>
        private static async Task<long> EnsureMovieAsync(SqliteConnection conn, SqliteTransaction tx, MovieIndex index, ParsedMedia parsed)
        {
            if (string.IsNullOrWhiteSpace(parsed.Title))
                throw new InvalidOperationException("The filename produced no usable title.");

            if (index.TryResolve(parsed, out var existingId, out var yearIsNewInformation))
            {
                if (yearIsNewInformation && parsed.Year.HasValue)
                {
                    await conn.ExecuteAsync(
                        "UPDATE movies SET year = @year WHERE id = @id AND year IS NULL",
                        new { year = parsed.Year.Value, id = existingId }, tx);

                    index.SetYear(existingId, parsed.Title, parsed.Year.Value);
                }

                return existingId;
            }

            // RETURNING rather than last_insert_rowid(): the FTS triggers on movies write to the
            // index straight after this row, and last_insert_rowid() would report that write.
            var id = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO movies (title, year) VALUES (@title, @year) RETURNING id",
                new { title = parsed.Title, year = parsed.Year }, tx);

            index.Add(id, parsed.Title, parsed.Year);
            return id;
        }

        /// <summary>
        /// Catalogues a single file that arrived outside a scan — today, a film downloaded from
        /// the Jellyfin server. Returns the id of the movie row it belongs to.
        ///
        /// It exists so a download is playable the instant it finishes rather than after the user
        /// works out that a scan is what makes a film appear. It writes through the same upsert and
        /// the same title index a scan uses, so the later scan that also walks the download folder
        /// agrees with it: the path is the key, so no second file row appears, and the title
        /// resolves through <see cref="MovieIndex"/>, so a download of a film already in the
        /// library links to the row that is already there rather than forking it.
        ///
        /// <c>last_seen_scan_id</c> is left null because no scan found this file — a download is
        /// not a scan, and borrowing an id from one would put this row in a session that never
        /// walked past it. <c>last_seen_at</c> is stamped, because the file is demonstrably there.
        /// </summary>
        public static async Task<long> RecordSingleFileAsync(SqliteConnection conn, string path)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));

            var movies = await LoadMovieIndexAsync(conn);

            var info = new FileInfo(path);
            var size = info.Exists ? info.Length : 0L;
            var created = info.Exists ? info.CreationTimeUtc.ToString("o") : null;
            var modified = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null;

            using var tx = conn.BeginTransaction();

            var movieId = await EnsureMovieAsync(conn, tx, movies, FilenameParser.Parse(path));

            await conn.ExecuteScalarAsync<long>(InsertFileSql, new
            {
                movie_id = movieId,
                file_path = path,
                size_bytes = size,
                created_at = created,
                updated_at = modified,
                last_seen_at = ScanSessions.Timestamp(DateTimeOffset.UtcNow),
                scan_id = (long?)null,
            }, tx);

            tx.Commit();
            return movieId;
        }

        /// <summary>
        /// Loads the catalogue into memory once per scan. A personal library is small enough that
        /// this costs less than a query per file, and it lets the matching rules stay pure.
        /// </summary>
        private static async Task<MovieIndex> LoadMovieIndexAsync(SqliteConnection conn)
        {
            var index = new MovieIndex();

            var rows = await conn.QueryAsync<MovieRow>(
                "SELECT id AS Id, title AS Title, year AS Year FROM movies ORDER BY id");

            foreach (var row in rows)
                index.Add(row.Id, row.Title, row.Year);

            return index;
        }

        private sealed class MovieRow
        {
            public long Id { get; set; }
            public string? Title { get; set; }
            public int? Year { get; set; }
        }

        /// <summary>
        /// Walks <paramref name="root"/> depth-first, skipping directories the OS refuses to read.
        /// macOS in particular denies access to folders such as ~/Library without a TCC grant, so a
        /// single unreadable subtree must not abort the whole scan.
        /// </summary>
        /// <param name="unreadable">
        /// Collects the directories that were refused, when a caller passes one. Skipping them
        /// quietly is right for the walk and wrong for what follows it: a folder nobody was
        /// allowed to open holds files that were not seen and are not gone, and marking a library
        /// missing the first time macOS withholds a TCC grant would be a spectacular way to answer
        /// a permission prompt.
        /// </param>
        public static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken ct = default, ICollection<string>? unreadable = null)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                var refused = false;

                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(dir); } catch { refused = true; }
                foreach (var sd in subDirs) stack.Push(sd);

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir); } catch { refused = true; }
                foreach (var f in files) yield return f;

                // A root that fails is usually a root that is simply not there, which the caller
                // has already checked for and reports as a skipped folder rather than a refusal.
                if (refused && unreadable is not null && Directory.Exists(dir)) unreadable.Add(dir);
            }
        }
    }
}
