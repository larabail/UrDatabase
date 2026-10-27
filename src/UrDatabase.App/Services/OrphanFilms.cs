using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;

namespace UrDatabase.Services
{
    /// <summary>Why a film row with no file at all is still in the catalogue.</summary>
    public enum OrphanKeepReason
    {
        /// <summary>Somebody chose its TMDB match with Wrong film?.</summary>
        ChosenByHand,
        Rated,
        Viewed,
        OnWatchlist,
        ProblemReported,
    }

    /// <summary>
    /// A catalogued film with no file row of any kind, and what — if anything — is keeping it.
    /// </summary>
    public sealed record OrphanFilmStanding(long MovieId, IReadOnlyList<OrphanKeepReason> Reasons)
    {
        public bool IsKept => Reasons.Count > 0;
    }

    /// <summary>One <c>movies</c> row, reduced to what deciding "is this an orphan" needs.</summary>
    public sealed class OrphanCandidate
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public int? Year { get; set; }
        public long? TmdbId { get; set; }
        public string? ScanTitle { get; set; }
        public bool MatchedByHand { get; set; }

        /// <summary>
        /// Any file row at all, found or not. A file on a drive that is not plugged in is still a
        /// file row, and that film is somebody's film rather than debris.
        /// </summary>
        public bool HasFiles { get; set; }
    }

    /// <summary>Everything a person has written that names a film, as the keys it is stored under.</summary>
    public sealed class OrphanUserMarks
    {
        public ISet<string> Rated { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public ISet<string> Viewed { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public ISet<string> Watchlist { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public ISet<string> Problems { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Film rows with no file at all, and what the catalogue does about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a scan creates a film row, and it creates one to hold a file, so a row with no file
    /// row whatsoever is something left behind: an episode an earlier scan filed as a film and a
    /// later one re-filed under its programme, an empty row an older scan inserted for a cleaner
    /// name while the file stayed linked to the noisy one, or the scanner's guess somebody
    /// overruled by linking its file to a different film. Each of them is a card with nothing to
    /// play, counted among the unmatched films until somebody notices.
    /// </para>
    /// <para>
    /// This deletes, where <see cref="MissingFilms"/> deliberately does not, and the line between
    /// them is the file row. A film whose file is gone or on an unplugged drive still has one —
    /// marked missing or not — and is left entirely alone. A row with none has nothing for a
    /// returning file to come back to: the scan resolves a file by its path first, and a file
    /// re-filed as an episode is an episode.
    /// </para>
    /// <para>
    /// What does keep such a row is a person: a match they chose by hand, a rating or a viewing
    /// nothing else still shows, a place on the watchlist, a reported problem. The journal and
    /// the watchlist do not reference <c>movies</c>, so deleting the row would not delete those —
    /// it would orphan them where nothing reads them. Those rows stay, the details screen says
    /// why, and <see cref="Remove"/> lets the person take them out.
    /// </para>
    /// <para>
    /// An automatic TMDB match does not keep a row. It is the poster pass guessing from a title,
    /// and the episodes it guessed about are exactly the rows this exists to clear.
    /// </para>
    /// </remarks>
    public static class OrphanFilms
    {
        private const string RowsSql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.tmdb_id AS TmdbId, m.scan_title AS ScanTitle,
       m.matched_by_hand AS MatchedByHand,
       EXISTS (SELECT 1 FROM files WHERE files.movie_id = m.id) AS HasFiles
FROM movies m";

        /// <summary>
        /// The delete, re-asking what can be asked in SQL. The read and the delete share a
        /// transaction, so this is belt and braces against another copy of the app rather than
        /// the only guard.
        /// </summary>
        private const string DeleteSql = @"
DELETE FROM movies
WHERE id = @id
  AND matched_by_hand = 0
  AND NOT EXISTS (SELECT 1 FROM files         WHERE files.movie_id = @id)
  AND NOT EXISTS (SELECT 1 FROM watchlist     WHERE item_key = @local)
  AND NOT EXISTS (SELECT 1 FROM film_problems WHERE film_key = @local)";

        /// <summary>
        /// Whether a row's TMDB match was a person's choice.
        /// </summary>
        /// <remarks>
        /// <c>matched_by_hand</c> is written by Wrong film? from 0.35.1 on. A correction made
        /// before that left no marker, but it did leave a shape: it wrote the TMDB id and moved the
        /// scanned name into <c>scan_title</c> together. The only other thing that fills
        /// <c>scan_title</c> is the scan's own retitle, which strips release tags from a name and
        /// nothing more — so a <c>scan_title</c> that is not the title plus tags, beside a TMDB id,
        /// was a person. A correction that changed nothing but the id left no trace at all, and
        /// is read as automatic.
        /// </remarks>
        public static bool WasMatchedByHand(OrphanCandidate row)
        {
            if (row is null) return false;
            if (row.MatchedByHand) return true;
            if (row.TmdbId is null || string.IsNullOrWhiteSpace(row.ScanTitle)) return false;

            return !IsAutomaticRetitle(row.ScanTitle, row.Title);
        }

        /// <summary>True when <paramref name="scanTitle"/> is <paramref name="title"/> with release tags on it.</summary>
        private static bool IsAutomaticRetitle(string? scanTitle, string? title)
        {
            if (!FilenameParser.CarriesReleaseTag(scanTitle)) return false;

            var cleaned = MovieIndex.NormalizeTitle(FilenameParser.CleanText(scanTitle));
            return cleaned.Length > 0 && cleaned == MovieIndex.NormalizeTitle(title);
        }

        /// <summary>
        /// Every journal key a row's rating or viewings could be stored under: its TMDB key, its
        /// title key until it had one, and the title key of the name the scan gave it.
        /// </summary>
        public static IReadOnlyList<string> JournalKeys(OrphanCandidate row) =>
            new[]
                {
                    FilmKey.For(row.TmdbId is long tmdb and > 0 and <= int.MaxValue ? (int)tmdb : null, row.Title, row.Year),
                    FilmKey.ForTitle(row.Title, row.Year),
                    FilmKey.ForTitle(row.ScanTitle, row.Year),
                }
                .Where(k => k is not null)
                .Select(k => k!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// What keeps <paramref name="row"/>, in a fixed order. Empty means nothing does.
        /// </summary>
        /// <param name="sharedKeys">
        /// Journal keys a film that still has a file shows its rating and viewings under. A rating under one of those is
        /// still on screen on that film, so it is not lost with this row and does not keep it —
        /// which is what clears the empty twin of a film rated under its TMDB key.
        /// </param>
        public static IReadOnlyList<OrphanKeepReason> ReasonsToKeep(
            OrphanCandidate row,
            OrphanUserMarks marks,
            ISet<string>? sharedKeys = null)
        {
            if (row is null) throw new ArgumentNullException(nameof(row));
            if (marks is null) throw new ArgumentNullException(nameof(marks));

            var reasons = new List<OrphanKeepReason>();
            if (WasMatchedByHand(row)) reasons.Add(OrphanKeepReason.ChosenByHand);

            var keys = JournalKeys(row).Where(k => sharedKeys is null || !sharedKeys.Contains(k)).ToList();
            if (keys.Any(marks.Rated.Contains)) reasons.Add(OrphanKeepReason.Rated);
            if (keys.Any(marks.Viewed.Contains)) reasons.Add(OrphanKeepReason.Viewed);

            var local = Watchlist.LocalKey(row.Id);
            if (marks.Watchlist.Contains(local)) reasons.Add(OrphanKeepReason.OnWatchlist);
            if (marks.Problems.Contains(local)) reasons.Add(OrphanKeepReason.ProblemReported);

            return reasons;
        }

        /// <summary>Which of <paramref name="rows"/> have no file and nothing keeping them.</summary>
        public static IReadOnlyList<long> Find(IEnumerable<OrphanCandidate>? rows, OrphanUserMarks marks)
        {
            var all = rows?.Where(r => r is not null).ToList();
            if (all is null || all.Count == 0) return Array.Empty<long>();

            var shared = SharedKeys(all);

            return all
                .Where(r => !r.HasFiles && ReasonsToKeep(r, marks, shared).Count == 0)
                .Select(r => r.Id)
                .OrderBy(id => id)
                .ToList();
        }

        /// <summary>
        /// Deletes every film row <see cref="Find"/> matches, inside <paramref name="tx"/>, and
        /// says how many that was.
        /// </summary>
        /// <remarks>
        /// The <c>movies_ad</c> trigger takes each row out of the search index in the same
        /// transaction, and <c>imdb_ratings.movie_id</c> is <c>ON DELETE SET NULL</c>, so a cached
        /// rating survives under its IMDb id. Synchronous, because Microsoft.Data.Sqlite is, and
        /// because <see cref="Database.Migrate"/> runs it.
        ///
        /// <b>A caller other than the migration must already hold <see cref="DatabaseWriteLane"/>.</b>
        /// </remarks>
        public static int Sweep(SqliteConnection conn, SqliteTransaction tx)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (tx is null) throw new ArgumentNullException(nameof(tx));

            var rows = conn.Query<OrphanCandidate>(RowsSql, transaction: tx).ToList();
            if (rows.All(r => r.HasFiles)) return 0;

            var doomed = Find(rows, LoadMarks(conn, tx));
            if (doomed.Count == 0) return 0;

            // One savepoint per row, so a row that cannot be deleted costs that row and nothing
            // else. The one that can't is a row the full-text index never held — a catalogue whose
            // movies predate movies_fts, where the delete trigger's 'delete' finds nothing to take
            // out and SQLite calls the index malformed. Left alone, it is no worse than it was, and
            // this runs inside Database.Open, which must not fail over a tidy-up.
            var removed = 0;
            foreach (var id in doomed)
            {
                tx.Save("orphan");
                try
                {
                    removed += conn.Execute(DeleteSql, new { id, local = Watchlist.LocalKey(id) }, tx);
                    tx.Release("orphan");
                }
                catch (SqliteException)
                {
                    tx.Rollback("orphan");
                    tx.Release("orphan");
                }
            }

            return removed;
        }

        /// <summary>
        /// What the details screen says about a film with no file, or null when it has one — or
        /// is not in the catalogue at all.
        /// </summary>
        public static OrphanFilmStanding? Describe(SqliteConnection conn, long movieId)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (movieId <= 0) return null;

            var rows = conn.Query<OrphanCandidate>(RowsSql).ToList();
            var row = rows.FirstOrDefault(r => r.Id == movieId);
            if (row is null || row.HasFiles) return null;

            return new OrphanFilmStanding(movieId, ReasonsToKeep(row, LoadMarks(conn, null), SharedKeys(rows)));
        }

        /// <summary>
        /// Takes a film with no file out of the catalogue on a person's say-so, with its place on
        /// the watchlist and its reported problem, which name this row and nothing else. Returns
        /// false, and changes nothing, when the film has a file row — found, missing or on an
        /// unplugged drive — because that is a film, not a leftover.
        /// </summary>
        /// <remarks>
        /// The rating and the viewings stay. They are keyed by the film rather than by this row,
        /// so they come back if the film is catalogued again, and the diary is a record of having
        /// watched something, not of owning a copy of it.
        ///
        /// <b>The caller must already hold <see cref="DatabaseWriteLane"/>.</b>
        /// </remarks>
        public static bool Remove(SqliteConnection conn, long movieId)
        {
            if (conn is null) throw new ArgumentNullException(nameof(conn));
            if (movieId <= 0) return false;

            var local = Watchlist.LocalKey(movieId);

            using var tx = conn.BeginTransaction();

            var deleted = conn.Execute(
                "DELETE FROM movies WHERE id = @id AND NOT EXISTS (SELECT 1 FROM files WHERE files.movie_id = @id)",
                new { id = movieId }, tx);
            if (deleted == 0) return false;

            conn.Execute("DELETE FROM watchlist WHERE item_key = @local", new { local }, tx);
            conn.Execute("DELETE FROM film_problems WHERE film_key = @local", new { local }, tx);

            tx.Commit();
            return true;
        }

        /// <summary>The sentence the details screen shows over a film with no file.</summary>
        public static string Explain(OrphanFilmStanding? standing)
        {
            if (standing is null) return "";

            const string lead = "There is no file for this film on this computer or any drive the library knows about.";

            if (!standing.IsKept)
                return lead + " Nothing you have written refers to it, so the next scan clears it away.";

            return $"{lead} It is still here because {Join(standing.Reasons.Select(Phrase))}.";
        }

        /// <summary>The question asked before <see cref="Remove"/>.</summary>
        public static string ConfirmRemoval(string? title, OrphanFilmStanding? standing)
        {
            var name = string.IsNullOrWhiteSpace(title) ? "this film" : $"\u201c{title.Trim()}\u201d";
            var reasons = standing?.Reasons ?? Array.Empty<OrphanKeepReason>();
            var lines = new List<string> { $"Remove {name} from the library? It has no file to play." };

            if (reasons.Contains(OrphanKeepReason.Rated) || reasons.Contains(OrphanKeepReason.Viewed))
                lines.Add("Your rating and viewings are kept, and come back if the film is added again.");

            var goes = new List<string>();
            if (reasons.Contains(OrphanKeepReason.OnWatchlist)) goes.Add("its place on your Watchlist");
            if (reasons.Contains(OrphanKeepReason.ProblemReported)) goes.Add("the problem you reported");
            if (reasons.Contains(OrphanKeepReason.ChosenByHand)) goes.Add("the match you chose");
            if (goes.Count > 0) lines.Add($"This also removes {Join(goes)}.");

            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }

        private static string Phrase(OrphanKeepReason reason) => reason switch
        {
            OrphanKeepReason.ChosenByHand => "you chose its match by hand",
            OrphanKeepReason.Rated => "you rated it",
            OrphanKeepReason.Viewed => "you logged a viewing",
            OrphanKeepReason.OnWatchlist => "it is on your Watchlist",
            OrphanKeepReason.ProblemReported => "you reported a problem with it",
            _ => reason.ToString(),
        };

        private static string Join(IEnumerable<string> parts)
        {
            var list = parts.ToList();
            return list.Count switch
            {
                0 => "",
                1 => list[0],
                _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
            };
        }

        /// <summary>
        /// The journal keys a film with a file actually shows: its TMDB key, or its title key until
        /// it has one, and its current title key. Never the key of the name the scan gave it — the
        /// journal does not read under <c>scan_title</c>, so a rating kept there is not on screen
        /// on this film and must still keep whichever empty row does answer to it.
        /// </summary>
        private static ISet<string> SharedKeys(IEnumerable<OrphanCandidate> rows) =>
            rows.Where(r => r.HasFiles)
                .SelectMany(r => new[]
                {
                    FilmKey.For(r.TmdbId is long tmdb and > 0 and <= int.MaxValue ? (int)tmdb : null, r.Title, r.Year),
                    FilmKey.ForTitle(r.Title, r.Year),
                })
                .Where(k => k is not null)
                .Select(k => k!)
                .ToHashSet(StringComparer.Ordinal);

        private static OrphanUserMarks LoadMarks(SqliteConnection conn, SqliteTransaction? tx) => new()
        {
            Rated = conn.Query<string>("SELECT film_key FROM film_ratings", transaction: tx)
                .ToHashSet(StringComparer.Ordinal),
            Viewed = conn.Query<string>("SELECT DISTINCT film_key FROM film_viewings", transaction: tx)
                .ToHashSet(StringComparer.Ordinal),
            Watchlist = conn.Query<string>("SELECT item_key FROM watchlist WHERE item_key LIKE 'local:%'", transaction: tx)
                .Select(k => k.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            Problems = conn.Query<string>("SELECT film_key FROM film_problems WHERE film_key LIKE 'local:%'", transaction: tx)
                .ToHashSet(StringComparer.Ordinal),
        };
    }
}
