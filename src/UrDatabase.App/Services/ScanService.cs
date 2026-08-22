using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

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

        public async Task<int> ScanAsync(SqliteConnection conn, IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            int updated = 0;
            foreach (var folder in folders.Where(Directory.Exists))
            {
                progress?.Report($"Scanning: {folder}");
                foreach (var path in EnumerateFilesSafe(folder, ct))
                {
                    if (ct.IsCancellationRequested) break;
                    if (!IsVideoFile(path)) continue;

                    try
                    {
                        var fi = new FileInfo(path);
                        var size = fi.Exists ? fi.Length : 0;
                        var created = fi.Exists ? fi.CreationTimeUtc.ToString("o") : null;
                        var updatedAt = fi.Exists ? fi.LastWriteTimeUtc.ToString("o") : null;

                        var sql = @"
INSERT INTO files (movie_id, file_path, size_bytes, created_at, updated_at)
VALUES (NULL, @file_path, @size_bytes, @created_at, @updated_at)
ON CONFLICT(file_path) DO UPDATE SET
    size_bytes = excluded.size_bytes,
    updated_at = excluded.updated_at;
";
                        var rows = await conn.ExecuteAsync(sql, new
                        {
                            file_path = path,
                            size_bytes = (long)size,
                            created_at = created,
                            updated_at = updatedAt
                        });
                        if (rows > 0) updated++;
                    }
                    catch (Exception ex)
                    {
                        progress?.Report($"Error: {ex.Message} ({path})");
                    }
                }
            }
            progress?.Report($"Scan complete. Updated {updated} file entries.");
            return updated;
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
