using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// The <c>scans</c> table: one row per scan, opened before the first folder and closed after
    /// the last one.
    ///
    /// Without it a scan leaves no trace of itself. An interrupted one commits an arbitrary prefix
    /// of the library and there is nothing afterwards that says how far it got, or even that it
    /// happened — and, more sharply, nothing that says whether it finished. That second question
    /// is not bookkeeping: marking a file missing because a scan did not see it is only sound when
    /// the scan actually looked everywhere, and a cancelled scan by construction did not.
    ///
    /// Writes here are never cancellable. The row recording that a scan was cancelled cannot
    /// itself be abandoned for being cancelled, and the wait is one other writer's turn in the
    /// lane rather than anything unbounded.
    /// </summary>
    public static class ScanSessions
    {
        /// <summary>
        /// Opens a scan and returns its id. The row is written before any file is touched, so an
        /// app that dies mid-scan leaves a <c>running</c> row behind — which is the honest record
        /// of what happened, and distinguishable from every scan that ended on purpose.
        /// </summary>
        public static async Task<long> BeginAsync(
            SqliteConnection conn,
            IEnumerable<string> roots,
            IEnumerable<string> skippedRoots,
            DateTimeOffset startedAt)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            const string sql = @"
INSERT INTO scans (started_at, status, roots, skipped_roots)
VALUES (@started_at, @status, @roots, @skipped_roots)
RETURNING id;
";

            return await DatabaseWriteLane.RunAsync(
                conn,
                _ => conn.ExecuteScalarAsync<long>(sql, new
                {
                    started_at = Timestamp(startedAt),
                    status = NameOf(ScanStatus.Running),
                    roots = Encode(roots),
                    skipped_roots = Encode(skippedRoots),
                }),
                CancellationToken.None);
        }

        /// <summary>Closes a scan with how it ended and what it counted.</summary>
        public static async Task FinishAsync(SqliteConnection conn, ScanResult result, DateTimeOffset finishedAt)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (result is null) throw new ArgumentNullException(nameof(result));

            const string sql = @"
UPDATE scans SET
    finished_at = @finished_at,
    status      = @status,
    inserted    = @inserted,
    moved       = @moved,
    updated     = @updated,
    unchanged   = @unchanged,
    failed      = @failed,
    missing     = @missing
WHERE id = @id;
";

            await DatabaseWriteLane.RunAsync(
                conn,
                _ => conn.ExecuteAsync(sql, new
                {
                    id = result.ScanId,
                    finished_at = Timestamp(finishedAt),
                    status = NameOf(result.Status),
                    inserted = result.Inserted,
                    moved = result.Moved,
                    updated = result.Updated,
                    unchanged = result.Unchanged,
                    failed = result.Failed,
                    missing = result.Missing,
                }),
                CancellationToken.None);
        }

        /// <summary>
        /// The status of a scan, or null when there is no such row. Reads back the vocabulary
        /// <see cref="NameOf"/> writes, and answers <see cref="ScanStatus.Running"/> for a value
        /// this version does not recognise — an unfinished scan is the safe reading of a status
        /// nothing here understands, because nothing may be concluded from it.
        /// </summary>
        public static async Task<ScanStatus?> StatusOfAsync(SqliteConnection conn, long scanId)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var stored = await conn.ExecuteScalarAsync<string?>(
                "SELECT status FROM scans WHERE id = @id", new { id = scanId });

            if (stored is null) return null;

            return Enum.TryParse<ScanStatus>(stored, ignoreCase: true, out var parsed)
                ? parsed
                : ScanStatus.Running;
        }

        /// <summary>The folders a scan recorded, decoded back from the column.</summary>
        public static IReadOnlyList<string> Decode(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return Array.Empty<string>();

            try
            {
                return JsonSerializer.Deserialize<string[]>(stored) ?? Array.Empty<string>();
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// The stored form of a status: its name in lower case. Written rather than the enum's
        /// numeric value so that the column reads as itself in a SQLite browser, which is where
        /// anybody debugging a library they cannot reproduce will be looking at it.
        /// </summary>
        internal static string NameOf(ScanStatus status) => status.ToString().ToLowerInvariant();

        /// <summary>
        /// JSON rather than a delimiter. A folder name can legally contain a newline, a comma and
        /// a semicolon on both platforms this ships to, and a list that cannot be read back is a
        /// worse record than no list.
        /// </summary>
        private static string Encode(IEnumerable<string>? paths) =>
            JsonSerializer.Serialize((paths ?? Array.Empty<string>()).ToArray());

        /// <summary>
        /// UTC, round-trippable, and sorting the same as it compares — the format every other
        /// timestamp in this database already uses.
        /// </summary>
        internal static string Timestamp(DateTimeOffset at) => at.UtcDateTime.ToString("o");
    }
}
