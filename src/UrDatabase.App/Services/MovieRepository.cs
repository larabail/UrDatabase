using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dapper;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads the local catalogue. The two queries the library view runs, the connection they run
    /// on, and nothing else.
    ///
    /// Out of the window because this is the most frequent query in the app — it used to run on
    /// the UI thread on every keystroke — and a query reachable only from a text-changed handler
    /// needs a UI thread to test. Here it is a plain object over a file, so a test opens a
    /// database in a temporary directory, writes rows into it and asserts on what comes back.
    ///
    /// Synchronous on purpose. <c>Microsoft.Data.Sqlite</c> has no asynchronous I/O underneath —
    /// its <c>*Async</c> methods run the statement on the calling thread and hand back a completed
    /// task — so an <c>await</c> here would look like it freed the UI thread while still holding
    /// it. Getting off that thread is the caller's job, and <see cref="LibraryLoader"/> does it
    /// once for the whole read rather than pretending each statement is asynchronous.
    /// </summary>
    public sealed class MovieRepository
    {
        /// <summary>
        /// Whether the catalogue still holds a file for a film, and whether it holds one a scan
        /// could not find. Two facts rather than one, because they are not opposites: a film with
        /// no file rows at all has neither, and that is an ordinary "nothing linked yet" rather
        /// than a film somebody deleted.
        ///
        /// Selected here rather than joined, so a film with several prints stays one row. The
        /// index on <c>files.movie_id</c> is what makes two correlated existence tests per film
        /// cheaper than the join and the grouping they replace.
        ///
        /// What the answers mean is deliberately not decided here; see
        /// <see cref="MissingFilms.Decide(bool, bool, bool)"/>.
        /// </summary>
        private const string FileFacts = @"
       EXISTS (SELECT 1 FROM files WHERE files.movie_id = m.id AND files.missing_since IS NULL)     AS HasFileHere,
       EXISTS (SELECT 1 FROM files WHERE files.movie_id = m.id AND files.missing_since IS NOT NULL) AS HasFileMissing";

        /// <summary>
        /// What the library's filters ask about a film. The rating is the IMDb one when the
        /// catalogue has fetched it — that is the number the details screen leads with — and
        /// TMDB's average otherwise, so a film opened once does not filter on a different number
        /// from the one it was seen wearing.
        /// </summary>
        private const string FactColumns = @"
       m.runtime_minutes AS RuntimeMinutes,
       COALESCE((SELECT r.rating FROM imdb_ratings r
                 WHERE r.movie_id = m.id AND r.rating IS NOT NULL AND r.rating > 0
                 ORDER BY r.fetched_at DESC LIMIT 1), m.rating) AS Rating,
       m.cast_list AS CastList, m.crew_list AS CrewList,
       (m.facts_fetched_at IS NOT NULL) AS FactsKnown,";

        private const string ListSql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.genres AS Genres, m.poster_path AS PosterPath, m.tmdb_id AS TmdbId,
" + FactColumns + FileFacts + @"
FROM movies m
ORDER BY COALESCE(m.year,0) DESC, m.title";

        private const string SearchSql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.genres AS Genres, m.poster_path AS PosterPath, m.tmdb_id AS TmdbId,
" + FactColumns + FileFacts + @"
FROM movies_fts f
JOIN movies m ON m.id = f.rowid
WHERE movies_fts MATCH @q
ORDER BY rank";

        /// <summary>
        /// Whether this repository has brought the catalogue up to the current schema yet. See
        /// <see cref="Query"/>.
        /// </summary>
        private int _schemaChecked;

        public MovieRepository(string? databasePath)
        {
            DatabasePath = databasePath ?? "";
        }

        /// <summary>Where the catalogue is, whether or not anything is there yet.</summary>
        public string DatabasePath { get; }

        /// <summary>
        /// False on a fresh install, where nothing has been scanned and no server has synced. The
        /// status line says so by name, so the answer has to come from the same place the query
        /// does rather than being guessed at separately.
        /// </summary>
        public bool Exists => !string.IsNullOrWhiteSpace(DatabasePath) && File.Exists(DatabasePath);

        /// <summary>
        /// The local half of the library.
        /// </summary>
        /// <param name="match">
        /// An FTS5 MATCH expression from <see cref="FtsQuery.Build"/>, or <c>null</c> to list the
        /// whole catalogue. Never raw text from the search box: FTS5 would read its punctuation as
        /// operators and fail the query.
        /// </param>
        /// <remarks>
        /// Throws whatever SQLite raises. A database written by an older schema is a real failure
        /// with a message worth showing, and swallowing it here would leave the caller unable to
        /// tell "no films" from "could not read the films".
        ///
        /// <paramref name="ct"/> is checked between steps, not during one. SQLite offers no way to
        /// interrupt a statement already running on this connection, so a cancelled read still
        /// returns its rows — which is exactly why the caller cannot rely on cancellation alone to
        /// decide whose results reach the screen.
        /// </remarks>
        public IReadOnlyList<UiMovie> Query(string? match, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // No file, no rows. A server library still has to be browsable on a machine that has
            // never scanned anything, and creating a catalogue is a scan's job, not a read's.
            if (!Exists) return Array.Empty<UiMovie>();

            // Database.Connect is the only way a catalogue connection is built, so this query gets
            // the same busy timeout and the same WAL snapshot as every write it might be racing.
            //
            // The first read through this repository migrates, every later one does not. This
            // query names columns that only Database.Migrate adds to an existing library, and on
            // an install with no Jellyfin server nothing else opens the catalogue before it: the
            // first launch after an upgrade would fail on "no such column", show an empty library,
            // and so never queue the poster fetches whose Database.Open would have repaired it.
            // Once per repository is enough — it is rebuilt with the configuration — and keeps the
            // schema check off the path every keystroke takes.
            using var conn = Volatile.Read(ref _schemaChecked) == 0
                ? Database.Open(DatabasePath)
                : Database.Connect(DatabasePath);

            // Only once it has worked: a migration that lost to a lock is tried again next read.
            Volatile.Write(ref _schemaChecked, 1);

            ct.ThrowIfCancellationRequested();

            return match is null
                ? conn.Query<UiMovie>(ListSql).ToList()
                : conn.Query<UiMovie>(SearchSql, new { q = match }).ToList();
        }
    }
}
