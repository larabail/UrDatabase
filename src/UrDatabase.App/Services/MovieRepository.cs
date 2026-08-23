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
        private const string ListSql =
            "SELECT id AS Id, title AS Title, year AS Year, genres AS Genres, poster_path AS PosterPath " +
            "FROM movies ORDER BY COALESCE(year,0) DESC, title";

        private const string SearchSql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.genres AS Genres, m.poster_path AS PosterPath
FROM movies_fts f
JOIN movies m ON m.id = f.rowid
WHERE movies_fts MATCH @q
ORDER BY rank";

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

            // Not Database.Open: the read path has no business migrating the schema. Database.Connect
            // is still the only way a catalogue connection is built, so this query gets the same busy
            // timeout and the same WAL snapshot as every write it might be racing.
            using var conn = Database.Connect(DatabasePath);

            ct.ThrowIfCancellationRequested();

            return match is null
                ? conn.Query<UiMovie>(ListSql).ToList()
                : conn.Query<UiMovie>(SearchSql, new { q = match }).ToList();
        }
    }
}
