using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// The <c>files</c> table as it stood when a scan began, in memory, so the scan can tell what
    /// it is looking at before it writes.
    ///
    /// A scan used to be a single upsert per path, which can only ever add: the database looked
    /// the same whether a file was new, changed, untouched, or had just been dragged into another
    /// folder, and the count it reported said "updated" for all four. Answering those apart needs
    /// the prior row, and reading it back per file would be a query per file. The catalogue loads
    /// once instead, which is the same bargain <see cref="MovieIndex"/> already strikes and the
    /// same reason: a personal film library is small, and the rules stay pure enough to test.
    ///
    /// It also carries the record of what this scan has seen, which is what the missing pass is
    /// computed from at the end.
    /// </summary>
    public sealed class ScanFileIndex
    {
        private readonly Dictionary<string, ScanFileRow> _byPath;
        private readonly Dictionary<string, List<ScanFileRow>> _byIdentity;
        private readonly HashSet<long> _seen = new();

        private ScanFileIndex(IEnumerable<ScanFileRow> rows)
        {
            _byPath = new Dictionary<string, ScanFileRow>(PathScope.Comparer);
            _byIdentity = new Dictionary<string, List<ScanFileRow>>(PathScope.Comparer);

            foreach (var row in rows)
            {
                // Last one wins. Two rows whose paths differ only in case can exist in a database
                // written before this lookup did, and picking one is better than throwing over a
                // library somebody already has.
                _byPath[PathScope.Normalise(row.FilePath)] = row;

                var identity = IdentityOf(row.FilePath, row.SizeBytes);
                if (identity is null) continue;

                if (!_byIdentity.TryGetValue(identity, out var bucket))
                    _byIdentity[identity] = bucket = new List<ScanFileRow>();

                bucket.Add(row);
            }
        }

        /// <summary>Every row loaded, in id order.</summary>
        public IReadOnlyCollection<ScanFileRow> Rows => _byPath.Values.OrderBy(r => r.Id).ToList();

        /// <summary>Reads the whole table once. Cheap for a personal library; see the type remarks.</summary>
        public static async Task<ScanFileIndex> LoadAsync(SqliteConnection conn)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            var rows = await conn.QueryAsync<ScanFileRow>(@"
SELECT id            AS Id,
       movie_id      AS MovieId,
       file_path     AS FilePath,
       size_bytes    AS SizeBytes,
       updated_at    AS UpdatedAt,
       missing_since AS MissingSince
FROM files
ORDER BY id");

            return new ScanFileIndex(rows);
        }

        /// <summary>The row for a path, or null when the scan has found something new.</summary>
        public ScanFileRow? ByPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            return _byPath.TryGetValue(PathScope.Normalise(path), out var row) ? row : null;
        }

        /// <summary>
        /// The row this file used to be, when it looks like the same file somewhere else.
        ///
        /// Identity here is the filename plus the exact byte length, and that choice is a
        /// judgement rather than a fact. It is the strongest signal the catalogue actually holds —
        /// nothing records an inode or a hash, and hashing a library of ten-gigabyte remuxes on
        /// every scan is not a trade worth making for this. Reorganising a collection is
        /// overwhelmingly moving files, not editing them, so the pair survives exactly the
        /// operation this is trying to follow.
        ///
        /// Three guards keep it honest, and each one is a case where the heuristic would otherwise
        /// be wrong:
        ///
        /// <list type="bullet">
        /// <item>The old path must be gone from disk. A second copy of a film under another folder
        /// is a duplicate, not a move, and relinking would silently drop one of the two rows.</item>
        /// <item>The candidate must not already have been seen by this scan, for the same
        /// reason.</item>
        /// <item>There must be exactly one candidate. Two rows with one name and one size are
        /// genuinely indistinguishable from here, and guessing which became which would scramble
        /// two links rather than lose one.</item>
        /// </list>
        ///
        /// What it does not catch: a file renamed as well as moved, which reads as a deletion and
        /// an addition; and a zero-byte file, excluded outright because a placeholder collides
        /// with every other placeholder.
        /// </summary>
        public ScanFileRow? FindMoved(string path, long size, Func<string, bool>? fileExists = null)
        {
            var identity = IdentityOf(path, size);
            if (identity is null) return null;
            if (!_byIdentity.TryGetValue(identity, out var bucket)) return null;

            var exists = fileExists ?? File.Exists;
            ScanFileRow? found = null;

            foreach (var candidate in bucket)
            {
                if (_seen.Contains(candidate.Id)) continue;
                if (PathScope.Normalise(candidate.FilePath).Equals(PathScope.Normalise(path), PathScope.Comparison)) continue;
                if (exists(candidate.FilePath)) continue;

                if (found is not null) return null;
                found = candidate;
            }

            return found;
        }

        /// <summary>Records that this scan walked past the file behind <paramref name="row"/>.</summary>
        public void MarkSeen(ScanFileRow row)
        {
            if (row is null) throw new ArgumentNullException(nameof(row));

            _seen.Add(row.Id);
        }

        /// <summary>True when this scan has already accounted for that row.</summary>
        public bool WasSeen(ScanFileRow row) => row is not null && _seen.Contains(row.Id);

        /// <summary>
        /// Records a row's new path, so a scan that relinks a move and then walks past the same
        /// path again does not treat it as new.
        /// </summary>
        public void Repath(ScanFileRow row, string newPath)
        {
            if (row is null) throw new ArgumentNullException(nameof(row));

            _byPath.Remove(PathScope.Normalise(row.FilePath));
            row.FilePath = newPath;
            _byPath[PathScope.Normalise(newPath)] = row;
        }

        /// <summary>Adds a row this scan has just inserted, so the rest of the scan can see it.</summary>
        public void Add(ScanFileRow row)
        {
            if (row is null) throw new ArgumentNullException(nameof(row));

            _byPath[PathScope.Normalise(row.FilePath)] = row;
            _seen.Add(row.Id);
        }

        /// <summary>
        /// Rows this scan did not see, that are under a folder it actually walked, and that are
        /// not already marked missing.
        ///
        /// Every clause is load-bearing. Restricting to walked roots is what stops a scan of one
        /// folder condemning another, and is the whole reason an unplugged drive costs nothing:
        /// its root was never walked, so nothing under it is a candidate. Excluding rows already
        /// marked leaves the original <c>missing_since</c> alone, so "gone since Tuesday" does not
        /// become "gone since today" on every scan after it — which is the timestamp any eventual
        /// prune has to count from.
        /// </summary>
        /// <param name="unreadableDirectories">
        /// Folders the OS refused to open during the walk. Their contents were not seen and yet
        /// nothing is known about them, so they are excluded too. macOS denies a folder outright
        /// until it has been granted, and a first scan should not answer that with a library
        /// marked missing.
        /// </param>
        public IReadOnlyList<ScanFileRow> Unseen(
            IReadOnlyCollection<string> walkedRoots,
            IReadOnlyCollection<string>? unreadableDirectories = null)
        {
            if (walkedRoots is null) throw new ArgumentNullException(nameof(walkedRoots));

            var blocked = unreadableDirectories ?? Array.Empty<string>();

            return _byPath.Values
                .Where(row => !_seen.Contains(row.Id))
                .Where(row => row.MissingSince is null)
                .Where(row => PathScope.IsUnderAny(walkedRoots, row.FilePath))
                .Where(row => blocked.Count == 0 || !PathScope.IsUnderAny(blocked, row.FilePath))
                .OrderBy(row => row.Id)
                .ToList();
        }

        /// <summary>
        /// The key two rows share when they are plausibly the same file in two places. Null when
        /// the file cannot be identified this way at all.
        /// </summary>
        private static string? IdentityOf(string? path, long? size)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (size is null or <= 0) return null;

            var name = Path.GetFileName(path);
            return string.IsNullOrEmpty(name) ? null : size.Value + "\u0000" + name;
        }
    }

    /// <summary>One row of <c>files</c> as a scan needs to see it.</summary>
    public sealed class ScanFileRow
    {
        public long Id { get; set; }
        public long? MovieId { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public long? SizeBytes { get; set; }
        public string? UpdatedAt { get; set; }
        public string? MissingSince { get; set; }

        /// <summary>
        /// True when the file on disk differs from what the catalogue recorded, or when the row
        /// says something the scan now knows to be untrue.
        ///
        /// Stamping <c>last_seen_at</c> is not a change: it is the scan writing down that it
        /// looked, and counting it would report a library where nothing happened as one where
        /// everything did. A row coming back from missing is a change, and a decisive one — it is
        /// the answer to the question the missing mark asked.
        /// </summary>
        public bool DiffersFrom(long size, string? modified, long movieId) =>
            SizeBytes != size
            || !string.Equals(UpdatedAt, modified, StringComparison.Ordinal)
            || MissingSince is not null
            || (MovieId is null && movieId > 0);
    }
}
