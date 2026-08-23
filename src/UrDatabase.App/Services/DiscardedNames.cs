using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>
    /// One catalogue row, reduced to what deciding "is this row a name somebody discarded" needs.
    /// </summary>
    public sealed class CatalogueName
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public int? Year { get; set; }

        /// <summary>
        /// The name the scanner gave this film before its title was corrected, when it has been.
        /// A row that has one is a film with a history, never debris.
        /// </summary>
        public string? ScanTitle { get; set; }

        public bool HasFiles { get; set; }
        public bool HasTmdbId { get; set; }
        public bool HasPoster { get; set; }
        public bool HasGenres { get; set; }

        /// <summary>
        /// True when the row is nothing but a title and a year: no file, no identification, no
        /// artwork, no genres, and no former name of its own. Such a row can be removed without
        /// losing anything a person or a lookup put there.
        /// </summary>
        public bool HoldsNothing =>
            !HasFiles && !HasTmdbId && !HasPoster && !HasGenres && string.IsNullOrWhiteSpace(ScanTitle);
    }

    /// <summary>
    /// Removes a catalogue row that is only another row's discarded name.
    ///
    /// Correcting a film's TMDB match renames it and keeps the scanned name in
    /// <c>movies.scan_title</c>, precisely so the next scan finds the row it already made instead
    /// of cataloguing the film again under the name on disk. That works — but it only works from
    /// the version that introduced it onwards. A catalogue scanned by anything that did not know
    /// about the alias, which includes every build from before it and any older copy of the app
    /// still pointed at the same database, gained a second row named after the file: no link to
    /// that file, since the scan leaves an existing <c>files.movie_id</c> alone, and so no way for
    /// anything afterwards to attach one. The result is a blank card that cannot be opened,
    /// played, matched or removed, sitting next to the film it is a duplicate of.
    ///
    /// Nothing in the app could clear one up. The scan only ever added, the library had no reason
    /// to hide a row it had been given, and there is no screen for editing the catalogue — so the
    /// only cure was SQL by hand, on a file the app otherwise owns entirely.
    /// </summary>
    /// <remarks>
    /// This deletes, where <see cref="MissingFilms"/> deliberately does not, and the difference is
    /// what the row is worth. A film whose file has gone still holds the only record that it was
    /// ever there, and a corrected match that has to survive the file coming back. A row matched
    /// here holds nothing at all — no file, no identification, no artwork, no genres, no former
    /// name — and the film it names is not lost, because the row that discarded that name is
    /// sitting beside it and has all of those things. There is nothing to keep and nothing to
    /// come back to.
    /// </remarks>
    public static class DiscardedNames
    {
        /// <summary>
        /// Which of <paramref name="rows"/> are another row's discarded name and hold nothing of
        /// their own.
        /// </summary>
        /// <remarks>
        /// Identity comes from <see cref="MovieIndex.BuildKey(string, int?)"/>, the same key the
        /// scanner resolves a filename through. It has to: this decides that two rows are one
        /// film, and deciding that by a rule the scanner does not share would remove a row the
        /// scanner would then create again on its next run.
        ///
        /// That key is a filename heuristic, though, and on its own it is nowhere near enough to
        /// delete on — two unrelated films genuinely can share a title and a year. So the rule
        /// asks for the whole shape of the accident rather than for a name that matches, and every
        /// one of these has to hold:
        ///
        /// <list type="bullet">
        /// <item>the candidate holds nothing — no file, no identification, no artwork, no genres,
        /// and no former name of its own, so there is provably nothing to lose;</item>
        /// <item>exactly one row claims that name as one it discarded. Two rows claiming it is an
        /// ambiguity, and an ambiguity is a reason to do nothing;</item>
        /// <item>that row was catalogued first. The debris is created by a scan that ran
        /// <em>after</em> the rename, so a candidate older than the row that renamed itself cannot
        /// be it;</item>
        /// <item>that row has the file. The whole mechanism is that the file stayed where it was
        /// and the duplicate never got one;</item>
        /// <item>and that row has a TMDB id, because the only thing in the app that discards a
        /// name is a correction, and a correction writes both together.</item>
        /// </list>
        ///
        /// Each of those costs a row staying and would otherwise cost a film. What is left matches
        /// the accident and very little else.
        /// </remarks>
        public static IReadOnlyList<long> Find(IEnumerable<CatalogueName>? rows)
        {
            var all = rows?.Where(r => r is not null).ToList();
            if (all is null || all.Count < 2) return Array.Empty<long>();

            // Every name a row says it used to be called, and which row says so — but only where
            // exactly one row says it. A name two rows have both discarded identifies neither.
            var owners = all
                .Where(r => MovieIndex.NormalizeTitle(r.ScanTitle).Length > 0)
                .GroupBy(r => MovieIndex.BuildKey(r.ScanTitle, r.Year), StringComparer.Ordinal)
                .Where(claimants => claimants.Count() == 1)
                .ToDictionary(claimants => claimants.Key, claimants => claimants.First(), StringComparer.Ordinal);

            if (owners.Count == 0) return Array.Empty<long>();

            return all
                .Where(row =>
                    row.HoldsNothing &&
                    owners.TryGetValue(MovieIndex.BuildKey(row.Title, row.Year), out var owner) &&
                    owner.Id < row.Id &&
                    owner.HasFiles &&
                    owner.HasTmdbId)
                .Select(row => row.Id)
                .OrderBy(id => id)
                .ToList();
        }

        private const string RowsSql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.scan_title AS ScanTitle,
       EXISTS (SELECT 1 FROM files WHERE files.movie_id = m.id)         AS HasFiles,
       (m.tmdb_id     IS NOT NULL)                                      AS HasTmdbId,
       (m.poster_path IS NOT NULL AND TRIM(m.poster_path) <> '')        AS HasPoster,
       (m.genres      IS NOT NULL AND TRIM(m.genres)      <> '')        AS HasGenres
FROM movies m
ORDER BY m.id";

        /// <summary>
        /// The delete, guarded by everything <see cref="Find"/> asked of the candidate.
        /// </summary>
        /// <remarks>
        /// The conditions are repeated here rather than trusted from the read above, because the
        /// read and the write are two statements and the catalogue has more than one writer. The
        /// write lane is process-local and not everything takes it —
        /// <see cref="PlayTargetResolver.LinkFile"/> does not — so a second copy of the app, or a
        /// link somebody made in between, could attach a file to a row this had already decided
        /// was empty. Re-asked as part of the delete, the worst case is a row that stays, which
        /// the next sweep looks at again.
        ///
        /// The owner-side conditions are not repeated, deliberately: they are about a different
        /// row, and nothing that happens to it can make deleting an empty duplicate wrong.
        /// </remarks>
        private const string DeleteSql = @"
DELETE FROM movies
WHERE id = @id
  AND tmdb_id    IS NULL
  AND scan_title IS NULL
  AND (poster_path IS NULL OR TRIM(poster_path) = '')
  AND (genres      IS NULL OR TRIM(genres)      = '')
  AND NOT EXISTS (SELECT 1 FROM files WHERE files.movie_id = movies.id)";

        /// <summary>
        /// Clears out whatever <see cref="Find"/> matches, and says how many rows that was.
        /// </summary>
        /// <param name="tx">
        /// The caller's transaction, when it has one. Null opens one around both the read and the
        /// delete, which have to be one transaction: judging a row on a catalogue and then writing
        /// to a later one is how a row that has just been given a file gets deleted anyway.
        /// </param>
        /// <remarks>
        /// The delete is what the <c>movies_ad</c> trigger exists for, so the full text index
        /// follows in the same transaction without being told, and <c>imdb_ratings.movie_id</c> is
        /// <c>ON DELETE SET NULL</c>, so a cached rating survives keyed by its IMDb id. No file
        /// row can be affected: a row with one is never matched, and would not pass
        /// <see cref="DeleteSql"/> if it were.
        ///
        /// <b>The caller must already hold <see cref="DatabaseWriteLane"/>.</b> This does not take
        /// it, because both callers are writing already and the lane is a plain
        /// <c>SemaphoreSlim(1, 1)</c> — asking for it a second time on the same thread does not
        /// re-enter, it waits for a turn that only that thread can give back.
        /// </remarks>
        public static async Task<int> SweepAsync(
            SqliteConnection conn,
            SqliteTransaction? tx = null,
            CancellationToken ct = default)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));

            if (tx is not null) return await SweepWithinAsync(conn, tx, ct).ConfigureAwait(false);

            using var owned = conn.BeginTransaction();
            var swept = await SweepWithinAsync(conn, owned, ct).ConfigureAwait(false);
            owned.Commit();

            return swept;
        }

        private static async Task<int> SweepWithinAsync(
            SqliteConnection conn,
            SqliteTransaction tx,
            CancellationToken ct)
        {
            var rows = await conn
                .QueryAsync<CatalogueName>(new CommandDefinition(RowsSql, transaction: tx, cancellationToken: ct))
                .ConfigureAwait(false);

            var doomed = Find(rows);
            if (doomed.Count == 0) return 0;

            return await conn
                .ExecuteAsync(new CommandDefinition(
                    DeleteSql,
                    doomed.Select(id => new { id }).ToList(),
                    transaction: tx,
                    cancellationToken: ct))
                .ConfigureAwait(false);
        }
    }
}
