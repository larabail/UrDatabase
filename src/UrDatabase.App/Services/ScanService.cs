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
        public static async Task<int> ScanLibraryAsync(string dbPath, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            using var conn = Database.Open(dbPath);
            return await new ScanService().ScanAsync(conn, folders, progress, ct);
        }

        /// <summary>
        /// Walks the watch folders and brings the catalogue up to date: every video file gets a row
        /// in <c>files</c>, a canonical row in <c>movies</c>, and a link between them.
        ///
        /// The movie row is the whole point. The window reads <c>movies</c> and nothing else, so a
        /// scan that only filled <c>files</c> left the library looking empty however many films were
        /// on disk. Returns the number of file rows written, which is what the caller reports.
        /// </summary>
        public async Task<int> ScanAsync(SqliteConnection conn, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (folders is null) throw new ArgumentNullException(nameof(folders));

            var index = await LoadMovieIndexAsync(conn);
            var known = index.Count;
            var updated = 0;
            var cancelled = false;

            foreach (var folder in folders.Where(Directory.Exists))
            {
                if (cancelled) break;
                progress?.Report($"Scanning: {folder}");

                // Batched rather than a commit per file, which cost a large library thousands of
                // fsyncs, and rather than one transaction for a whole folder, which would hold the
                // write lock long enough to starve poster enrichment on a big library.
                var tx = conn.BeginTransaction();
                try
                {
                    var sinceCommit = 0;

                    foreach (var path in EnumerateFilesSafe(folder, ct))
                    {
                        if (ct.IsCancellationRequested) { cancelled = true; break; }
                        if (!IsVideoFile(path)) continue;

                        try
                        {
                            if (await RecordFileAsync(conn, tx, index, path)) updated++;
                        }
                        catch (Exception ex)
                        {
                            progress?.Report($"Error: {ex.Message} ({path})");
                        }

                        if (++sinceCommit < FilesPerTransaction) continue;

                        tx.Commit();
                        tx.Dispose();
                        tx = conn.BeginTransaction();
                        sinceCommit = 0;
                    }

                    tx.Commit();
                }
                catch (OperationCanceledException)
                {
                    // Whatever was catalogued before the cancellation is worth keeping: every write
                    // here is idempotent, so a resumed scan simply carries on.
                    cancelled = true;
                    tx.Commit();
                }
                finally
                {
                    tx.Dispose();
                }
            }

            var added = index.Count - known;
            progress?.Report($"Scan complete. {updated} file entries updated, {added} movies added.");
            return updated;
        }

        /// <summary>
        /// Writes one file and the movie it belongs to. Both statements are upserts, so re-running
        /// a scan over an unchanged folder changes nothing.
        /// </summary>
        private static async Task<bool> RecordFileAsync(SqliteConnection conn, SqliteTransaction tx, MovieIndex index, string path)
        {
            var info = new FileInfo(path);
            var size = info.Exists ? info.Length : 0L;
            var created = info.Exists ? info.CreationTimeUtc.ToString("o") : null;
            var modified = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null;

            var movieId = await EnsureMovieAsync(conn, tx, index, FilenameParser.Parse(path));

            const string sql = @"
INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at)
VALUES (@movie_id, @file_path, @size_bytes, @created_at, @updated_at)
ON CONFLICT(file_path) DO UPDATE SET
    movie_id   = COALESCE(files.movie_id, excluded.movie_id),
    size_bytes = excluded.size_bytes,
    updated_at = excluded.updated_at;
";

            var rows = await conn.ExecuteAsync(sql, new
            {
                movie_id = movieId,
                file_path = path,
                size_bytes = size,
                created_at = created,
                updated_at = modified
            }, tx);

            return rows > 0;
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
        public static IEnumerable<string> EnumerateFilesSafe(string root, CancellationToken ct = default)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();

                string[] subDirs = Array.Empty<string>();
                try { subDirs = Directory.GetDirectories(dir); } catch { }
                foreach (var sd in subDirs) stack.Push(sd);

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir); } catch { }
                foreach (var f in files) yield return f;
            }
        }
    }
}
